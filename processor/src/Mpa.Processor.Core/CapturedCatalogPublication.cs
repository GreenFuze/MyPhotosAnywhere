using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Immutable inputs selected before the first dependency write. Parent and
/// source-policy fields are captured evidence, never remote availability or permission.</summary>
public sealed record CapturedCatalogBasis(CatalogSnapshotDescriptor Snapshot, CatalogObjectReference? Parent,
    long ParentSequence, string SourcePolicySha256, long SettingsRevision, long MinimumHistoryGeneration);
public sealed record CapturedCatalogCommitPlan(CapturedCatalogBasis Basis, CatalogObjectReference RootIndex, CatalogObjectReference SourceCoverage)
{
    /// <summary>Only this saved plan determines the outgoing body. No current discovery
    /// or caller-supplied dependency can be substituted under an existing signing ID.</summary>
    public byte[] EncodeBody(WorkspacePublicationScope scope, long sequence)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (sequence != checked(Basis.ParentSequence + 1)) throw new CatalogGenerationCaptureConflictException();
        var payload = new CatalogPublicationWriter(new(scope.AccountId, scope.FolderId)).EncodeCommit(new(scope.GenerationId,
            Basis.Parent, RootIndex, SourceCoverage, Basis.SettingsRevision, Basis.MinimumHistoryGeneration), sequence);
        using var document = JsonDocument.Parse(payload);
        return new CanonicalJson(ProtocolSigningJournal.MaximumBodyBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa", major = 1, minor = 0, libraryId = scope.LibraryId.ToString("D"), actorId = scope.ActorId.ToString("D"),
            epoch = scope.ActorEpoch.ToString(CultureInfo.InvariantCulture), sequence = sequence.ToString(CultureInfo.InvariantCulture),
            kind = "catalog-commit", requiredCapabilities = new[] { DriveCatalogFacts.Capability, CatalogPublicationReader.Capability },
            payload = document.RootElement
        }));
    }
}
public sealed record CapturedCatalogPublication(CatalogGenerationCapture Capture, CapturedCatalogBasis Basis, CapturedCatalogCommitPlan? Plan);

/// <summary>Durable immutable generation plan and phase checkpoints under the serial
/// database owner. No method grants cloud authority, activates a head, releases snapshot
/// claims or infers permission to delete an original. Actual ancestry/graph reconciliation
/// and a private current upload admission remain the application owner's responsibility.</summary>
public sealed partial class CatalogGenerationCaptureRepository
{
    public CapturedCatalogPublication? Publication(Guid generationId)
    {
        Id(generationId); var capture = Read(generationId);
        return capture is null ? null : ReadPublication(capture, null);
    }

