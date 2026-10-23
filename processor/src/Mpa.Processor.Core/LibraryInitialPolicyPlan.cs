using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record LibraryInitialPolicyPlan(Guid LibraryId, Guid ControllerId, long ControllerEpoch, string AccountId, string FolderId,
    Guid PublisherId, long PublisherEpoch, string PublisherKeyId, long ConfigurationRevision, string SourceApprovalSha256,
    Guid PublisherIntentId, Guid SourceIntentId);
public sealed class LibraryInitialPolicyConflictException() : InvalidOperationException("Initial library policy preparation conflicts with retained or superseding work. Recover the existing plan; do not recreate setup or reactivate an old publisher.");

public sealed partial class LibraryAuthorityRepository
{
    private const string InitialPolicyPlanKey = "initial_library_policies_v1";
    private const int InitialPolicyPlanBytes = 4096;

    /// <summary>Historical plan only. It never grants current signing/publication rights.</summary>
    public LibraryInitialPolicyPlan? InitialPolicyPlan()
    {
        using var transaction = _database.Connection.BeginTransaction(); var result = ReadInitialPolicyPlan(transaction);
        transaction.Commit(); return result;
    }

    /// <summary>Fix both outgoing intent IDs before the first signature, using the
    /// still-current original human approval and registered native public identities.
    /// A lost reply resumes the exact saved plan; signing history cannot recreate it.</summary>
    public LibraryInitialPolicyPlan PrepareInitialPolicyPlan(PublisherSigningRegistration nativePublisher)
    {
        ArgumentNullException.ThrowIfNull(nativePublisher); _ = Budget;
        using var transaction = _database.Connection.BeginTransaction(); var pin = ReadPin(transaction) ?? throw new InvalidOperationException("Approve the library controller first.");
        var approved = AssertBootstrapSourcesCurrent(transaction);
        new ProtocolSigningJournal(_database, Budget).AssertBootstrapActors(transaction, pin, nativePublisher);
        var old = ReadInitialPolicyPlan(transaction);
        if (old is not null)
        {
            if (old != BuildInitialPlan(pin, approved, nativePublisher, old.PublisherIntentId, old.SourceIntentId)) throw new LibraryInitialPolicyConflictException();
            transaction.Commit(); return old;
        }
        using (var work = Command(transaction, "SELECT (SELECT count(*) FROM signing_intent)+(SELECT count(*) FROM signing_actor WHERE sequence<>0)"))
            if ((long)work.ExecuteScalar()! != 0 || HasPublisherHistory(transaction) || HasSourceHistory(transaction)) throw new LibraryInitialPolicyConflictException();
        var plan = BuildInitialPlan(pin, approved, nativePublisher, Guid.NewGuid(), Guid.NewGuid()); var encoded = EncodeInitialPlan(plan);
        using var reservation = Budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(transaction, InitialPolicyPlanKey, encoded); Insert(transaction, InitialPolicyPlanKey + ":sha256", Hash(Encoding.UTF8.GetBytes(encoded)));
        transaction.Commit(); return plan;
    }

