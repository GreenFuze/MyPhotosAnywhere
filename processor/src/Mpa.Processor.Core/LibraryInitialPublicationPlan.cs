using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record LibraryInitialPublicationPlan(WorkspacePublicationScope Scope, Guid PublisherIntentId, Guid SourceIntentId,
    Guid PublisherObjectId, Guid SourceObjectId, string PublisherEnvelopeSha256, string SourceEnvelopeSha256);
public sealed class LibraryInitialPublicationConflictException() : InvalidOperationException("Initial policy uploads conflict with retained publication work. Recover the original context; do not change its identities or destination.");

public sealed partial class LibraryAuthorityRepository
{
    private const string InitialPublicationPlanKey = "initial_library_publication_v1";
    private const int InitialPublicationPlanBytes = 4096;

    /// <summary>Historical pending transport context only. It proves neither current
    /// account access nor actual Drive availability and cannot authorize an upload.</summary>
    public LibraryInitialPublicationPlan? InitialPublicationPlan()
    {
        using var transaction = _database.Connection.BeginTransaction(); var result = ReadInitialPublicationPlan(transaction);
        transaction.Commit(); return result;
    }

    /// <summary>Fix transport identities after both exact initial sealed policies have
    /// been selected locally, before preparing any outbox work. Actual native possession,
    /// complete cloud/pending/history reconciliation and renewed-access admission remain
    /// the production owner's responsibilities. A reconnect never edits the old scope.</summary>
    public LibraryInitialPublicationPlan PrepareInitialPublication()
    {
        _ = Budget; using var transaction = _database.Connection.BeginTransaction();
        var signing = ReadInitialPolicyPlan(transaction) ?? throw new InvalidOperationException("Prepare the fixed initial policy signing plan first.");
        var pin = ReadPin(transaction) ?? throw new InvalidDataException("Initial publication lost its pinned controller.");
        var approval = AssertBootstrapSourcesCurrent(transaction);
        if (approval.ConfigurationRevision != signing.ConfigurationRevision || approval.Sha256 != signing.SourceApprovalSha256) throw new LibraryInitialPolicyConflictException();
        AssertInitialPlanSelections(transaction, pin, signing);
        var current = ReadLocalPublicationAuthority(transaction);
        if (current.ActorId != signing.PublisherId || current.ActorEpoch != signing.PublisherEpoch || current.PublisherKeyId != signing.PublisherKeyId ||
            current.ConfigurationRevision != signing.ConfigurationRevision) throw new LibraryInitialPublicationConflictException();
        var journal = new ProtocolSigningJournal(_database, Budget);
        var publisher = InitialSealedPolicy(journal, transaction, signing.PublisherIntentId, "library-publisher-authorization", pin);
        var sources = InitialSealedPolicy(journal, transaction, signing.SourceIntentId, "library-source-configuration", pin);
        var old = ReadInitialPublicationPlan(transaction);
        if (old is not null)
        {
            var expectedScope = new WorkspacePublicationScope(current.LibraryId, current.ActorId, current.ActorEpoch, old.Scope.GenerationId,
                current.AccountId, current.AccountRevision, current.FolderId, current.ConfigurationRevision, current.AuthoritySha256);
            if (old.Scope.AccountRevision > current.AccountRevision || (old.Scope with { AccountRevision = current.AccountRevision }) != expectedScope ||
                old.PublisherEnvelopeSha256 != publisher.EnvelopeSha256 || old.SourceEnvelopeSha256 != sources.EnvelopeSha256) throw new LibraryInitialPublicationConflictException();
            transaction.Commit(); return old; // Original access revision remains immutable; this return grants no renewed access.
        }
        using (var prior = Command(transaction, "SELECT count(*) FROM workspace_publication"))
            if ((long)prior.ExecuteScalar()! != 0) throw new LibraryInitialPublicationConflictException();
        var scope = new WorkspacePublicationScope(current.LibraryId, current.ActorId, current.ActorEpoch, Guid.NewGuid(), current.AccountId,
            current.AccountRevision, current.FolderId, current.ConfigurationRevision, current.AuthoritySha256);
        var plan = new LibraryInitialPublicationPlan(scope, signing.PublisherIntentId, signing.SourceIntentId, Guid.NewGuid(), Guid.NewGuid(),
            publisher.EnvelopeSha256!, sources.EnvelopeSha256!); var encoded = EncodeInitialPublication(plan);
        using var reservation = Budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(transaction, InitialPublicationPlanKey, encoded); Insert(transaction, InitialPublicationPlanKey + ":sha256", Hash(Encoding.UTF8.GetBytes(encoded)));
        transaction.Commit(); return plan;
    }

    private static ProtocolSigningIntent InitialSealedPolicy(ProtocolSigningJournal journal, SqliteTransaction transaction, Guid id, string kind, LibraryControllerPin pin)
    {
        var intent = journal.Read(id, transaction) ?? throw new InvalidOperationException("The captured initial policy intent is missing.");
        if (intent.LibraryId != pin.LibraryId || intent.ActorId != pin.ActorId || intent.Epoch != pin.Epoch || intent.Kind != kind || intent.Envelope is null || intent.EnvelopeSha256 is null)
            throw new InvalidDataException("Initial publication requires the exact sealed controller policy intents.");
        return intent;
    }
    private LibraryInitialPublicationPlan? ReadInitialPublicationPlan(SqliteTransaction transaction)
    {
        var text = ReadMeta(transaction, InitialPublicationPlanKey, InitialPublicationPlanBytes); var digest = ReadMeta(transaction, InitialPublicationPlanKey + ":sha256", 64);
        if (text is null && digest is null) return null;
        if (text is null || digest is null || Hash(Encoding.UTF8.GetBytes(text)) != digest) throw new InvalidDataException("Initial publication context is missing or corrupt; retain its outbox and signing work.");
        try
        {
            var plan = JsonSerializer.Deserialize<LibraryInitialPublicationPlan>(text) ?? throw new InvalidDataException("Initial publication context is missing.");
            var signing = ReadInitialPolicyPlan(transaction) ?? throw new InvalidDataException("Initial publication lost its fixed signing plan.");
            if (plan.Scope is null) throw new InvalidDataException("Initial publication lost its captured scope.");
            WorkspacePublicationRepository.ValidateScope(plan.Scope);
            if (plan.Scope.LibraryId != signing.LibraryId || plan.Scope.ActorId != signing.PublisherId || plan.Scope.ActorEpoch != signing.PublisherEpoch ||
                plan.Scope.AccountId != signing.AccountId || plan.Scope.FolderId != signing.FolderId || plan.Scope.ConfigurationRevision != signing.ConfigurationRevision ||
                plan.PublisherIntentId != signing.PublisherIntentId || plan.SourceIntentId != signing.SourceIntentId ||
                !InitialDigest(plan.PublisherEnvelopeSha256) || !InitialDigest(plan.SourceEnvelopeSha256) || plan.PublisherObjectId == Guid.Empty || plan.SourceObjectId == Guid.Empty ||
                new[] { plan.Scope.GenerationId, plan.PublisherIntentId, plan.SourceIntentId, plan.PublisherObjectId, plan.SourceObjectId }.Distinct().Count() != 5 ||
                EncodeInitialPublication(plan) != text) throw new InvalidDataException("Initial publication changed its fixed policy/object identities or context.");
            return plan;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidDataException("Initial publication context cannot be decoded safely.", error); }
    }
    private static string EncodeInitialPublication(LibraryInitialPublicationPlan plan) => Encoding.UTF8.GetString(new CanonicalJson(InitialPublicationPlanBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(plan)));
}
