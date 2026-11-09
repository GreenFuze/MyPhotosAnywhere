using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public sealed record CatalogCoveragePageReference(Guid FirstId, Guid LastId, int Count, CatalogObjectReference Reference);
public sealed record CatalogCoverageRoot(Guid LibraryId, long ConfigurationRevision, long DiscoveryRevision, int SourceCount,
    ReadOnlyCollection<CatalogCoveragePageReference> Pages);
public sealed record CatalogScanCheckpoint(Guid JobId, long SourceRevision, long AccountRevision, long ObservedItems,
    long PendingItems, long FailedItems, bool IncompleteSearch, string Sha256);
public sealed record CatalogSourceCoverage(Guid Id, string AccountId, string FolderId, long Revision, long AccountRevision,
    bool IncludeDescendants, bool Enabled, bool Connected, string State, CatalogScanCheckpoint? Checkpoint);
public sealed record CatalogCoveragePage(CatalogCoveragePageReference Descriptor, ReadOnlyCollection<CatalogSourceCoverage> Sources);

public sealed partial class CatalogPublicationReader
{
    public const int MaximumCoverageSources = 256;
    public const int MaximumCoveragePageSources = 16;
    private static readonly HashSet<string> CoverageStates = new(StringComparer.Ordinal)
        { "complete", "scanning", "partial", "pending", "access-lost", "offline", "disconnected", "disabled" };

    public CatalogCoverageRoot ReadCoverageRoot(ReadOnlySpan<byte> canonicalBytes, Guid library)
    {
        if (library == Guid.Empty) throw new ArgumentException("An established library is required.", nameof(library));
        using var document = Canonical(canonicalBytes); var root = document.RootElement;
        CoverageHeader(root, "mpa-source-coverage-root", library);
        var total = Decimal(root, "sourceCount", allowZero: true);
        var rows = Property(root, "pages");
        if (total > MaximumCoverageSources || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 16)
            throw new ProtocolFormatException("Source coverage exceeds its bounded library/page count.");
        var pages = new List<CatalogCoveragePageReference>(); var ids = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        Guid? previous = null;
        foreach (var row in rows.EnumerateArray())
        {
            var first = Uuid(row, "firstId"); var last = Uuid(row, "lastId"); var size = Decimal(row, "count");
            var reference = Reference(Property(row, "reference"), MaximumIndexBytes);
            if (size > MaximumCoveragePageSources || string.CompareOrdinal(first.ToString("D"), last.ToString("D")) > 0 ||
                (size == 1) != (first == last) || previous is not null && string.CompareOrdinal(previous.Value.ToString("D"), first.ToString("D")) >= 0 ||
                !ids.Add(reference.FileId)) throw new ProtocolFormatException("Source coverage pages overlap, alias or exceed their range/count bounds.");
            pages.Add(new(first, last, (int)size, reference)); count += (int)size; previous = last;
        }
        if (count != total) throw new ProtocolFormatException("Source coverage pages do not account for the declared source count.");
        return new(library, Decimal(root, "configurationRevision"), Decimal(root, "discoveryRevision"), count, pages.AsReadOnly());
    }