    /// <summary>Initial setup cannot revive a superseded/revoked publisher or source
    /// policy, even when its original signature/intent is an exact retained replay.</summary>
    public LibraryInitialPolicyPlan AssertInitialPolicyIntentCurrent(Guid intentId, string kind, PublisherSigningRegistration nativePublisher)
    {
        if (intentId == Guid.Empty || kind is not ("library-publisher-authorization" or "library-source-configuration")) throw new ArgumentException("A fixed initial controller policy intent is required.");
        ArgumentNullException.ThrowIfNull(nativePublisher); _ = Budget;
        using var transaction = _database.Connection.BeginTransaction(); var pin = ReadPin(transaction) ?? throw new InvalidOperationException("Approve the library controller first.");
        var approved = AssertBootstrapSourcesCurrent(transaction);
        new ProtocolSigningJournal(_database, Budget).AssertBootstrapActors(transaction, pin, nativePublisher);
        var plan = ReadInitialPolicyPlan(transaction) ?? throw new InvalidOperationException("Prepare the durable initial policy plan before signing.");
        if (plan != BuildInitialPlan(pin, approved, nativePublisher, plan.PublisherIntentId, plan.SourceIntentId) ||
            intentId != (kind == "library-publisher-authorization" ? plan.PublisherIntentId : plan.SourceIntentId)) throw new LibraryInitialPolicyConflictException();
        var publisherSequence = SelectedSequence(transaction);
        if (publisherSequence is not null)
        {
            var policy = ReadPolicy(transaction, pin, Counter(publisherSequence));
            AssertInitialSelection(transaction, plan.PublisherIntentId, "library-publisher-authorization", policy.Sequence, policy.EnvelopeSha256);
        }
        var sources = ReadCurrentSources(transaction, pin);
        if (sources is not null) AssertInitialSelection(transaction, plan.SourceIntentId, "library-source-configuration", sources.Sequence, sources.EnvelopeSha256);
        transaction.Commit(); return plan;
    }
    private static LibraryInitialPolicyPlan BuildInitialPlan(LibraryControllerPin pin, LibraryBootstrapSources approved,
        PublisherSigningRegistration publisher, Guid publisherIntent, Guid sourceIntent) => new(pin.LibraryId, pin.ActorId, pin.Epoch, pin.AccountId, pin.FolderId,
            publisher.ActorId, publisher.Epoch, publisher.KeyId, approved.ConfigurationRevision, approved.Sha256, publisherIntent, sourceIntent);
    private void AssertInitialSelection(SqliteTransaction transaction, Guid intent, string kind, long sequence, string envelopeHash)
    {
        using var command = Command(transaction, "SELECT sequence,kind,envelope_hash FROM signing_intent WHERE id=$id", ("$id", intent.ToString("D")));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != sequence || reader.GetString(1) != kind || reader.IsDBNull(2) || reader.GetString(2) != envelopeHash) throw new LibraryInitialPolicyConflictException();
    }
    private LibraryInitialPolicyPlan? ReadInitialPolicyPlan(SqliteTransaction transaction)
    {
        var text = ReadMeta(transaction, InitialPolicyPlanKey, InitialPolicyPlanBytes); var digest = ReadMeta(transaction, InitialPolicyPlanKey + ":sha256", 64);
        if (text is null && digest is null) return null;
        if (text is null || digest is null || Hash(Encoding.UTF8.GetBytes(text)) != digest) throw new InvalidDataException("The fixed initial policy plan is missing or corrupt; preserve its signing history.");
        try
        {
            var plan = JsonSerializer.Deserialize<LibraryInitialPolicyPlan>(text) ?? throw new InvalidDataException("Initial policy plan is missing.");
            var pin = ReadPin(transaction) ?? throw new InvalidDataException("The initial policy plan lost its pinned controller.");
            if (plan.LibraryId != pin.LibraryId || plan.ControllerId != pin.ActorId || plan.ControllerEpoch != pin.Epoch || plan.AccountId != pin.AccountId || plan.FolderId != pin.FolderId ||
                plan.PublisherId == Guid.Empty || plan.PublisherId == pin.ActorId || plan.PublisherEpoch < 1 || plan.ConfigurationRevision < 1 ||
                !InitialDigest(plan.PublisherKeyId) || !InitialDigest(plan.SourceApprovalSha256) || plan.PublisherIntentId == Guid.Empty || plan.SourceIntentId == Guid.Empty ||
                plan.PublisherIntentId == plan.SourceIntentId || EncodeInitialPlan(plan) != text) throw new InvalidDataException("The initial policy plan changed its captured context.");
            return plan;
        }
        catch (JsonException error) { throw new InvalidDataException("The initial policy plan cannot be decoded.", error); }
    }
    private static bool InitialDigest(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string EncodeInitialPlan(LibraryInitialPolicyPlan plan) => Encoding.UTF8.GetString(new CanonicalJson(InitialPolicyPlanBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(plan)));
}