    public CapturedCatalogPublication CapturePublicationBasis(Guid generationId, CapturedCatalogBasis basis, CatalogGenerationLocalAccess? localAccess = null)
    {
        Id(generationId); ArgumentNullException.ThrowIfNull(basis);
        using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        if (capture.Phase == "abandoned") throw new CatalogGenerationCaptureConflictException();
        ValidateBasis(capture, basis); var existing = ReadPublication(capture, transaction);
        if (existing is not null)
        {
            if (existing.Basis != basis) throw new CatalogGenerationCaptureConflictException();
            transaction.Commit(); return existing; // Recovery read, never fresh authority.
        }
        if (capture.Phase != "frozen") throw new CatalogGenerationCaptureConflictException();
        AssertNoPublicationWork(capture, transaction);
        new LibraryAuthorityRepository(_database, _budget).AssertLocalPublicationScope(capture.Scope, transaction, localAccess);
        // This read is local. Complete current cloud/high-water verification is separate.
        var approved = new LibraryAuthorityRepository(_database).CurrentSourceConfiguration(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        if (approved.EnvelopeSha256 != basis.SourcePolicySha256 || approved.Configuration.LibraryId != capture.Scope.LibraryId ||
            approved.Configuration.Revision != capture.Scope.ConfigurationRevision) throw new CatalogGenerationCaptureConflictException();
        var encoded = PublicationEncode(basis); using var reservation = _budget.Reserve(131072); reservation.RecheckBeforeWrite(reservation.Bytes);
        using var insert = Command(transaction, "INSERT INTO catalog_generation_publication(id,basis,basis_hash) VALUES($id,$basis,$hash)",
            ("$id", generationId.ToString("D")), ("$basis", encoded), ("$hash", BasisHash(capture, encoded)));
        if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Publication basis lost its captured generation owner.");
        ChangePhase(capture, "publication-planned", transaction);
        transaction.Commit(); return new(capture with { Phase = "publication-planned" }, basis, null);
    }

    /// <summary>Call after the actual dependency owner has read back and verified the
    /// complete graph. This checkpoint independently binds root/coverage to this exact
    /// generation's retained, verified outbox bytes; receipts alone are not current reads.</summary>
    public CapturedCatalogPublication SealDependencies(Guid generationId, CapturedCatalogCommitPlan plan, CatalogGenerationLocalAccess? localAccess = null)
    {
        Id(generationId); ArgumentNullException.ThrowIfNull(plan);
        using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        var publication = ReadPublication(capture, transaction) ?? throw new InvalidOperationException("Capture the publication basis before writing dependencies.");
        ValidatePlan(capture, plan);
        if (publication.Basis != plan.Basis) throw new CatalogGenerationCaptureConflictException();
        if (publication.Plan is not null)
        {
            if (publication.Plan != plan) throw new CatalogGenerationCaptureConflictException();
            transaction.Commit(); return publication;
        }
        if (capture.Phase != "publication-planned" || new ProtocolSigningJournal(_database, _budget).Read(capture.CommitIntentId, transaction) is not null)
            throw new CatalogGenerationCaptureConflictException();
        new LibraryAuthorityRepository(_database).AssertLocalPublicationScope(capture.Scope, transaction, localAccess);
        AssertDependency(capture, plan.RootIndex, "facts-index", transaction);
        AssertDependency(capture, plan.SourceCoverage, "coverage-root", transaction);
        var encoded = PublicationEncode(plan); using var reservation = _budget.Reserve(131072); reservation.RecheckBeforeWrite(reservation.Bytes);
        using (var update = Command(transaction, "UPDATE catalog_generation_publication SET plan=$plan,plan_hash=$hash WHERE id=$id AND plan IS NULL",
            ("$plan", encoded), ("$hash", PlanHash(capture, publication.Basis, encoded)), ("$id", generationId.ToString("D"))))
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Dependency checkpoint lost its immutable plan owner.");
        ChangePhase(capture, "dependencies-ready", transaction); transaction.Commit();
        return new(capture with { Phase = "dependencies-ready" }, publication.Basis, plan);
    }

    /// <summary>Recovers the crash between sealing a signature and saving this phase.
    /// Re-reads the actual journal and compares the exact body, actor and fixed sequence.
    /// Neither a caller's envelope nor a receipt can mark a generation sealed.</summary>
    public CapturedCatalogPublication ConfirmCommitSealed(Guid generationId)
    {
        Id(generationId); using var transaction = _database.Connection.BeginTransaction(); var capture = RequiredCapture(generationId, transaction);
        var publication = ReadPublication(capture, transaction) ?? throw new InvalidOperationException("The captured publication plan is missing.");
        var plan = publication.Plan ?? throw new InvalidOperationException("Seal dependencies before signing the commit.");
        var intent = new ProtocolSigningJournal(_database, _budget).Read(capture.CommitIntentId, transaction)
            ?? throw new InvalidOperationException("The fixed commit signing intent is missing.");
        AssertIntent(capture, plan, intent);
        if (intent.Envelope is null) throw new InvalidOperationException("The commit signature has not been durably sealed.");
        if (capture.Phase == "dependencies-ready")
        {
            using var reservation = _budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
            ChangePhase(capture, "commit-sealed", transaction);
        }
        else if (capture.Phase != "commit-sealed") throw new CatalogGenerationCaptureConflictException();
        transaction.Commit(); return publication with { Capture = capture with { Phase = "commit-sealed" } };
    }

    private void ValidatePublicationState(CatalogGenerationCapture capture, SqliteTransaction? transaction)
    {
        var publication = ReadPublication(capture, transaction);
        if (capture.Phase == "publication-planned" && publication is null)
            throw new InvalidDataException("Planned generation lost its immutable publication basis; preserve its claim.");
        if (capture.Phase is "dependencies-ready" or "commit-sealed" or "commit-verified" or "complete" or "snapshot-retiring" or "retired" && publication?.Plan is null)
            throw new InvalidDataException("Prepared generation lost its immutable publication plan; preserve its claim.");
    }
    private CapturedCatalogPublication? ReadPublication(CatalogGenerationCapture capture, SqliteTransaction? transaction)
    {
        string basisText; string basisHash; string? planText; string? planHash;
        using (var command = Command(transaction, "SELECT length(basis),basis,basis_hash,length(plan),plan,plan_hash FROM catalog_generation_publication WHERE id=$id", ("$id", capture.Scope.GenerationId.ToString("D"))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetInt64(0) is < 1 or > 16384 || !reader.IsDBNull(3) && reader.GetInt64(3) is < 1 or > 16384 || reader.IsDBNull(4) != reader.IsDBNull(5))
                throw new InvalidDataException("Captured publication exceeds its bounded plan or loses its integrity proof.");
            basisText = reader.GetString(1); basisHash = reader.GetString(2);
            planText = reader.IsDBNull(4) ? null : reader.GetString(4); planHash = reader.IsDBNull(5) ? null : reader.GetString(5);
        }
        try
        {
            var basis = JsonSerializer.Deserialize<CapturedCatalogBasis>(basisText) ?? throw new InvalidDataException("Captured publication basis is missing.");
            ValidateBasis(capture, basis);
            if (PublicationEncode(basis) != basisText || BasisHash(capture, basisText) != basisHash) throw new InvalidDataException("Captured publication basis integrity failed.");
            CapturedCatalogCommitPlan? plan = null;
            if (planText is not null)
            {
                plan = JsonSerializer.Deserialize<CapturedCatalogCommitPlan>(planText) ?? throw new InvalidDataException("Captured commit plan is missing.");
                ValidatePlan(capture, plan);
                if (plan.Basis != basis || PublicationEncode(plan) != planText || PlanHash(capture, basis, planText) != planHash)
                    throw new InvalidDataException("Captured commit plan integrity failed.");
                AssertDependency(capture, plan.RootIndex, "facts-index", transaction);
                AssertDependency(capture, plan.SourceCoverage, "coverage-root", transaction);
                var intent = new ProtocolSigningJournal(_database, _budget).Read(capture.CommitIntentId, transaction);
                if (intent is not null) AssertIntent(capture, plan, intent);
                if (capture.Phase is "commit-sealed" or "commit-verified" or "complete" or "snapshot-retiring" or "retired" && intent?.Envelope is null) throw new InvalidDataException("Sealed generation lost its retained signature.");
            }
            if (capture.Phase is "capturing" or "frozen" || (capture.Phase is "publication-planned" or "abandoned") != (plan is null))
                throw new InvalidDataException("Publication plan disagrees with its retained generation phase.");
            return new(capture, basis, plan);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException or CatalogGenerationCaptureConflictException or OverflowException)
        { throw new InvalidDataException("Captured publication is invalid; do not replace it with current discovery.", error); }
    }

    private static void ValidateBasis(CatalogGenerationCapture capture, CapturedCatalogBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis.Snapshot); ValidateDescriptor(basis.Snapshot);
        if (capture.Snapshot != basis.Snapshot || basis.ParentSequence < 0 || basis.ParentSequence == long.MaxValue ||
            (basis.Parent is null) != (basis.ParentSequence == 0) || basis.SourcePolicySha256 is not { Length: 64 } ||
            basis.SourcePolicySha256.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')) || basis.SettingsRevision < 0 ||
            basis.MinimumHistoryGeneration < 1 || basis.MinimumHistoryGeneration > basis.ParentSequence + 1) throw new CatalogGenerationCaptureConflictException();
        if (basis.Parent is not null) ValidateReference(capture.Scope, basis.Parent);
    }
    private static void ValidatePlan(CatalogGenerationCapture capture, CapturedCatalogCommitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan.Basis); ValidateBasis(capture, plan.Basis);
        ValidateReference(capture.Scope, plan.RootIndex); ValidateReference(capture.Scope, plan.SourceCoverage);
        if (plan.RootIndex.FileId == plan.SourceCoverage.FileId || plan.Basis.Parent?.FileId == plan.RootIndex.FileId ||
            plan.Basis.Parent?.FileId == plan.SourceCoverage.FileId) throw new CatalogGenerationCaptureConflictException();
        _ = plan.EncodeBody(capture.Scope, checked(plan.Basis.ParentSequence + 1));
    }
    private static void ValidateReference(WorkspacePublicationScope scope, CatalogObjectReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AccountId != scope.AccountId || value.WorkspaceId != scope.FolderId || value.FileId is not { Length: > 0 and <= 256 } ||
            value.FileId is "root" || value.FileId == scope.FolderId || value.FileId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            value.Bytes is < 1 or > WorkspacePublicationRepository.MaximumPayloadBytes || value.Sha256 is not { Length: 64 } ||
            value.Sha256.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))) throw new CatalogGenerationCaptureConflictException();
    }
    private void AssertDependency(CatalogGenerationCapture capture, CatalogObjectReference reference, string role, SqliteTransaction? transaction)
    {
        Guid id;
        using (var query = Command(transaction, "SELECT id FROM workspace_publication WHERE account_id=$account AND file_id=$file", ("$account", reference.AccountId), ("$file", reference.FileId)))
            id = query.ExecuteScalar() is string value ? ParseId(value) : throw new InvalidDataException("Prepared dependency has no retained outbox owner.");
        var record = new WorkspacePublicationRepository(_database, _budget).Read(id, transaction) ?? throw new InvalidDataException("The dependency outbox disappeared.");
        if (record.Scope != capture.Scope || record.Role != role || record.MimeType != "application/json" || record.VerifiedVersion is null ||
            record.Sha256 != reference.Sha256 || record.Payload.Length != reference.Bytes) throw new InvalidDataException("Prepared dependency changed its exact generation, role, receipt or immutable bytes.");
    }
    private static void AssertIntent(CatalogGenerationCapture capture, CapturedCatalogCommitPlan plan, ProtocolSigningIntent intent)
    {
        if (intent.Id != capture.CommitIntentId || intent.LibraryId != capture.Scope.LibraryId || intent.ActorId != capture.Scope.ActorId ||
            intent.Epoch != capture.Scope.ActorEpoch || intent.Kind != "catalog-commit" || !intent.Body.Span.SequenceEqual(plan.EncodeBody(capture.Scope, intent.Sequence)))
            throw new InvalidDataException("Retained signing intent differs from the fixed captured commit plan.");
    }
    private void AssertNoPublicationWork(CatalogGenerationCapture capture, SqliteTransaction transaction)
    {
        using var query = Command(transaction, "SELECT (SELECT count(*) FROM signing_intent WHERE id=$intent)+(SELECT count(*) FROM workspace_publication WHERE json_extract(scope,'$.GenerationId')=$id)",
            ("$intent", capture.CommitIntentId.ToString("D")), ("$id", capture.Scope.GenerationId.ToString("D")));
        if ((long)query.ExecuteScalar()! != 0) throw new CatalogGenerationCaptureConflictException();
    }
    private CatalogGenerationCapture RequiredCapture(Guid id, SqliteTransaction transaction) => Read(id, transaction) ?? throw new InvalidOperationException("The generation capture is missing.");
    private void ChangePhase(CatalogGenerationCapture capture, string phase, SqliteTransaction transaction)
    {
        using var update = Command(transaction, "UPDATE catalog_generation_capture SET phase=$next WHERE id=$id AND phase=$previous", ("$next", phase), ("$id", capture.Scope.GenerationId.ToString("D")), ("$previous", capture.Phase));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Generation phase transition lost its exclusive captured owner.");
    }
    private static string PublicationEncode<T>(T value) => Encoding.UTF8.GetString(new CanonicalJson(16384).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string BasisHash(CatalogGenerationCapture capture, string basis) => Hash(new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(new { capture.Scope, capture.SnapshotId, capture.CommitIntentId, basis })));
    private static string PlanHash(CatalogGenerationCapture capture, CapturedCatalogBasis basis, string plan) => Hash(new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(new { basisHash = BasisHash(capture, PublicationEncode(basis)), plan })));
}
