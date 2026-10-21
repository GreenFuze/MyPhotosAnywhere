using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Core;

public sealed record SourceDiscoverySchedule(Guid JobId, bool Created);
public sealed record SourceDiscoveryVerification(StageLease Lease, SourceDiscoveryScope Scope);
public sealed record VerifiedDiscoveryRoot(string FolderId, string? SharedDriveId);

public sealed partial class SourceDiscoveryRepository
{
    private const string VerificationStage = "verify-source";
    private static readonly string VerificationRecipe = Convert.ToHexStringLower(SHA256.HashData("mpa-source-verification:1"u8));

    /// <summary>Composition-root startup only, after acquiring the exclusive process/state
    /// lease and before admitting any new reader. These three stages have no child processes;
    /// all other job kinds remain fenced until their own worker owners confirm exit.</summary>
    public int RecoverInterruptedReadsAfterExclusiveRestart()
    {
        using var transaction = _database.Connection.BeginTransaction();
        using (var validate = _database.Connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = """
                SELECT 1 FROM item_stage s JOIN job j ON j.id=s.job_id
                WHERE j.kind=$kind AND s.state IN('running','canceling') AND s.name NOT IN($verify,$list,$repair) LIMIT 1
                """;
            validate.Parameters.AddWithValue("$kind", JobKind); validate.Parameters.AddWithValue("$verify", VerificationStage);
            validate.Parameters.AddWithValue("$list", Stage); validate.Parameters.AddWithValue("$repair", RepairStage);
            if (validate.ExecuteScalar() is not null) throw new InvalidDataException("Unknown source stage cannot be recovered as a joined metadata read.");
        }
        using var update = _database.Connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE item_stage SET state=CASE WHEN (SELECT canceled FROM job WHERE id=job_id)=1 THEN 'canceled' ELSE 'queued' END,lease_id=NULL
            WHERE state IN('running','canceling') AND job_id IN(SELECT id FROM job WHERE kind=$kind)
            """;
        update.Parameters.AddWithValue("$kind", JobKind); var changed = update.ExecuteNonQuery(); transaction.Commit(); return changed;
    }

    /// <summary>At most one automatic request for each captured source/account revision.
    /// Restart and browser retries cannot reset completed or failed work. A failed root check
    /// is durable work too; network access happens after this atomic request is committed.</summary>
    public SourceDiscoverySchedule EnsureScheduled(Guid sourceId, long sourceRevision, long accountRevision)
    {
        var scope = CaptureScope(sourceId, sourceRevision, accountRevision);
        var suffix = "discovery-auto-" + sourceId.ToString("N") + "-" + sourceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "-" + accountRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var job = _identity.Derive(scope.AccountId, suffix);
        return new(job, _jobs.EnsureJobWithInitialItem(job, JobKind, ScopeContext(scope), VerificationItem(scope)));
    }
    public SourceDiscoveryVerification? ClaimVerification()
    {
        var lease = _jobs.ClaimNext(JobKind, VerificationStage);
        if (lease is null) return null;
        var work = new SourceDiscoveryVerification(lease, Scope(lease.CapturedContext)); ValidateShape(work); return work;
    }
    public void ValidateCurrent(SourceDiscoveryVerification work)
    { ValidateShape(work); ValidateActive(work.Lease); ValidateCurrent(work.Scope); }

    public VerifiedDiscoveryRoot? VerifiedRoot(SourceDiscoveryVerification work)
    {
        ValidateShape(work);
        if (work.Lease.Checkpoint is null) return null;
        using var context = Object(work.Lease.Checkpoint, 3, "mpa-source-verified-root-1"); var root = context.RootElement;
        var folder = root.GetProperty("folderId").GetString(); var shared = root.GetProperty("sharedDriveId").GetString();
        if (folder is null || folder != work.Scope.RootFolderId || shared is not null && !Identity(shared)) throw new InvalidDataException("Saved verification changed its captured root.");
        return new(folder, shared);
    }
    public SourceDiscoveryVerification SaveVerifiedRoot(SourceDiscoveryVerification work, string verifiedFolderId, string? verifiedSharedDriveId)
    {
        ValidateCurrent(work);
        if (verifiedFolderId != work.Scope.RootFolderId || verifiedSharedDriveId is not null && !Identity(verifiedSharedDriveId))
            throw new InvalidDataException("Verification cannot replace the captured source root.");
        var prior = VerifiedRoot(work);
        if (prior is not null && (prior.FolderId != verifiedFolderId || prior.SharedDriveId != verifiedSharedDriveId))
            throw new InvalidDataException("An accepted verification checkpoint is immutable.");
        var context = _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-verified-root-1", folderId = verifiedFolderId, sharedDriveId = verifiedSharedDriveId }));
        _jobs.SaveCheckpoint(work.Lease, context);
        return work with { Lease = work.Lease with { Checkpoint = Encoding.UTF8.GetString(context) } };
    }
    public void CompleteVerification(SourceDiscoveryVerification work)
    {
        ValidateCurrent(work);
        var verified = VerifiedRoot(work) ?? throw new InvalidOperationException("Source verification requires its accepted checkpoint.");
        // If the process exits after root admission but before this receipt, replay uses the
        // accepted checkpoint rather than a newer network result that would retarget work.
        _jobs.EnsureItem(work.Lease.JobId, Item(work.Scope, new(verified.FolderId, verified.SharedDriveId, 0)));
        _jobs.Complete(work.Lease, _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-verification-receipt-1",
            sourceId = work.Scope.SourceId.ToString("D"), folderId = verified.FolderId, sharedDriveId = verified.SharedDriveId })));
    }
    private JobItemSpec VerificationItem(SourceDiscoveryScope scope)
    {
        var context = _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-verification-1", folderId = scope.RootFolderId }));
        var hash = Convert.ToHexStringLower(SHA256.HashData(context));
        return new(_identity.Derive(scope.AccountId, "verify-" + hash), hash, [new(VerificationStage, VerificationRecipe, [])], Encoding.UTF8.GetString(context));
    }
    private void ValidateShape(SourceDiscoveryVerification work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var expected = VerificationItem(work.Scope);
        if (work.Scope != Scope(work.Lease.CapturedContext) || work.Lease.Stage != VerificationStage || expected.Id != work.Lease.ItemId || expected.CapturedContext != work.Lease.CapturedItemContext)
            throw new InvalidDataException("Source verification does not match its immutable captured identity.");
    }
}
