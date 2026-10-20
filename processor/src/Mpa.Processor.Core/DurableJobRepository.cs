using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record JobStageSpec(string Name, string RecipeSha256, IReadOnlyList<string> Dependencies);
public sealed record JobItemSpec(Guid Id, string InputFingerprint, IReadOnlyList<JobStageSpec> Stages);
public sealed record StageLease(Guid JobId, Guid ItemId, string Stage, Guid LeaseId, int Attempt, string CapturedContext, string? Checkpoint);
public sealed record JobSummary(int Queued, int Running, int Ready, int Failed, int Unsupported, int Canceled, bool Paused, bool IsCanceled);
public sealed record PendingStageReceipt(Guid Id, Guid JobId, Guid ItemId, string Stage, string Receipt);
public sealed class StaleStageLeaseException() : InvalidOperationException("Stage lease is no longer current; retain/reconcile receipts and do not publish stale results.");

/// <summary>Serial background coordinator repository. Transactions fence each attempt and commit
/// successful stage receipts plus outbox together. Network/native work never runs inside a transaction.</summary>
public sealed class DurableJobRepository(ProcessorStateDatabase database)
{
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly CanonicalJson _codec = new(256 * 1024);

    public void Create(Guid jobId, string kind, ReadOnlySpan<byte> capturedContext)
    {
        if (jobId == Guid.Empty || !Identifier(kind)) throw new ArgumentException("Invalid job identity/kind.");
        var canonicalContext = CanonicalObject(capturedContext);
        var context = Encoding.UTF8.GetBytes(canonicalContext);
        var hash = Convert.ToHexStringLower(SHA256.HashData(context));
        using var command = Command(null, "INSERT INTO job(id,kind,captured_context,context_hash) VALUES($job,$kind,$context,$hash)",
            ("$job", Key(jobId)), ("$kind", kind), ("$context", Encoding.UTF8.GetString(context)), ("$hash", hash));
        command.ExecuteNonQuery();
    }

    public void AddItem(Guid jobId, JobItemSpec item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (jobId == Guid.Empty || item.Id == Guid.Empty || !Hash(item.InputFingerprint) || item.Stages.Count is < 1 or > 32)
            throw new ArgumentException("Invalid captured job item.", nameof(item));
        var stages = item.Stages.Take(33).Select(stage => stage with { Dependencies = Array.AsReadOnly(stage.Dependencies.Take(32).ToArray()) }).ToArray();
        ValidateGraph(stages);
        using var transaction = _database.Connection.BeginTransaction();
        using (var itemCommand = Command(transaction, "INSERT INTO job_item(job_id,id,input_fingerprint) VALUES($job,$item,$input)",
            ("$job", Key(jobId)), ("$item", Key(item.Id)), ("$input", item.InputFingerprint))) itemCommand.ExecuteNonQuery();
        foreach (var stage in stages)
        {
            using var command = Command(transaction, "INSERT INTO item_stage(job_id,item_id,name,recipe_hash,state) VALUES($job,$item,$stage,$recipe,'queued')",
                ("$job", Key(jobId)), ("$item", Key(item.Id)), ("$stage", stage.Name), ("$recipe", stage.RecipeSha256));
            command.ExecuteNonQuery();
        }
        foreach (var stage in stages)
            foreach (var parent in stage.Dependencies)
            {
                using var command = Command(transaction, "INSERT INTO stage_dependency VALUES($job,$item,$stage,$parent)",
                    ("$job", Key(jobId)), ("$item", Key(item.Id)), ("$stage", stage.Name), ("$parent", parent));
                command.ExecuteNonQuery();
            }
        transaction.Commit();
    }

