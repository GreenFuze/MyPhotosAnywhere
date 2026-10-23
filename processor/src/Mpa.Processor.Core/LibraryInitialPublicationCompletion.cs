using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>A historical stage receipt, not current cloud availability, head activation,
/// renewed credential permission or publication authority. Later workflow stages must
/// independently reconcile the actual current library before accepting or writing work.</summary>
public sealed record LibraryInitialPublicationCompletion(LibraryInitialPublicationPlan Plan, WorkspaceInventoryCheckpoint Observation,
    WorkspaceDocumentObservation Publisher, WorkspaceDocumentObservation Sources);
public sealed class LibraryInitialPublicationCompletionMissingException() : InvalidOperationException(
    "Later library work exists without its initial-publication completion receipt. Preserve the library and reconcile its retained history; do not restart setup.");

public sealed partial class LibraryAuthorityRepository
{
    private const string InitialCompletionKey = "initial_library_publication_complete_v1";
    private const int InitialCompletionBytes = 49152;

    /// <summary>Reads retained stage evidence even after policy/account changes. It
    /// validates exact sealed history, outbox and retained observations, but grants no
    /// current authority. Missing evidence over later work stops automatic bootstrap.</summary>
    public LibraryInitialPublicationCompletion? InitialPublicationCompletion()
    {
        _ = Budget; using var transaction = _database.Connection.BeginTransaction();
        var result = ReadInitialCompletion(transaction); transaction.Commit(); return result;
    }

