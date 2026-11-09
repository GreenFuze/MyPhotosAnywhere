using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical evidence of a separately reconciled credential attempt. This
/// never replaces the original capture scope, creates an upload grant or proves current
/// remote availability. Every later operation still needs its own private current owner.</summary>
public sealed record CatalogGenerationAccessAttempt(Guid Id, WorkspacePublicationScope Scope, Guid SnapshotId, Guid CommitIntentId,
    long AccountRevision, LibraryControllerPin Controller, string PublisherKeyId, WorkspaceInventoryCheckpoint Observation,
    string SigningInventorySha256, string PublicationInventorySha256);

public sealed partial class CatalogGenerationCaptureRepository
{
    public const int MaximumAccessAttemptsPerGeneration = 64;

    /// <summary>Called only by the concrete joined reconciler after actual access,
    /// native identity and complete history checks. The local transaction independently
    /// fences current semantic policy, the completed scan and both complete inventories.
    /// Recording evidence deliberately does not relax any original-scope write check.</summary>
    public CatalogGenerationAccessAttempt RecordRenewedAccess(Guid generationId, LocalCatalogPublicationAuthority current,
        WorkspaceInventoryCheckpoint checkpoint, ProtocolSigningInventory signing, WorkspacePublicationInventory publications)
    {
        Id(generationId); ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(signing); ArgumentNullException.ThrowIfNull(publications);
        using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        if (capture.Phase is "abandoned" or "retired" or "snapshot-retiring" or "complete") throw new CatalogGenerationCaptureConflictException();
        var authority = new LibraryAuthorityRepository(_database, _budget);
        var actual = authority.ReadLocalPublicationAuthority(transaction);
        var scope = capture.Scope;
        if (actual != current || current.AccountRevision <= scope.AccountRevision ||
            scope.LibraryId != current.LibraryId || scope.ActorId != current.ActorId || scope.ActorEpoch != current.ActorEpoch ||
            scope.AccountId != current.AccountId || scope.FolderId != current.FolderId || scope.ConfigurationRevision != current.ConfigurationRevision ||
            scope.AuthoritySha256 != current.AuthoritySha256 || checkpoint.Context.LibraryId != scope.LibraryId ||
            checkpoint.Context.AccountId != scope.AccountId || checkpoint.Context.FolderId != scope.FolderId ||
            checkpoint.Context.AccountRevision != current.AccountRevision || publications.Context != checkpoint.Context || signing.LibraryId != scope.LibraryId)
            throw new StaleLibraryConfigurationException();
        new WorkspaceObservationRepository(_database, _budget).AssertCompletedScan(checkpoint, null, transaction);
        new ProtocolSigningJournal(_database, _budget).AssertInventoryUnchanged(signing.LibraryId, signing.Sha256, transaction);
        new WorkspacePublicationRepository(_database, _budget).AssertInventoryUnchanged(publications, transaction);
        var old = ReadAccessAttempt(capture, current.AccountRevision, transaction);
        if (old is not null) { transaction.Commit(); return old; } // Never rewrite the first completed attempt's provenance.
        using (var count = Command(transaction, "SELECT count(*) FROM catalog_generation_access_attempt WHERE generation_id=$id", ("$id", generationId.ToString("D"))))
            if ((long)count.ExecuteScalar()! >= MaximumAccessAttemptsPerGeneration) throw new InvalidDataException("Renewed access history exceeds its complete recovery bound; preserve it for review.");
        var pin = authority.Controller(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        var result = new CatalogGenerationAccessAttempt(Guid.NewGuid(), scope, capture.SnapshotId, capture.CommitIntentId,
            current.AccountRevision, pin, current.PublisherKeyId, checkpoint, signing.Sha256, publications.Sha256);
        var encoded = EncodeAccessAttempt(result); using var storage = _budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded) * 3L + 65536));
        storage.RecheckBeforeWrite(storage.Bytes);
        using (var insert = Command(transaction, """
            INSERT INTO catalog_generation_access_attempt(id,generation_id,account_revision,document,document_hash)
            VALUES($id,$generation,$revision,$document,$hash)
            """, ("$id", result.Id.ToString("D")), ("$generation", generationId.ToString("D")), ("$revision", current.AccountRevision),
            ("$document", encoded), ("$hash", AccessAttemptHash(capture, encoded))))
            if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Renewed access did not retain exactly one original attempt.");
        transaction.Commit(); return result;
    }

    /// <summary>Historical journal read, including after another reconnect or disconnect.
    /// A record returned here never authorizes current I/O, signing or completion.</summary>
    public CatalogGenerationAccessAttempt? AccessAttempt(Guid generationId, long accountRevision)
    {
        Id(generationId); ArgumentOutOfRangeException.ThrowIfLessThan(accountRevision, 1L);
        using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        var result = ReadAccessAttempt(capture, accountRevision, transaction); transaction.Commit(); return result;
    }

    private CatalogGenerationAccessAttempt? ReadAccessAttempt(CatalogGenerationCapture capture, long revision, SqliteTransaction? transaction)
    {
        string id, encoded, digest;
        using (var query = Command(transaction, """
            SELECT id,length(document),document,document_hash FROM catalog_generation_access_attempt
            WHERE generation_id=$generation AND account_revision=$revision
            """, ("$generation", capture.Scope.GenerationId.ToString("D")), ("$revision", revision)))
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            id = reader.GetString(0); if (reader.GetInt64(1) is < 1 or > 32768) throw new InvalidDataException("Renewed access record exceeds its recovery bound.");
            encoded = reader.GetString(2); digest = reader.GetString(3);
        }
        try
        {
            var result = JsonSerializer.Deserialize<CatalogGenerationAccessAttempt>(encoded) ?? throw new InvalidDataException("Renewed access record is missing.");
            var context = result.Observation?.Context; var pin = result.Controller;
            if (result.Id != ParseId(id) || result.Scope != capture.Scope || result.SnapshotId != capture.SnapshotId || result.CommitIntentId != capture.CommitIntentId ||
                result.AccountRevision != revision || revision <= capture.Scope.AccountRevision || context is null || context.LibraryId != capture.Scope.LibraryId ||
                context.AccountId != capture.Scope.AccountId || context.FolderId != capture.Scope.FolderId || context.AccountRevision != revision ||
                result.Observation!.Revision < 1 || result.Observation.Documents is < 0 or > WorkspaceObservationRepository.MaximumDocuments ||
                result.Observation.Cursor is not { Length: > 0 and <= 4096 } || result.Observation.Cursor.Any(char.IsControl) ||
                pin is null || pin.LibraryId != capture.Scope.LibraryId || pin.AccountId != capture.Scope.AccountId || pin.FolderId != capture.Scope.FolderId ||
                pin.ActorId == Guid.Empty || pin.Epoch < 1 || pin.PublicKeySpki is not { Length: > 0 and <= 684 } || !Digest(pin.KeyId) ||
                !Digest(result.PublisherKeyId) || !Digest(result.Observation.InventorySha256) || !Digest(result.SigningInventorySha256) ||
                !Digest(result.PublicationInventorySha256) || EncodeAccessAttempt(result) != encoded || AccessAttemptHash(capture, encoded) != digest)
                throw new InvalidDataException("Renewed access changed its original capture, identity, revision or observation.");
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException)
        { throw new InvalidDataException("Renewed access cannot be decoded safely; preserve the original capture.", error); }
    }

    private static string EncodeAccessAttempt(CatalogGenerationAccessAttempt value) => Encoding.UTF8.GetString(new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string AccessAttemptHash(CatalogGenerationCapture capture, string encoded) => Hash(new CanonicalJson(49152).Encode(JsonSerializer.SerializeToUtf8Bytes(new
    { capture.Scope, capture.SnapshotId, capture.CommitIntentId, encoded })));
}
