using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Immutable historical publication evidence, never current cloud authority or
/// permission to mutate originals. The application owner separately verifies the entire
/// actual signed head/graph and holds its private current proof across these checkpoints.</summary>
public sealed record CatalogGenerationVerification(Guid GenerationId, WorkspacePublicationScope Scope, Guid SnapshotId, Guid CommitIntentId,
    long Sequence, long Assets, WorkspaceInventoryCheckpoint Observation, WorkspaceDocumentObservation Commit);
public sealed record PublishedCatalogHead(Guid GenerationId, long Sequence, long Assets, CatalogObjectReference Commit);

public sealed partial class CatalogGenerationCaptureRepository
{
    private const string PublishedHeadKey = "catalog_published_head_v1";
    private sealed record HeadPointer(Guid GenerationId, string VerificationSha256);

    public CatalogGenerationVerification? Verification(Guid generationId)
    {
        Id(generationId); using var transaction=_database.Connection.BeginTransaction();var capture=Read(generationId,transaction);
        var result=capture is null ? null : ReadVerification(capture,transaction);transaction.Commit();return result;
    }

    /// <summary>The joined concrete owner calls this only after current full graph and
    /// ancestry verification. Independently checks the exact retained signature/outbox,
    /// completed actual observation and original scope inside the checkpoint transaction.</summary>
    public CatalogGenerationVerification ConfirmCurrentCommitVerified(Guid generationId, WorkspaceInventoryCheckpoint checkpoint, CatalogObjectReference committed,
        CatalogGenerationLocalAccess? localAccess = null)
    {
        Id(generationId); ArgumentNullException.ThrowIfNull(checkpoint); ArgumentNullException.ThrowIfNull(committed);
        using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        if (capture.Phase is not ("commit-sealed" or "commit-verified" or "complete")) throw new CatalogGenerationCaptureConflictException();
        var actual = CurrentVerification(capture, checkpoint, committed, transaction, localAccess);
        var retained = ReadVerification(capture, transaction);
        if (retained is not null) { transaction.Commit(); return retained; }
        if (capture.Phase != "commit-sealed") throw new InvalidDataException("Verified generation lost its original receipt.");
        var encoded = EncodeVerification(actual); var publication = ReadPublication(capture, transaction)!;
        using var storage = _budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded) * 3L + 65536)); storage.RecheckBeforeWrite(storage.Bytes);
        using (var insert = Command(transaction, "INSERT INTO catalog_generation_verification(id,document,document_hash) VALUES($id,$document,$hash)",
            ("$id", generationId.ToString("D")), ("$document", encoded), ("$hash", VerificationHash(capture, publication, encoded))))
            if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Verification lost its original generation owner.");
        ChangePhase(capture, "commit-verified", transaction); transaction.Commit(); return actual;
    }

    /// <summary>Atomically completes this publisher's exact head and generation. This
    /// is not independent-client catalog activation. A prior receipt is historical only;
    /// current local policy, completed scan and every generation object are rechecked.</summary>
    public PublishedCatalogHead CompleteCurrentGeneration(Guid generationId, WorkspaceInventoryCheckpoint checkpoint, CatalogObjectReference committed,
        CatalogGenerationLocalAccess? localAccess = null)
    {
        Id(generationId); using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        if (capture.Phase is not ("commit-verified" or "complete")) throw new CatalogGenerationCaptureConflictException();
        _ = CurrentVerification(capture, checkpoint, committed, transaction, localAccess);
        var receipt = ReadVerification(capture, transaction) ?? throw new InvalidDataException("Completion lost its original verification.");
        var publication = ReadPublication(capture, transaction)!; var previous = ReadPublishedHead(transaction);
        if (capture.Phase == "complete")
        {
            if (previous?.GenerationId != generationId) throw new InvalidDataException("Completed generation lost its publisher head.");
            transaction.Commit(); return previous;
        }
        if (previous is not null && (previous.GenerationId == generationId || previous.Sequence != publication.Basis.ParentSequence ||
            publication.Basis.Parent is not { } parent || previous.Commit.AccountId != parent.AccountId || previous.Commit.WorkspaceId != parent.WorkspaceId ||
            previous.Commit.Bytes != parent.Bytes || previous.Commit.Sha256 != parent.Sha256)) throw new CatalogGenerationCaptureConflictException();
        var text = EncodeHead(new(generationId, VerificationHash(capture, publication, EncodeVerification(receipt))));
        using var storage = _budget.Reserve(65536); storage.RecheckBeforeWrite(storage.Bytes);
        using (var update = Command(transaction, "INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$key", PublishedHeadKey), ("$value", text)))
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Publisher completion did not install exactly one head receipt.");
        ChangePhase(capture, "complete", transaction); transaction.Commit(); return Head(receipt);
    }

    public PublishedCatalogHead? PublishedHead()
    {
        using var transaction = _database.Connection.BeginTransaction(); var result = ReadPublishedHead(transaction); transaction.Commit(); return result;
    }

    /// <summary>Private completed metadata-copy retirement only. No credential, media
    /// path or original mutation is involved. Keep its exclusive claim until native
    /// deletion/absence has been joined and confirmed through the snapshot-store owner.</summary>
    public CatalogGenerationCapture BeginSnapshotRetirement(Guid generationId)
    {
        Id(generationId); using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        if (capture.Phase is not ("complete" or "snapshot-retiring") || ReadCurrentSupersession(capture,transaction,CancellationToken.None) is not null || ReadUnsignedCurrentDisposition(capture,transaction,CancellationToken.None) is not null)
            throw new CatalogGenerationCaptureConflictException();
        if (ReadPublishedHead(transaction)?.GenerationId != generationId || ReadVerification(capture, transaction) is null && ReadCurrentGenerationVerification(capture,transaction) is null)
            throw new InvalidDataException("Snapshot retirement lost its completed publication owner.");
        using var storage = capture.Phase == "complete" ? _budget.Reserve(65536) : null;
        if (capture.Phase == "complete")
        {
            storage!.RecheckBeforeWrite(storage.Bytes); ChangePhase(capture, "snapshot-retiring", transaction);
        }
        storage?.RecheckBeforeWrite(storage.Bytes); transaction.Commit(); return capture with { Phase = "snapshot-retiring" };
    }

    public CatalogGenerationCapture ConfirmSnapshotRetired(CatalogSnapshotRetirement actual)
    {
        ArgumentNullException.ThrowIfNull(actual); using var transaction = _database.Connection.BeginTransaction();
        var capture = RequiredCapture(actual.Capture.Scope.GenerationId, transaction); actual.AssertOwned(capture);
        if (capture.Phase != "snapshot-retiring" || ReadCurrentSupersession(capture,transaction,CancellationToken.None) is not null ||
            ReadUnsignedCurrentDisposition(capture,transaction,CancellationToken.None) is not null ||
            ReadPublishedHead(transaction)?.GenerationId != capture.Scope.GenerationId)
            throw new CatalogGenerationCaptureConflictException();
        using var storage = _budget.Reserve(65536); storage.RecheckBeforeWrite(storage.Bytes);
        ChangePhase(capture, "retired", transaction); transaction.Commit(); actual.Confirm(); return capture with { Phase = "retired" };
    }

    private CatalogGenerationVerification CurrentVerification(CatalogGenerationCapture capture, WorkspaceInventoryCheckpoint checkpoint, CatalogObjectReference committed,
        SqliteTransaction transaction, CatalogGenerationLocalAccess? localAccess)
    {
        new LibraryAuthorityRepository(_database).AssertLocalPublicationScope(capture.Scope, transaction, localAccess);
        var publication = ReadPublication(capture, transaction) ?? throw new CatalogGenerationCaptureConflictException();
        var intent = new ProtocolSigningJournal(_database, _budget).Read(capture.CommitIntentId, transaction) ?? throw new CatalogGenerationCaptureConflictException();
        var work = CommitWork(capture, intent, transaction);
        var context = checkpoint.Context; var scope = capture.Scope;
        if (context.LibraryId != scope.LibraryId || context.AccountId != scope.AccountId || context.FolderId != scope.FolderId ||
            context.AccountRevision != (localAccess?.AccountRevision ?? scope.AccountRevision) ||
            committed != new CatalogObjectReference(scope.AccountId, work.FileId!, scope.FolderId, work.Payload.Length, work.Sha256)) throw new WorkspaceObservationConflictException();
        var observations = new WorkspaceObservationRepository(_database, _budget); observations.AssertCompletedScan(checkpoint, null, transaction);
        var known = observations.Known(context, transaction).ToDictionary(item => item.FileId, StringComparer.Ordinal);
        var outbox = new WorkspacePublicationRepository(_database, _budget);
        foreach (var id in outbox.Identities(WorkspaceObservationRepository.MaximumDocuments, transaction))
        {
            var record = outbox.Read(id, transaction) ?? throw new WorkspacePublicationConflictException();
            if (record.Scope.GenerationId != scope.GenerationId) continue;
            if (record.Scope != scope || record.FileId is null || record.VerifiedVersion is null || !known.TryGetValue(record.FileId, out var observed) ||
                observed.Version < record.VerifiedVersion || observed.Bytes != record.Payload.Length || observed.Sha256 != record.Sha256 ||
                (observed.MimeType ?? "application/json") != record.MimeType) throw new WorkspaceObservationConflictException();
        }
        foreach (var selected in new CatalogDependencyReferenceRepository(_database, _budget).ForScope(scope, transaction))
        {
            var original = outbox.Read(selected.OriginalPublicationId, transaction) ?? throw new WorkspacePublicationConflictException();
            if (!known.TryGetValue(selected.FileId, out var observed) || observed.Version < original.VerifiedVersion ||
                observed.Bytes != selected.Bytes || observed.Sha256 != selected.Sha256 ||
                (observed.MimeType ?? "application/json") != selected.MimeType) throw new WorkspaceObservationConflictException();
        }
        if (publication.Basis.Parent is { } parent && (!known.TryGetValue(parent.FileId, out var old) || old.Bytes != parent.Bytes || old.Sha256 != parent.Sha256 ||
            (old.MimeType ?? "application/json") != "application/json")) throw new WorkspaceObservationConflictException();
        return new(scope.GenerationId, scope, capture.SnapshotId, capture.CommitIntentId, intent.Sequence,
            capture.Snapshot!.Assets, checkpoint, known[work.FileId!]);
    }

    private void ValidateVerificationState(CatalogGenerationCapture capture, SqliteTransaction? transaction)
    {
        if(ReadCurrentSupersession(capture,transaction,CancellationToken.None) is not null || ReadUnsignedCurrentDisposition(capture,transaction,CancellationToken.None) is not null)
        {
            if(capture.Phase is not ("complete" or "snapshot-retiring" or "retired")) throw new InvalidDataException("Supersession lost its atomic terminal phase.");
            return;
        }
        var selected=ReadCurrentGenerationVerification(capture,transaction);
        if(selected is not null)
        {
            if(capture.Phase is not ("complete" or "snapshot-retiring" or "retired")) throw new InvalidDataException("Current completion lost its atomic completed phase.");
            return;
        }
        var receipt = ReadVerification(capture, transaction);
        if ((capture.Phase is "commit-verified" or "complete" or "snapshot-retiring" or "retired") != (receipt is not null))
            throw new InvalidDataException("Generation phase lost or substituted its immutable verification receipt.");
    }
    private void ValidateRetiredHistory(SqliteTransaction transaction)
    {
        var ids = new List<Guid>();
        using (var query = Command(transaction, "SELECT id FROM catalog_generation_capture WHERE phase='retired' ORDER BY id LIMIT $limit",
            ("$limit", ProtocolSigningJournal.MaximumInventoryIntents + 1)))
        using (var reader = query.ExecuteReader())
            while (reader.Read())
            {
                if (ids.Count == ProtocolSigningJournal.MaximumInventoryIntents) throw new InvalidDataException("Retired generation history exceeds its complete validation bound.");
                ids.Add(ParseId(reader.GetString(0)));
            }
        var selectedHistory=false;
        foreach(var id in ids)
        {
            var capture=Read(id,transaction) ?? throw new InvalidDataException("Retired capture history disappeared.");
            if(ReadCurrentSupersession(capture,transaction,CancellationToken.None) is null && ReadUnsignedCurrentDisposition(capture,transaction,CancellationToken.None) is null) selectedHistory=true;
        }
        if(selectedHistory && ReadPublishedHead(transaction) is null) throw new InvalidDataException("Retired selected history lost its completed publisher head.");
    }
    private CatalogGenerationVerification? ReadVerification(CatalogGenerationCapture capture, SqliteTransaction? transaction)
    {
        string text; string digest;
        using (var query = Command(transaction, "SELECT length(document),document,document_hash FROM catalog_generation_verification WHERE id=$id", ("$id", capture.Scope.GenerationId.ToString("D"))))
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetInt64(0) is < 1 or > 49152) throw new InvalidDataException("Generation verification exceeds its bound.");
            text = reader.GetString(1); digest = reader.GetString(2);
        }
        if(CurrentVerificationTag(text))
        {
            _=ReadCurrentGenerationVerification(capture,transaction) ?? throw new InvalidDataException("Current completion lost its typed receipt.");
            return null; // Never manufacture a legacy observation/checkpoint.
        }
        try
        {
            var receipt = JsonSerializer.Deserialize<CatalogGenerationVerification>(text) ?? throw new InvalidDataException("Generation verification is missing.");
            var publication = ReadPublication(capture, transaction) ?? throw new InvalidDataException("Generation verification lost its plan.");
            var intent = new ProtocolSigningJournal(_database, _budget).Read(capture.CommitIntentId, transaction) ?? throw new InvalidDataException("Generation verification lost its signature.");
            var work = CommitWork(capture, intent, transaction);
            if (receipt.Scope != capture.Scope || receipt.GenerationId != capture.Scope.GenerationId || receipt.SnapshotId != capture.SnapshotId || receipt.CommitIntentId != capture.CommitIntentId ||
                receipt.Sequence != intent.Sequence || receipt.Assets != capture.Snapshot!.Assets || receipt.Observation?.Context is not { } context || receipt.Commit is null ||
                !VerificationAccessMatches(capture, context, transaction) ||
                receipt.Observation.Revision < 1 || receipt.Observation.Documents is < 1 or > WorkspaceObservationRepository.MaximumDocuments ||
                receipt.Observation.Cursor is not { Length: > 0 and <= 4096 } || receipt.Observation.Cursor.Any(char.IsControl) || !Digest(receipt.Observation.InventorySha256) ||
                receipt.Commit.FileId != work.FileId || receipt.Commit.Version < work.VerifiedVersion || receipt.Commit.Bytes != work.Payload.Length || receipt.Commit.Sha256 != work.Sha256 ||
                (receipt.Commit.MimeType ?? "application/json") != "application/json" || EncodeVerification(receipt) != text || VerificationHash(capture, publication, text) != digest)
                throw new InvalidDataException("Generation verification changed its original context, signature, bytes or observation.");
            return receipt;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException or OverflowException)
        { throw new InvalidDataException("Generation verification cannot be decoded safely.", error); }
    }
    private bool VerificationAccessMatches(CatalogGenerationCapture capture, WorkspaceObservationContext context, SqliteTransaction? transaction)
    {
        var scope = capture.Scope;
        if (context.LibraryId != scope.LibraryId || context.AccountId != scope.AccountId || context.FolderId != scope.FolderId || context.AccountRevision < scope.AccountRevision)
            return false;
        if (context.AccountRevision == scope.AccountRevision) return true;
        // Historical verification may retain the original scope and actual renewed
        // observation only through its exact immutable access-attempt binding. This
        // read is not current access. Use the already-read capture to avoid recursion.
        return ReadAccessAttempt(capture, context.AccountRevision, transaction)?.Observation.Context == context;
    }

    private WorkspacePublicationRecord CommitWork(CatalogGenerationCapture capture, ProtocolSigningIntent intent, SqliteTransaction? transaction)
    {
        var publication = ReadPublication(capture, transaction) ?? throw new InvalidDataException("Commit verification lost its publication basis.");
        AssertIntent(capture, publication.Plan ?? throw new InvalidDataException("Commit verification lost its plan."), intent);
        var work = new WorkspacePublicationRepository(_database, _budget).Read(capture.CommitIntentId, transaction) ?? throw new InvalidDataException("Commit verification lost its original outbox.");
        if (intent.Envelope is not { } envelope || work.Scope != capture.Scope || work.Role != "catalog-commit" || work.MimeType != "application/json" ||
            work.FileId is null || work.VerifiedVersion is null || work.Sha256 != intent.EnvelopeSha256 || !work.Payload.Span.SequenceEqual(envelope.Span))
            throw new InvalidDataException("Commit verification changed its exact signature, scope, allocation or receipt.");
        return work;
    }
    private PublishedCatalogHead? ReadPublishedHead(SqliteTransaction transaction)
    {
        using var query = Command(transaction, "SELECT length(value),value FROM processor_meta WHERE key=$key", ("$key", PublishedHeadKey));
        string? text;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) text = null;
            else { if (reader.GetInt64(0) is < 1 or > 1024) throw new InvalidDataException("Publisher head exceeds its bounded pointer."); text = reader.GetString(1); }
        }
        if (text is null)
        {
            var completedIds=new List<Guid>();
            using(var completed=Command(transaction,"SELECT id FROM catalog_generation_capture WHERE phase IN('complete','snapshot-retiring','retired') ORDER BY id LIMIT $limit",
                ("$limit",ProtocolSigningJournal.MaximumInventoryIntents+1)))
            using(var reader=completed.ExecuteReader())
                while(reader.Read())
                {
                    if(completedIds.Count==ProtocolSigningJournal.MaximumInventoryIntents) throw new InvalidDataException("Completed history exceeds its validation bound.");
                    completedIds.Add(ParseId(reader.GetString(0)));
                }
            foreach(var id in completedIds)
            {
                var capture=Read(id,transaction) ?? throw new InvalidDataException("Completed capture disappeared.");
                if(ReadCurrentSupersession(capture,transaction,CancellationToken.None) is null && ReadUnsignedCurrentDisposition(capture,transaction,CancellationToken.None) is null)
                    throw new InvalidDataException("Completed selected publication history lost its atomic head pointer.");
            }
            return null;
        }
        if(CurrentHeadTag(text)) return ReadCurrentPublishedHead(text,transaction);
        try
        {
            var pointer = JsonSerializer.Deserialize<HeadPointer>(text) ?? throw new InvalidDataException("Publisher head pointer is missing."); Id(pointer.GenerationId);
            var capture = RequiredCapture(pointer.GenerationId, transaction);
            var receipt = ReadVerification(capture, transaction) ?? throw new InvalidDataException("Publisher head lost its verification.");
            if (capture.Phase is not ("complete" or "snapshot-retiring" or "retired") || EncodeHead(pointer) != text ||
                VerificationHash(capture, ReadPublication(capture, transaction)!, EncodeVerification(receipt)) != pointer.VerificationSha256)
                throw new InvalidDataException("Publisher head changed its completed generation binding.");
            return Head(receipt);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException)
        { throw new InvalidDataException("Publisher head cannot be decoded safely.", error); }
    }
    private static bool Digest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static PublishedCatalogHead Head(CatalogGenerationVerification receipt) => new(receipt.GenerationId, receipt.Sequence, receipt.Assets,
        new(receipt.Scope.AccountId, receipt.Commit.FileId, receipt.Scope.FolderId, receipt.Commit.Bytes, receipt.Commit.Sha256));
    private static string EncodeVerification(CatalogGenerationVerification value) => Encoding.UTF8.GetString(new CanonicalJson(49152).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string EncodeHead(HeadPointer value) => Encoding.UTF8.GetString(new CanonicalJson(1024).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string VerificationHash(CatalogGenerationCapture capture, CapturedCatalogPublication publication, string encoded) => Hash(new CanonicalJson(65536)
        .Encode(JsonSerializer.SerializeToUtf8Bytes(new { capture.Scope, capture.SnapshotId, capture.CommitIntentId, planHash = PlanHash(capture, publication.Basis, PublicationEncode(publication.Plan!)), encoded })));
}