    /// <summary>Called by the joined native owner after actual upload/read-back and a
    /// complete current scan. Saves both exact policies and the scan atomically. The
    /// original receipt is immutable; later observations never rewrite its evidence.</summary>
    public LibraryInitialPublicationCompletion CompleteInitialPublication(LibraryInitialPublicationPlan plan, WorkspaceInventoryCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(checkpoint); _ = Budget;
        using var transaction = _database.Connection.BeginTransaction();
        if (ReadInitialPublicationPlan(transaction) != plan) throw new LibraryInitialPublicationConflictException();
        var pin = ReadPin(transaction) ?? throw new InvalidDataException("Initial completion lost its pinned controller.");
        var signing = ReadInitialPolicyPlan(transaction) ?? throw new InvalidDataException("Initial completion lost its signing plan.");
        AssertBootstrapSourcesCurrent(transaction); AssertInitialPlanSelections(transaction, pin, signing); AssertLocalPublicationScope(plan.Scope, transaction);
        var outbox = new WorkspacePublicationRepository(_database, Budget); var ids = outbox.Identities(2, transaction);
        if (ids.Count != 2 || !ids.Contains(plan.PublisherObjectId) || !ids.Contains(plan.SourceObjectId)) throw new LibraryInitialPublicationConflictException();
        var observations = new WorkspaceObservationRepository(_database, Budget);
        if (observations.ReadCheckpoint(transaction) != checkpoint) throw new WorkspaceObservationConflictException();
        observations.AssertCompletedScan(checkpoint, null, transaction);
        var known = observations.Known(checkpoint.Context, transaction);
        var publisher = CompletionObject(plan, true, outbox, transaction); var sources = CompletionObject(plan, false, outbox, transaction);
        if (known.Count != 2) throw new WorkspaceObservationConflictException();
        var publisherObservation = known.SingleOrDefault(document => document.FileId == publisher.FileId) ?? throw new WorkspaceObservationConflictException();
        var sourceObservation = known.SingleOrDefault(document => document.FileId == sources.FileId) ?? throw new WorkspaceObservationConflictException();
        var result = new LibraryInitialPublicationCompletion(plan, checkpoint, publisherObservation, sourceObservation);
        AssertCompletionEvidence(result, transaction);
        var existing = ReadInitialCompletion(transaction);
        if (existing is not null) { transaction.Commit(); return existing; }
        var encoded = EncodeInitialCompletion(result);
        using var reservation = Budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded) * 3L + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(transaction, InitialCompletionKey, encoded); Insert(transaction, InitialCompletionKey + ":sha256", Hash(Encoding.UTF8.GetBytes(encoded)));
        transaction.Commit(); return result;
    }

    private LibraryInitialPublicationCompletion? ReadInitialCompletion(SqliteTransaction transaction)
    {
        var text = ReadMeta(transaction, InitialCompletionKey, InitialCompletionBytes); var digest = ReadMeta(transaction, InitialCompletionKey + ":sha256", 64);
        if (text is null && digest is null)
        {
            if (HasLaterInitialPublicationWork(transaction, ReadInitialPublicationPlan(transaction))) throw new LibraryInitialPublicationCompletionMissingException();
            return null;
        }
        if (text is null || digest is null || Hash(Encoding.UTF8.GetBytes(text)) != digest) throw new InvalidDataException("Initial publication completion is missing or corrupt; preserve the existing work.");
        try
        {
            var result = JsonSerializer.Deserialize<LibraryInitialPublicationCompletion>(text) ?? throw new InvalidDataException("Initial completion is missing.");
            if (EncodeInitialCompletion(result) != text) throw new InvalidDataException("Initial completion lost its canonical evidence.");
            AssertCompletionEvidence(result, transaction); return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException)
        { throw new InvalidDataException("Initial publication completion cannot be decoded safely.", error); }
    }

    private void AssertCompletionEvidence(LibraryInitialPublicationCompletion result, SqliteTransaction transaction)
    {
        var plan = result.Plan;
        if (plan is null || plan != ReadInitialPublicationPlan(transaction) || result.Observation?.Context is not { } context || result.Publisher is null || result.Sources is null)
            throw new InvalidDataException("Initial completion changed its fixed context or observations.");
        var pin = ReadPin(transaction) ?? throw new InvalidDataException("Initial completion lost its controller.");
        var signing = ReadInitialPolicyPlan(transaction) ?? throw new InvalidDataException("Initial completion lost its policy plan.");
        var approval = ReadBootstrapSources(transaction, pin) ?? throw new InvalidDataException("Initial completion lost its original human source approval.");
        if (approval.ConfigurationRevision != signing.ConfigurationRevision || approval.Sha256 != signing.SourceApprovalSha256)
            throw new InvalidDataException("Initial completion changed its original human source approval.");
        var checkpoint = result.Observation;
        if (context.LibraryId != plan.Scope.LibraryId || context.AccountId != plan.Scope.AccountId || context.FolderId != plan.Scope.FolderId || context.AccountRevision != plan.Scope.AccountRevision ||
            context.SharedDriveId is not null && (context.SharedDriveId is not { Length: > 0 and <= 128 } || context.SharedDriveId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))) ||
            checkpoint.Revision < 1 || checkpoint.Documents != 2 || checkpoint.Cursor is not { Length: > 0 and <= 4096 } || checkpoint.Cursor.Any(char.IsControl) || !InitialDigest(checkpoint.InventorySha256))
            throw new InvalidDataException("Initial completion lost its bounded exact workspace checkpoint.");
        var outbox = new WorkspacePublicationRepository(_database, Budget);
        var publisher = CompletionObject(plan, true, outbox, transaction); var sources = CompletionObject(plan, false, outbox, transaction);
        AssertCompletionObject(publisher, result.Publisher); AssertCompletionObject(sources, result.Sources);
        if (result.Publisher.FileId == result.Sources.FileId || WorkspaceObservationRepository.InventoryHash(new[] { result.Publisher, result.Sources }.OrderBy(document => document.FileId, StringComparer.Ordinal)) != checkpoint.InventorySha256)
            throw new InvalidDataException("Initial completion does not cover both exact policy observations.");
        var observations = new WorkspaceObservationRepository(_database, Budget);
        foreach (var document in new[] { result.Publisher, result.Sources })
        {
            var retained = observations.ReadOne(context, document.FileId, transaction);
            if (retained is null || retained.Bytes != document.Bytes || retained.Sha256 != document.Sha256 || retained.Version < document.Version)
                throw new InvalidDataException("Initial completion lost its retained actual observation.");
        }
        var current = observations.ReadCheckpoint(transaction);
        if (current is null || current.Revision < checkpoint.Revision || current.Context.LibraryId != context.LibraryId || current.Context.AccountId != context.AccountId ||
            current.Context.FolderId != context.FolderId || current.Context.SharedDriveId != context.SharedDriveId || current.Context.AccountRevision < context.AccountRevision ||
            current.Revision == checkpoint.Revision && current != checkpoint)
            throw new InvalidDataException("Initial completion lost its retained workspace checkpoint history.");
    }

    private WorkspacePublicationRecord CompletionObject(LibraryInitialPublicationPlan plan, bool publisher, WorkspacePublicationRepository outbox, SqliteTransaction transaction)
    {
        var pin = ReadPin(transaction) ?? throw new InvalidDataException("Initial completion lost its controller history.");
        var sealedPolicy = InitialSealedPolicy(new ProtocolSigningJournal(_database, Budget), transaction, publisher ? plan.PublisherIntentId : plan.SourceIntentId,
            publisher ? "library-publisher-authorization" : "library-source-configuration", pin);
        var record = outbox.Read(publisher ? plan.PublisherObjectId : plan.SourceObjectId, transaction) ?? throw new InvalidDataException("Initial completion lost its exact outbox object.");
        var expectedHash = publisher ? plan.PublisherEnvelopeSha256 : plan.SourceEnvelopeSha256;
        if (record.Scope != plan.Scope || record.Role != (publisher ? "publisher-authorization" : "source-configuration") || record.MimeType != "application/json" ||
            record.Sha256 != expectedHash || sealedPolicy.EnvelopeSha256 != expectedHash || !record.Payload.Span.SequenceEqual(sealedPolicy.Envelope!.Value.Span) ||
            record.FileId is null || record.VerifiedVersion is null)
            throw new InvalidDataException("Initial completion requires both exact sealed and verified policy objects.");
        return record;
    }
    private static void AssertCompletionObject(WorkspacePublicationRecord expected, WorkspaceDocumentObservation observed)
    {
        if (observed.FileId != expected.FileId || observed.Version < expected.VerifiedVersion || observed.Bytes != expected.Payload.Length || observed.Sha256 != expected.Sha256)
            throw new InvalidDataException("Initial completion changed an observed policy identity, revision, length or digest.");
    }
    private bool HasLaterInitialPublicationWork(SqliteTransaction transaction, LibraryInitialPublicationPlan? plan)
    {
        using var command = plan is null ? Command(transaction, "SELECT 1 FROM workspace_publication LIMIT 1") : Command(transaction, """
            SELECT 1 FROM workspace_publication WHERE id NOT IN($publisherObject,$sourceObject)
            UNION ALL SELECT 1 FROM signing_intent WHERE id NOT IN($publisherIntent,$sourceIntent) LIMIT 1
            """, ("$publisherObject", plan.PublisherObjectId.ToString("D")), ("$sourceObject", plan.SourceObjectId.ToString("D")),
            ("$publisherIntent", plan.PublisherIntentId.ToString("D")), ("$sourceIntent", plan.SourceIntentId.ToString("D")));
        return command.ExecuteScalar() is not null;
    }
    private static string EncodeInitialCompletion(LibraryInitialPublicationCompletion result) => Encoding.UTF8.GetString(new CanonicalJson(InitialCompletionBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(result)));
}
