using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Borrowed create-only metadata sink. Its production adapter must journal exact
/// bytes/scope before writes, fence authority, verify real Drive parent/hash/size and join
/// actual requests through the captured account owner. A reference alone is not a receipt.</summary>
public interface ICatalogGenerationObjectSink
{
    Task<CatalogObjectReference> StoreAsync(string role, string mimeType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}
public sealed record PreparedCatalogDependencies(Guid LibraryId, long ConfigurationRevision, long DiscoveryRevision, long Assets,
    string SourcePolicySha256, CatalogObjectReference RootIndex, CatalogObjectReference SourceCoverage);

/// <summary>Build only dependencies from the frozen observation, never a signed commit,
/// discovery pointer or active generation. Partial writes remain unreferenced if canceled
/// or failed. The caller still owns current publisher/source fences, restore/high-water
/// reconciliation, predecessor verification and final commit admission.</summary>
public sealed class CatalogGenerationDependencyBuilder(CatalogWorkspace workspace, ICatalogGenerationObjectSink sink,
    int objectBudget = CatalogFactsTraversal.MaximumObjectBudget)
{
    private readonly CatalogWorkspace _workspace = workspace is not null && workspace.FolderId != "root" ? workspace : throw new ArgumentException("A selected canonical metadata folder is required.", nameof(workspace));
    private readonly ICatalogGenerationObjectSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly int _budget = objectBudget is >= 1 and <= CatalogFactsTraversal.MaximumObjectBudget ? objectBudget : throw new ArgumentOutOfRangeException(nameof(objectBudget));

    public async Task<PreparedCatalogDependencies> BuildAsync(FrozenCatalogSnapshot frozen, AcceptedSourceConfiguration approved,
        CatalogObjectReference? previouslyVerifiedParent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frozen); ArgumentNullException.ThrowIfNull(approved); cancellationToken.ThrowIfCancellationRequested();
        ValidateApprovedSources(frozen, approved);
        var objects = new HashSet<string>(StringComparer.Ordinal);
        if (previouslyVerifiedParent is not null) Reserve(previouslyVerifiedParent);
        var schema = new CatalogPublicationReader(_workspace); var coverageWriter = new CatalogCoverageDocumentWriter(_workspace);
        var pages = new List<(CatalogCoveragePageReference Reference, byte[] Bytes)>();
        var sources = frozen.Coverage.OrderBy(source => source.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        foreach (var group in sources.Chunk(CatalogPublicationReader.MaximumCoveragePageSources))
        {
            var bytes = coverageWriter.EncodePage(frozen.LibraryId, frozen.ConfigurationRevision, frozen.DiscoveryRevision, group);
            var reference = await StoreAsync("coverage-page", "application/json", bytes).ConfigureAwait(false);
            pages.Add((new(group[0].Id, group[^1].Id, group.Length, reference), bytes));
        }
        var rootBytes = coverageWriter.EncodeRoot(frozen.LibraryId, frozen.ConfigurationRevision, frozen.DiscoveryRevision, sources.Length, pages.Select(page => page.Reference));
        var root = schema.ReadCoverageRoot(rootBytes, frozen.LibraryId);
        var coverage = new CatalogSourceCoverageSet(root, pages.Select(page => schema.ReadCoveragePage(page.Bytes, root, page.Reference)));
        approved.Configuration.ValidateCoverage(coverage);
        var coverageReference = await StoreAsync("coverage-root", "application/json", rootBytes).ConfigureAwait(false);
        var leaves = new List<Leaf>(); var prefixes = new HashSet<string>(StringComparer.Ordinal); var codec = new DriveCatalogFacts(frozen.LibraryId);
        var decoder = new CatalogChunkDecoder(); long observed = 0;
        foreach (var chunk in new CatalogFactsPartitioner(frozen, objectBudget: _budget).ReadChunks(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!prefixes.Add(chunk.Prefix)) throw new InvalidDataException("Prepared facts repeated an identity partition.");
            var expanded = await decoder.DecodeAsync(chunk.Compressed, new(chunk.Compressed.Length, chunk.CompressedSha256, "chunk.json.gz"),
                new(chunk.ExpandedBytes, chunk.ExpandedSha256, "chunk.json"), cancellationToken).ConfigureAwait(false);
            var facts = codec.Read(expanded, chunk.Prefix);
            foreach (var asset in facts.Assets) coverage.ValidateAsset(asset);
            if (facts.Assets.Count != chunk.Assets) throw new InvalidDataException("Prepared chunk changed its photo count.");
            var reference = await StoreAsync("facts-chunk", "application/gzip", chunk.Compressed).ConfigureAwait(false);
            leaves.Add(new(chunk.Prefix, new(reference, chunk.ExpandedBytes, chunk.ExpandedSha256))); observed = checked(observed + chunk.Assets);
        }
        if (observed != frozen.Count) throw new InvalidDataException("Prepared generation lost captured photos.");
        var index = await IndexAsync("", leaves).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        return new(frozen.LibraryId, frozen.ConfigurationRevision, frozen.DiscoveryRevision, observed, approved.EnvelopeSha256, index, coverageReference);

        async Task<CatalogObjectReference> IndexAsync(string prefix, IEnumerable<Leaf> entries)
        {
            var children = new List<CatalogIndexChild>();
            foreach (var group in entries.GroupBy(leaf => leaf.Prefix[..(prefix.Length + 2)], StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested(); var captured = group.ToArray();
                var exact = captured.Where(leaf => leaf.Prefix == group.Key).ToArray();
                if (exact.Length != 0)
                {
                    if (exact.Length != 1 || captured.Length != 1) throw new InvalidDataException("Prepared partitions overlap at an index edge.");
                    children.Add(new(group.Key[^2..], exact[0].Reference.Encoded, exact[0].Reference));
                }
                else children.Add(new(group.Key[^2..], await IndexAsync(group.Key, captured).ConfigureAwait(false), null));
            }
            return await StoreAsync("facts-index", "application/json", new CatalogIndexDocumentWriter(_workspace).Encode(prefix, children)).ConfigureAwait(false);
        }
        async Task<CatalogObjectReference> StoreAsync(string role, string mime, ReadOnlyMemory<byte> bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (objects.Count >= _budget) throw new CatalogTraversalLimitException();
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            var result = await _sink.StoreAsync(role, mime, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); ValidateReference(result);
            if (result.Bytes != bytes.Length || result.Sha256 != expectedHash || Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) != expectedHash)
                throw new InvalidDataException("Metadata sink changed the captured immutable bytes.");
            Reserve(result); return result;
        }
        void Reserve(CatalogObjectReference reference)
        {
            ValidateReference(reference);
            if (!objects.Add(reference.FileId)) throw new InvalidDataException("Generation roles or predecessors alias a metadata object.");
            if (objects.Count > _budget) throw new CatalogTraversalLimitException();
        }
    }
    private void ValidateReference(CatalogObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.AccountId != _workspace.AccountId || reference.WorkspaceId != _workspace.FolderId || reference.FileId is not { Length: > 0 and <= 256 } ||
            reference.FileId == _workspace.FolderId || reference.FileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            reference.Bytes is < 1 or > WorkspacePublicationRepository.MaximumPayloadBytes || reference.Sha256 is not { Length: 64 } ||
            reference.Sha256.Any(character => character is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw new InvalidDataException("Generation reference is outside the captured metadata workspace or bounds.");
    }
    private void ValidateApprovedSources(FrozenCatalogSnapshot frozen, AcceptedSourceConfiguration approved)
    {
        var policy = approved.Configuration;
        if (policy.LibraryId != frozen.LibraryId || policy.Revision != frozen.ConfigurationRevision || policy.Workspace != _workspace ||
            policy.Sources.Count != frozen.Coverage.Count) throw new StaleLibraryConfigurationException();
        var sources = policy.Sources.ToDictionary(source => source.Id);
        foreach (var captured in frozen.Coverage)
            if (!sources.TryGetValue(captured.Id, out var source) || source.AccountId != captured.AccountId || source.FolderId != captured.FolderId ||
                source.Revision != captured.Revision || source.IncludeDescendants != captured.IncludeDescendants || source.Enabled != captured.Enabled)
                throw new StaleLibraryConfigurationException();
    }
    private sealed record Leaf(string Prefix, CatalogChunkReference Reference);
}