    public StageLease? ClaimNext()
    {
        using var transaction = _database.Connection.BeginTransaction();
        using var select = Command(transaction, """
            SELECT s.job_id,s.item_id,s.name,s.attempt,j.captured_context,s.checkpoint
            FROM item_stage s JOIN job j ON j.id=s.job_id
            WHERE s.state='queued' AND j.paused=0 AND j.canceled=0
              AND NOT EXISTS(SELECT 1 FROM stage_dependency d JOIN item_stage parent
                ON parent.job_id=d.job_id AND parent.item_id=d.item_id AND parent.name=d.depends_on
                WHERE d.job_id=s.job_id AND d.item_id=s.item_id AND d.stage_name=s.name AND parent.state!='ready')
            ORDER BY s.job_id,s.item_id,s.name LIMIT 1
            """);
        Guid job; Guid item; string name; int attempt; string context; string? checkpoint;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) return null;
            job = Guid.Parse(reader.GetString(0)); item = Guid.Parse(reader.GetString(1)); name = reader.GetString(2);
            attempt = checked(reader.GetInt32(3) + 1); context = reader.GetString(4); checkpoint = reader.IsDBNull(5) ? null : reader.GetString(5);
        }
        var lease = Guid.NewGuid();
        using var update = Command(transaction, "UPDATE item_stage SET state='running',attempt=$attempt,lease_id=$lease WHERE job_id=$job AND item_id=$item AND name=$stage AND state='queued'",
            ("$attempt", attempt), ("$lease", Key(lease)), ("$job", Key(job)), ("$item", Key(item)), ("$stage", name));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Serial job claim lost its selected row.");
        transaction.Commit();
        return new(job, item, name, lease, attempt, context, checkpoint);
    }

    public void SaveCheckpoint(StageLease lease, ReadOnlySpan<byte> checkpoint)
    {
        var canonical = CanonicalObject(checkpoint);
        using var command = LeaseCommand(null, lease, "UPDATE item_stage SET checkpoint=$checkpoint " + LeaseWhere,
            ("$checkpoint", canonical));
        if (command.ExecuteNonQuery() != 1) throw new StaleStageLeaseException();
    }

    public void Complete(StageLease lease, ReadOnlySpan<byte> verifiedReceipt)
    {
        var canonical = CanonicalObject(verifiedReceipt);
        using var transaction = _database.Connection.BeginTransaction();
        using var command = LeaseCommand(transaction, lease,
            "UPDATE item_stage SET state='ready',result_receipt=$receipt,lease_id=NULL,error_code=NULL,error_message=NULL,retryable=0 " + LeaseWhere,
            ("$receipt", canonical));
        if (command.ExecuteNonQuery() != 1) throw new StaleStageLeaseException();
        using var outbox = Command(transaction, "INSERT INTO state_outbox(id,job_id,item_id,stage_name,receipt) VALUES($id,$job,$item,$stage,$receipt)",
            ("$id", Key(lease.LeaseId)), ("$job", Key(lease.JobId)), ("$item", Key(lease.ItemId)), ("$stage", lease.Stage), ("$receipt", canonical));
        outbox.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Fail(StageLease lease, string code, string message, bool retryable, bool unsupported = false)
    {
        if (!Identifier(code) || string.IsNullOrWhiteSpace(message) || message.Length > 2048 || unsupported && retryable)
            throw new ArgumentException("Invalid stage failure classification.");
        using var command = LeaseCommand(null, lease,
            "UPDATE item_stage SET state=$state,error_code=$code,error_message=$message,retryable=$retryable,lease_id=NULL " + LeaseWhere,
            ("$state", unsupported ? "unsupported" : "failed"), ("$code", code), ("$message", message), ("$retryable", retryable ? 1 : 0));
        if (command.ExecuteNonQuery() != 1) throw new StaleStageLeaseException();
    }

    public int RetryFailed(Guid jobId)
    {
        using var command = Command(null, """
            UPDATE item_stage SET state='queued',lease_id=NULL
            WHERE job_id=$job AND state='failed' AND retryable=1
              AND EXISTS(SELECT 1 FROM job WHERE id=$job AND canceled=0)
            """, ("$job", Key(jobId)));
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<StageLease> Pause(Guid jobId, bool cancelJob = false)
    {
        using var transaction = _database.Connection.BeginTransaction();
        using (var update = Command(transaction, "UPDATE job SET paused=$paused,canceled=$canceled WHERE id=$job AND canceled=0",
            ("$paused", cancelJob ? 0 : 1), ("$canceled", cancelJob ? 1 : 0), ("$job", Key(jobId))))
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Unknown or already canceled job.");
        using (var fence = Command(transaction, "UPDATE item_stage SET state='canceling' WHERE job_id=$job AND state='running'", ("$job", Key(jobId)))) fence.ExecuteNonQuery();
        if (cancelJob)
        {
            using var cancel = Command(transaction, "UPDATE item_stage SET state='canceled' WHERE job_id=$job AND state IN('queued','failed')", ("$job", Key(jobId)));
            cancel.ExecuteNonQuery();
        }
        var leases = new List<StageLease>();
        using (var select = Command(transaction, """
            SELECT s.item_id,s.name,s.lease_id,s.attempt,j.captured_context,s.checkpoint FROM item_stage s JOIN job j ON j.id=s.job_id
            WHERE s.job_id=$job AND s.state='canceling' ORDER BY s.item_id,s.name
            """, ("$job", Key(jobId))))
        using (var reader = select.ExecuteReader())
            while (reader.Read()) leases.Add(new(jobId, Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                reader.GetInt32(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        transaction.Commit();
        return leases.AsReadOnly();
    }

    /// <summary>Only the worker process lease owner calls this AFTER the exact attempt has exited
    /// and its pipes/native/cache readers are joined. A UI cancel acknowledgement is insufficient.</summary>
    public void ReleaseStoppedAttempt(StageLease lease)
    {
        using var command = LeaseCommand(null, lease, """
            UPDATE item_stage SET state=CASE WHEN (SELECT canceled FROM job WHERE id=$job)=1 THEN 'canceled' ELSE 'queued' END,lease_id=NULL
            WHERE job_id=$job AND item_id=$item AND name=$stage AND lease_id=$lease AND attempt=$attempt AND state='canceling'
            """);
        if (command.ExecuteNonQuery() != 1) throw new StaleStageLeaseException();
    }

    public void Resume(Guid jobId)
    {
        using var command = Command(null, """
            UPDATE job SET paused=0 WHERE id=$job AND canceled=0
              AND NOT EXISTS(SELECT 1 FROM item_stage WHERE job_id=$job AND state='canceling')
            """, ("$job", Key(jobId)));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Job is canceled, missing, or its workers have not finished stopping.");
    }

    public IReadOnlyList<PendingStageReceipt> ReadPendingReceipts(int limit = 60)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using var command = Command(null, "SELECT id,job_id,item_id,stage_name,receipt FROM state_outbox WHERE published=0 ORDER BY id LIMIT $limit", ("$limit", limit));
        using var reader = command.ExecuteReader();
        var receipts = new List<PendingStageReceipt>();
        while (reader.Read()) receipts.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4)));
        return receipts.AsReadOnly();
    }

    public void MarkReceiptPublished(Guid id)
    {
        using var command = Command(null, "UPDATE state_outbox SET published=1 WHERE id=$id", ("$id", Key(id)));
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Unknown publication receipt.");
    }

    public JobSummary Summary(Guid jobId)
    {
        using var command = Command(null, """
            SELECT j.paused,j.canceled,s.state,count(s.name) FROM job j LEFT JOIN item_stage s ON s.job_id=j.id
            WHERE j.id=$job GROUP BY j.paused,j.canceled,s.state
            """, ("$job", Key(jobId)));
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        bool paused = false, canceled = false, found = false;
        while (reader.Read())
        {
            found = true; paused = reader.GetBoolean(0); canceled = reader.GetBoolean(1);
            if (!reader.IsDBNull(2)) counts[reader.GetString(2)] = reader.GetInt32(3);
        }
        if (!found) throw new KeyNotFoundException("Job does not exist.");
        return new(counts.GetValueOrDefault("queued"), counts.GetValueOrDefault("running") + counts.GetValueOrDefault("canceling"),
            counts.GetValueOrDefault("ready"), counts.GetValueOrDefault("failed"), counts.GetValueOrDefault("unsupported"), counts.GetValueOrDefault("canceled"), paused, canceled);
    }

    public int PendingReceiptCount()
    {
        using var command = Command(null, "SELECT count(*) FROM state_outbox WHERE published=0");
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private const string LeaseWhere = """
        WHERE job_id=$job AND item_id=$item AND name=$stage AND lease_id=$lease AND attempt=$attempt AND state='running'
          AND EXISTS(SELECT 1 FROM job WHERE id=$job AND paused=0 AND canceled=0)
        """;

    private SqliteCommand LeaseCommand(SqliteTransaction? transaction, StageLease lease, string sql, params (string Name, object Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Command(transaction, sql, values.Concat(new (string Name, object Value)[] {
            ("$job", Key(lease.JobId)), ("$item", Key(lease.ItemId)), ("$stage", lease.Stage), ("$lease", Key(lease.LeaseId)), ("$attempt", lease.Attempt) }).ToArray());
    }

    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = _database.Connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    private static string Key(Guid id) => id != Guid.Empty ? id.ToString("D") : throw new ArgumentException("Identity is required.", nameof(id));
    private string CanonicalObject(ReadOnlySpan<byte> bytes)
    {
        var canonical = _codec.Encode(bytes);
        using var document = JsonDocument.Parse(canonical);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Durable job context/checkpoint/receipt must be an object.", nameof(bytes));
        return Encoding.UTF8.GetString(canonical);
    }
    private static bool Identifier(string value) => !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(character =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');
    private static bool Hash(string value) => value is not null && value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateGraph(JobStageSpec[] stages)
    {
        if (stages.Length is < 1 or > 32) throw new ArgumentException("Stage graph exceeds its bound.", nameof(stages));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in stages)
            if (stage is null || !Identifier(stage.Name) || !Hash(stage.RecipeSha256) || !names.Add(stage.Name) || stage.Dependencies.Count > 31 ||
                stage.Dependencies.Distinct(StringComparer.Ordinal).Count() != stage.Dependencies.Count)
                throw new ArgumentException("Invalid stage/recipe/dependency identity.", nameof(stages));
        if (stages.Any(stage => stage.Dependencies.Any(parent => parent == stage.Name || !names.Contains(parent))))
            throw new ArgumentException("Unknown/self stage dependency.", nameof(stages));
        var ready = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var added = false;
            foreach (var stage in stages)
                if (!ready.Contains(stage.Name) && stage.Dependencies.All(ready.Contains)) { ready.Add(stage.Name); added = true; }
            if (!added) break;
        }
        if (ready.Count != stages.Length) throw new ArgumentException("Stage dependency cycle.", nameof(stages));
    }
}
