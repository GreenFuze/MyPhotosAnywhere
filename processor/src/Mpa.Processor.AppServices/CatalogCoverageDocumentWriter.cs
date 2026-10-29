using System.Globalization;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Canonical bounded coverage serialization. The generation builder validates
/// every page against its final captured root before admitting dependent facts.</summary>
public sealed class CatalogCoverageDocumentWriter(CatalogWorkspace workspace)
{
    private readonly CatalogWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    private readonly CatalogPublicationReader _reader = new(workspace ?? throw new ArgumentNullException(nameof(workspace)));
    public byte[] EncodePage(Guid library, long configuration, long discovery, IEnumerable<CatalogSourceCoverage> sources)
    {
        ArgumentNullException.ThrowIfNull(sources); var rows = sources.Take(17).ToArray();
        if (rows.Length is < 1 or > CatalogPublicationReader.MaximumCoveragePageSources) throw new CatalogPartitionCapacityException();
        if (rows.Any(source => source.FolderId == _workspace.FolderId))
            throw new ProtocolFormatException("Metadata workspace cannot be published as photo-source coverage.");
        return new CanonicalJson(CatalogPublicationReader.MaximumIndexBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa-source-coverage-page", major = 1, libraryId = library.ToString("D"), configurationRevision = Decimal(configuration), discoveryRevision = Decimal(discovery),
            sources = rows.OrderBy(source => source.Id.ToString("D"), StringComparer.Ordinal).Select(source => new
            {
                id = source.Id.ToString("D"), accountId = source.AccountId, folderId = source.FolderId, revision = Decimal(source.Revision), accountRevision = Decimal(source.AccountRevision),
                includeDescendants = source.IncludeDescendants, enabled = source.Enabled, connected = source.Connected, state = source.State,
                checkpoint = source.Checkpoint is null ? null : new { jobId = source.Checkpoint.JobId.ToString("D"), sourceRevision = Decimal(source.Checkpoint.SourceRevision),
                    accountRevision = Decimal(source.Checkpoint.AccountRevision), observedItems = Decimal(source.Checkpoint.ObservedItems), pendingItems = Decimal(source.Checkpoint.PendingItems),
                    failedItems = Decimal(source.Checkpoint.FailedItems), incompleteSearch = source.Checkpoint.IncompleteSearch, sha256 = source.Checkpoint.Sha256 }
            })
        }));
    }
    public byte[] EncodeRoot(Guid library, long configuration, long discovery, int sourceCount, IEnumerable<CatalogCoveragePageReference> pages)
    {
        ArgumentNullException.ThrowIfNull(pages); var captured = pages.Take(17).ToArray();
        if (captured.Length > 16) throw new CatalogPartitionCapacityException();
        var bytes = new CanonicalJson(CatalogPublicationReader.MaximumIndexBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa-source-coverage-root", major = 1, libraryId = library.ToString("D"), configurationRevision = Decimal(configuration), discoveryRevision = Decimal(discovery),
            sourceCount = Decimal(sourceCount), pages = captured.OrderBy(page => page.FirstId.ToString("D"), StringComparer.Ordinal).Select(page => new
            {
                firstId = page.FirstId.ToString("D"), lastId = page.LastId.ToString("D"), count = Decimal(page.Count),
                reference = new { accountId = page.Reference.AccountId, fileId = page.Reference.FileId, workspaceId = page.Reference.WorkspaceId,
                    bytes = Decimal(page.Reference.Bytes), sha256 = page.Reference.Sha256 }
            })
        }));
        _ = _reader.ReadCoverageRoot(bytes, library); return bytes;
    }
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
}