    public CatalogCoveragePage ReadCoveragePage(ReadOnlySpan<byte> canonicalBytes, CatalogCoverageRoot root, CatalogCoveragePageReference descriptor)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(descriptor);
        if (!root.Pages.Contains(descriptor) || descriptor.Reference.AccountId != _workspace.AccountId || descriptor.Reference.WorkspaceId != _workspace.FolderId)
            throw new ProtocolFormatException("Source page was not captured by this coverage root and selected workspace.");
        if (canonicalBytes.Length > MaximumIndexBytes || canonicalBytes.Length != descriptor.Reference.Bytes ||
            Convert.ToHexStringLower(SHA256.HashData(canonicalBytes)) != descriptor.Reference.Sha256)
            throw new ProtocolFormatException("Source coverage page does not match its captured immutable bytes.");
        using var document = Canonical(canonicalBytes); var value = document.RootElement;
        CoverageHeader(value, "mpa-source-coverage-page", root.LibraryId);
        if (Decimal(value, "configurationRevision") != root.ConfigurationRevision || Decimal(value, "discoveryRevision") != root.DiscoveryRevision)
            throw new ProtocolFormatException("Source coverage page revisions do not match the captured root.");
        var rows = Property(value, "sources");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > MaximumCoveragePageSources || rows.GetArrayLength() != descriptor.Count)
            throw new ProtocolFormatException("Source coverage page does not match its captured count.");
        var sources = new List<CatalogSourceCoverage>(); var bindings = new HashSet<(string, string)>(); Guid? previous = null;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Uuid(row, "id"); var account = Text(row, "accountId"); var folder = Text(row, "folderId");
            CheckOpaque(account); CheckOpaque(folder);
            if (folder == "root" || !bindings.Add((account, folder)) || previous is not null && string.CompareOrdinal(previous.Value.ToString("D"), id.ToString("D")) >= 0)
                throw new ProtocolFormatException("Source coverage requires unique ordered identities and canonical bindings.");
            var revision = Decimal(row, "revision"); var accountRevision = Decimal(row, "accountRevision");
            var descendants = CoverageFlag(row, "includeDescendants"); var enabled = CoverageFlag(row, "enabled"); var connected = CoverageFlag(row, "connected");
            var state = Text(row, "state");
            if (!CoverageStates.Contains(state) || !enabled && state != "disabled" || enabled && !connected && state != "disconnected" ||
                enabled && connected && (state is "disabled" or "disconnected"))
                throw new ProtocolFormatException("Source coverage state contradicts its captured policy or account availability.");
            var checkpointValue = Property(row, "checkpoint"); CatalogScanCheckpoint? checkpoint = null;
            if (checkpointValue.ValueKind != JsonValueKind.Null)
            {
                checkpoint = new(Uuid(checkpointValue, "jobId"), Decimal(checkpointValue, "sourceRevision"), Decimal(checkpointValue, "accountRevision"),
                    Decimal(checkpointValue, "observedItems", true), Decimal(checkpointValue, "pendingItems", true), Decimal(checkpointValue, "failedItems", true),
                    CoverageFlag(checkpointValue, "incompleteSearch"), Digest(checkpointValue, "sha256"));
                if (checkpoint.SourceRevision > revision || checkpoint.AccountRevision > accountRevision)
                    throw new ProtocolFormatException("Scan checkpoint comes from a future source/account revision.");
            }
            if ((state is "complete" or "scanning" or "partial") && checkpoint is null)
                throw new ProtocolFormatException("Observed scan states require their captured checkpoint.");
            if (state == "complete" && (checkpoint!.SourceRevision != revision || checkpoint.AccountRevision != accountRevision ||
                checkpoint.PendingItems != 0 || checkpoint.FailedItems != 0 || checkpoint.IncompleteSearch))
                throw new ProtocolFormatException("An incomplete or stale scan cannot claim complete source coverage.");
            if (state == "partial" && checkpoint!.PendingItems == 0 && checkpoint.FailedItems == 0 && !checkpoint.IncompleteSearch)
                throw new ProtocolFormatException("Partial scan coverage requires outstanding work or incomplete observations.");
            sources.Add(new(id, account, folder, revision, accountRevision, descendants, enabled, connected, state, checkpoint)); previous = id;
        }
        if (sources[0].Id != descriptor.FirstId || sources[^1].Id != descriptor.LastId)
            throw new ProtocolFormatException("Source coverage page does not match its captured identity range.");
        return new(descriptor, sources.AsReadOnly());
    }
    private static void CoverageHeader(JsonElement root, string format, Guid library)
    {
        var major = Property(root, "major");
        if (Text(root, "format") != format || major.ValueKind != JsonValueKind.Number || !major.TryGetInt32(out var version) || version != 1)
            throw new ProtocolCompatibilityException("Source coverage requires a compatible MPA update.");
        if (Uuid(root, "libraryId") != library) throw new ProtocolFormatException("Source coverage belongs to another library.");
    }
    private static bool CoverageFlag(JsonElement root, string name) => Property(root, name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new ProtocolFormatException("Source coverage flag must be boolean: " + name) };
}

/// <summary>Bounded complete coverage mapping. Actual reference hashes/Drive scope, signed
/// ancestry/authority, agreement with the approved configuration, and transactional
/// activation are separate generation-owner checks.</summary>
public sealed class CatalogSourceCoverageSet
{
    private readonly Dictionary<Guid, CatalogSourceCoverage> _sources = [];
    public CatalogCoverageRoot Root { get; }
    public ReadOnlyCollection<CatalogSourceCoverage> Sources { get; }
    public CatalogSourceCoverageSet(CatalogCoverageRoot root, IEnumerable<CatalogCoveragePage> pages)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root)); ArgumentNullException.ThrowIfNull(pages);
        if (root.SourceCount is < 0 or > CatalogPublicationReader.MaximumCoverageSources || root.Pages.Count > 16)
            throw new ProtocolFormatException("Coverage root exceeds its bounded source/page count.");
        var seen = new HashSet<string>(StringComparer.Ordinal); var bindings = new HashSet<(string, string)>();
        foreach (var page in pages)
        {
            if (!root.Pages.Contains(page.Descriptor) || !seen.Add(page.Descriptor.Reference.FileId) || seen.Count > 16)
                throw new ProtocolFormatException("Coverage pages are missing, duplicated or outside the captured root.");
            if (page.Sources.Count is < 1 or > CatalogPublicationReader.MaximumCoveragePageSources || page.Sources.Count != page.Descriptor.Count ||
                page.Sources[0].Id != page.Descriptor.FirstId || page.Sources[^1].Id != page.Descriptor.LastId)
                throw new ProtocolFormatException("Coverage page does not preserve its validated count/range.");
            foreach (var source in page.Sources)
                if (!_sources.TryAdd(source.Id, source) || !bindings.Add((source.AccountId, source.FolderId)) || _sources.Count > CatalogPublicationReader.MaximumCoverageSources)
                    throw new ProtocolFormatException("Source coverage repeats an identity/binding or exceeds its bound.");
        }
        if (seen.Count != root.Pages.Count || _sources.Count != root.SourceCount)
            throw new ProtocolFormatException("All declared source coverage pages must validate before accepting facts.");
        Sources = _sources.Values.OrderBy(source => source.Id.ToString("D"), StringComparer.Ordinal).ToList().AsReadOnly();
    }
    public void ValidateAsset(DriveCatalogAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Sources.Count == 0) throw new ProtocolFormatException("Asset requires a captured source membership.");
        foreach (var membership in asset.Sources)
            if (!_sources.TryGetValue(membership.Id, out var source) || source.AccountId != asset.AccountId ||
                source.Revision != membership.Revision || !source.Enabled || membership.Depth is < 0 or > 256 || !source.IncludeDescendants && membership.Depth != 0)
                throw new ProtocolFormatException("Asset membership contradicts its captured source coverage.");
    }
}
