using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Local scheduling hint only, not cloud availability, publication authority,
/// an incoming activation or a transferable current-read capability.</summary>
public sealed record CatalogGenerationInputs(Guid LibraryId, long ConfigurationRevision, long DiscoveryRevision, string Sha256);

/// <summary>Compare bounded coverage/progress and indexed discovery revisions without
/// enumerating photo facts or keeping a full-library subscription. Published inputs come
/// from the exact retained signed plan/outbox, never from a deleted private snapshot.
/// Every actual publication must still perform independent current reconciliation.</summary>
public sealed class CatalogGenerationInputReader(ProcessorStateDatabase database, StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));

    public CatalogGenerationInputs Current(CancellationToken cancellationToken)
    {
        var authority = new LibraryAuthorityRepository(_database, _budget).CaptureLocalPublicationAuthority();
        using var snapshot = CatalogFactsSnapshot.Open(_database);
        if (snapshot.LibraryId != authority.LibraryId || snapshot.ConfigurationRevision != authority.ConfigurationRevision) throw new StaleLibraryConfigurationException();
        return Describe(snapshot.LibraryId, snapshot.ConfigurationRevision, snapshot.DiscoveryRevision, authority.AuthoritySha256,
            snapshot.ReadCoverageSources(cancellationToken), cancellationToken);
    }

    public CatalogGenerationInputs? Published(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var journal = new CatalogGenerationCaptureRepository(_database, _budget);
        var head = journal.PublishedHead(); if (head is null) return null;
        var publication = journal.Publication(head.GenerationId) ?? throw new InvalidDataException("Published inputs lost their retained capture.");
        var capture = publication.Capture; var plan = publication.Plan ?? throw new InvalidDataException("Published inputs lost their exact plan.");
        var schema = new CatalogPublicationReader(new(capture.Scope.AccountId, capture.Scope.FolderId));
        var root = schema.ReadCoverageRoot(ReadDependency(capture.Scope, plan.SourceCoverage, "coverage-root"), capture.Scope.LibraryId);
        if (root.ConfigurationRevision != capture.Scope.ConfigurationRevision || root.DiscoveryRevision != capture.Snapshot!.DiscoveryRevision)
            throw new InvalidDataException("Published coverage changed its frozen observation.");
        var pages = new List<CatalogCoveragePage>();
        foreach (var page in root.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(schema.ReadCoveragePage(ReadDependency(capture.Scope, page.Reference, "coverage-page"), root, page));
        }
        var coverage = new CatalogSourceCoverageSet(root, pages);
        return Describe(root.LibraryId, root.ConfigurationRevision, root.DiscoveryRevision, capture.Scope.AuthoritySha256, coverage.Sources, cancellationToken);
    }

    private byte[] ReadDependency(WorkspacePublicationScope scope, CatalogObjectReference reference, string role)
    {
        using var query = _database.Connection.CreateCommand(); query.CommandText = "SELECT id FROM workspace_publication WHERE account_id=$account AND file_id=$file";
        query.Parameters.AddWithValue("$account", scope.AccountId); query.Parameters.AddWithValue("$file", reference.FileId);
        if (query.ExecuteScalar() is not string id || !Guid.TryParseExact(id, "D", out var key)) throw new InvalidDataException("Published coverage lost its original outbox identity.");
        var work = new WorkspacePublicationRepository(_database, _budget).Read(key) ?? throw new InvalidDataException("Published coverage disappeared.");
        if (work.Scope != scope || work.Role != role || work.MimeType != "application/json" || work.VerifiedVersion is null ||
            reference.AccountId != scope.AccountId || reference.WorkspaceId != scope.FolderId || reference.Bytes != work.Payload.Length || reference.Sha256 != work.Sha256)
            throw new InvalidDataException("Published scheduling inputs changed their exact retained context or bytes.");
        return work.Payload.ToArray();
    }
    private static CatalogGenerationInputs Describe(Guid libraryId, long configurationRevision, long discoveryRevision, string authority,
        IEnumerable<CatalogSourceCoverage> sources, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Frame(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        { format = "mpa-local-generation-inputs", major = 1, libraryId, configurationRevision, discoveryRevision, authority })));
        var count = 0;
        foreach (var source in sources.OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); if (++count > CatalogPublicationReader.MaximumCoverageSources) throw new InvalidDataException("Scheduling coverage exceeds its complete source bound.");
            Frame(new CanonicalJson(16384).Encode(JsonSerializer.SerializeToUtf8Bytes(source)));
        }
        token.ThrowIfCancellationRequested(); return new(libraryId, configurationRevision, discoveryRevision, Convert.ToHexStringLower(hash.GetHashAndReset()));
        void Frame(ReadOnlySpan<byte> bytes)
        { Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length); hash.AppendData(size); hash.AppendData(bytes); }
    }
}
