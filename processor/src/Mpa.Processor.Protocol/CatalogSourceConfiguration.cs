using System.Collections.ObjectModel;
using System.Text;

namespace Mpa.Processor.Protocol;

public sealed record CatalogApprovedSource(Guid Id, string AccountId, string FolderId, long Revision, bool IncludeDescendants, bool Enabled);

/// <summary>Source policies decoded only after the generation owner verifies an enrolled
/// controller's signature and selects this exact configuration revision. Credential
/// availability is observed state, not permission to add/retarget/enable a source.
/// Source policy grants neither recognition consent nor original modification.</summary>
public sealed class CatalogSourceConfiguration
{
    public const string Capability = "library.sources.v1";
    public Guid LibraryId { get; }
    public long Revision { get; }
    public CatalogWorkspace Workspace { get; }
    public ReadOnlyCollection<string> Accounts { get; }
    public ReadOnlyCollection<CatalogApprovedSource> Sources { get; }
    private readonly Dictionary<Guid, CatalogApprovedSource> _sources;
    internal CatalogSourceConfiguration(Guid library, long revision, CatalogWorkspace workspace, List<string> accounts, List<CatalogApprovedSource> sources)
    {
        LibraryId = library; Revision = revision; Workspace = workspace;
        Accounts = accounts.ToList().AsReadOnly(); Sources = sources.ToList().AsReadOnly(); _sources = sources.ToDictionary(source => source.Id);
    }
    public void ValidateCoverage(CatalogSourceCoverageSet coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (coverage.Root.LibraryId != LibraryId || coverage.Root.ConfigurationRevision != Revision || coverage.Sources.Count != _sources.Count)
            throw new ProtocolFormatException("Captured coverage does not match the enrolled approved source configuration.");
        foreach (var source in coverage.Sources)
            if (!_sources.TryGetValue(source.Id, out var approved) || source.AccountId != approved.AccountId || source.FolderId != approved.FolderId ||
                source.Revision != approved.Revision || source.IncludeDescendants != approved.IncludeDescendants || source.Enabled != approved.Enabled)
                throw new ProtocolFormatException("Captured coverage changed an approved source binding, policy or revision.");
    }
}

public sealed partial class CatalogPublicationReader
{
    public CatalogSourceConfiguration ReadSourceConfiguration(VerifiedProtocolDocument verified, Guid expectedLibrary)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (expectedLibrary == Guid.Empty) throw new ArgumentException("An enrolled library is required.", nameof(expectedLibrary));
        if (verified.LibraryId != expectedLibrary || verified.Kind != "library-source-configuration" ||
            !verified.RequiredCapabilities.Contains(CatalogSourceConfiguration.Capability, StringComparer.Ordinal))
            throw new ProtocolFormatException("An enrolled controller's verified source configuration is required.");
        using var document = Canonical(Encoding.UTF8.GetBytes(verified.CanonicalPayload)); var root = document.RootElement;
        var workspace = Property(root, "workspace");
        if (Text(workspace, "accountId") != _workspace.AccountId || Text(workspace, "folderId") != _workspace.FolderId)
            throw new ProtocolFormatException("Source configuration cannot select another metadata workspace.");
        var rows = Property(root, "accounts");
        if (rows.ValueKind != System.Text.Json.JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 128)
            throw new ProtocolFormatException("Source configuration exceeds its account bound.");
        var accounts = new List<string>(); string? priorAccount = null;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != System.Text.Json.JsonValueKind.String) throw new ProtocolFormatException("An approved account must be an opaque identity.");
            var account = row.GetString()!; CheckOpaque(account);
            if (priorAccount is not null && string.CompareOrdinal(priorAccount, account) >= 0)
                throw new ProtocolFormatException("Approved accounts must be unique and ordinally ordered.");
            accounts.Add(account); priorAccount = account;
        }
        if (!accounts.Contains(_workspace.AccountId, StringComparer.Ordinal)) throw new ProtocolFormatException("The selected workspace account requires explicit association.");
        rows = Property(root, "sources");
        if (rows.ValueKind != System.Text.Json.JsonValueKind.Array || rows.GetArrayLength() > MaximumCoverageSources)
            throw new ProtocolFormatException("Source configuration exceeds its source bound.");
        var sources = new List<CatalogApprovedSource>(); var bindings = new HashSet<(string, string)>(); Guid? previous = null;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Uuid(row, "id"); var account = Text(row, "accountId"); var folder = Text(row, "folderId");
            CheckOpaque(account); CheckOpaque(folder);
            if (!accounts.Contains(account, StringComparer.Ordinal) || folder == "root" || !bindings.Add((account, folder)) ||
                previous is not null && string.CompareOrdinal(previous.Value.ToString("D"), id.ToString("D")) >= 0)
                throw new ProtocolFormatException("Approved sources require ordered identities, associated accounts and unique canonical folder bindings.");
            sources.Add(new(id, account, folder, Decimal(row, "revision"), CoverageFlag(row, "includeDescendants"), CoverageFlag(row, "enabled"))); previous = id;
        }
        return new(expectedLibrary, Decimal(root, "configurationRevision"), _workspace, accounts, sources);
    }
}
