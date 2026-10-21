using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record JobStageSpec(string Name, string RecipeSha256, IReadOnlyList<string> Dependencies);
public sealed record JobItemSpec(Guid Id, string InputFingerprint, IReadOnlyList<JobStageSpec> Stages, string CapturedContext = "{}");
public sealed record StageLease(Guid JobId, Guid ItemId, string Stage, Guid LeaseId, int Attempt, string CapturedContext, string? Checkpoint, string CapturedItemContext = "{}");
public sealed record JobSummary(int Queued, int Running, int Ready, int Failed, int Unsupported, int Canceled, bool Paused, bool IsCanceled);
public sealed record PendingStageReceipt(Guid Id, Guid JobId, Guid ItemId, string Stage, string Receipt);
public sealed class StaleStageLeaseException() : InvalidOperationException("Stage lease is no longer current; retain/reconcile receipts and do not publish stale results.");
public sealed class ConflictingJobItemException() : InvalidOperationException("The durable item identity already binds different input, context or recipes. Existing work was retained.");

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
        InsertJob(null, jobId, kind, canonicalContext);
    }

    public void CreateWithInitialItem(Guid jobId, string kind, ReadOnlySpan<byte> capturedContext, JobItemSpec item)
    {
        if (jobId == Guid.Empty || !Identifier(kind)) throw new ArgumentException("Invalid job identity/kind.");
        var canonical = CanonicalObject(capturedContext); var prepared = Prepare(item);
        using var transaction = _database.Connection.BeginTransaction();
        InsertJob(transaction, jobId, kind, canonical); InsertItem(transaction, jobId, prepared); transaction.Commit();
    }
    private void InsertJob(SqliteTransaction? transaction, Guid jobId, string kind, string canonicalContext)
    {
        var context = Encoding.UTF8.GetBytes(canonicalContext);
        var hash = Convert.ToHexStringLower(SHA256.HashData(context));
        using var command = Command(transaction, "INSERT INTO job(id,kind,captured_context,context_hash) VALUES($job,$kind,$context,$hash)",
            ("$job", Key(jobId)), ("$kind", kind), ("$context", Encoding.UTF8.GetString(context)), ("$hash", hash));
        command.ExecuteNonQuery();
    }

    public void AddItem(Guid jobId, JobItemSpec item)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job identity is required.", nameof(jobId));
        var prepared = Prepare(item);
        using var transaction = _database.Connection.BeginTransaction();
        InsertItem(transaction, jobId, prepared); transaction.Commit();
    }
    public bool EnsureItem(Guid jobId, JobItemSpec item)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job identity is required.", nameof(jobId));
        var prepared = Prepare(item);
        using var transaction = _database.Connection.BeginTransaction();
        var created = EnsureItem(transaction, jobId, prepared);
        transaction.Commit(); return created;
    }

    /// <summary>Record a failed observation without briefly admitting it as runnable work.
    /// Replays preserve its current retry/ready state instead of resetting successful work.</summary>
    public bool EnsureFailedItem(Guid jobId, JobItemSpec item, string code, string message)
    {
        if (jobId == Guid.Empty || !Identifier(code) || string.IsNullOrWhiteSpace(message) || message.Length > 2048)
            throw new ArgumentException("Invalid failed item classification.");
        var prepared = Prepare(item);
        if (prepared.Stages.Length != 1 || prepared.Stages[0].Dependencies.Count != 0)
            throw new ArgumentException("A failed observation requires one independent repair stage.", nameof(item));
        using var transaction = _database.Connection.BeginTransaction();
        var created = EnsureItem(transaction, jobId, prepared);
        if (created)
        {
            using var fail = Command(transaction, """
                UPDATE item_stage SET state='failed',error_code=$code,error_message=$message,retryable=1
                WHERE job_id=$job AND item_id=$item AND name=$stage AND state='queued' AND attempt=0
                """, ("$job", Key(jobId)), ("$item", Key(item.Id)), ("$stage", prepared.Stages[0].Name), ("$code", code), ("$message", message));
            if (fail.ExecuteNonQuery() != 1) throw new InvalidOperationException("Failed observation was not admitted atomically.");
        }
        transaction.Commit(); return created;
    }
    private bool EnsureItem(SqliteTransaction transaction, Guid jobId, PreparedItem prepared)
    {
        var exists = false;
        using (var select = Command(transaction, "SELECT input_fingerprint,captured_context FROM job_item WHERE job_id=$job AND id=$item",
            ("$job", Key(jobId)), ("$item", Key(prepared.Id))))
        {
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                exists = true;
                if (reader.GetString(0) != prepared.InputFingerprint || reader.GetString(1) != prepared.Context) throw new ConflictingJobItemException();
            }
        }
        if (!exists) InsertItem(transaction, jobId, prepared);
        else
        {
            var expected = new List<(string Name, string Recipe, string? Parent)>();
            foreach (var stage in prepared.Stages.OrderBy(stage => stage.Name, StringComparer.Ordinal))
                if (stage.Dependencies.Count == 0) expected.Add((stage.Name, stage.RecipeSha256, null));
                else foreach (var parent in stage.Dependencies.Order(StringComparer.Ordinal)) expected.Add((stage.Name, stage.RecipeSha256, parent));
            var actual = new List<(string Name, string Recipe, string? Parent)>();
            using var graph = Command(transaction, """
                SELECT s.name,s.recipe_hash,d.depends_on FROM item_stage s LEFT JOIN stage_dependency d
                  ON d.job_id=s.job_id AND d.item_id=s.item_id AND d.stage_name=s.name
                WHERE s.job_id=$job AND s.item_id=$item ORDER BY s.name,d.depends_on LIMIT 1025
                """, ("$job", Key(jobId)), ("$item", Key(prepared.Id)));
            using var reader = graph.ExecuteReader();
            while (reader.Read()) actual.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            if (!expected.SequenceEqual(actual)) throw new ConflictingJobItemException();
        }
        return !exists;
    }
    private PreparedItem Prepare(JobItemSpec item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id == Guid.Empty || !Hash(item.InputFingerprint) || item.Stages.Count is < 1 or > 32)
            throw new ArgumentException("Invalid captured job item.", nameof(item));
        var stages = item.Stages.Take(33).Select(stage => stage with { Dependencies = Array.AsReadOnly(stage.Dependencies.Take(32).ToArray()) }).ToArray();
        ValidateGraph(stages);
        return new(item.Id, item.InputFingerprint, stages, CanonicalObject(Encoding.UTF8.GetBytes(item.CapturedContext)));
    }
    private void InsertItem(SqliteTransaction transaction, Guid jobId, PreparedItem item)
    {
        using (var itemCommand = Command(transaction, "INSERT INTO job_item(job_id,id,input_fingerprint,captured_context) VALUES($job,$item,$input,$context)",
            ("$job", Key(jobId)), ("$item", Key(item.Id)), ("$input", item.InputFingerprint), ("$context", item.Context))) itemCommand.ExecuteNonQuery();
        foreach (var stage in item.Stages)
        {
            using var command = Command(transaction, "INSERT INTO item_stage(job_id,item_id,name,recipe_hash,state) VALUES($job,$item,$stage,$recipe,$state)",
                ("$job", Key(jobId)), ("$item", Key(item.Id)), ("$stage", stage.Name), ("$recipe", stage.RecipeSha256),
                ("$state", stage.Dependencies.Count == 0 ? "queued" : "waiting-dependency"));
            command.ExecuteNonQuery();
        }
        foreach (var stage in item.Stages)
            foreach (var parent in stage.Dependencies)
            {
                using var command = Command(transaction, "INSERT INTO stage_dependency VALUES($job,$item,$stage,$parent)",
                    ("$job", Key(jobId)), ("$item", Key(item.Id)), ("$stage", stage.Name), ("$parent", parent));
                command.ExecuteNonQuery();
            }
    }
    private sealed record PreparedItem(Guid Id, string InputFingerprint, JobStageSpec[] Stages, string Context);

    public StageLease? ClaimNext(string? kind = null, string? stage = null)
    {
        if (kind is not null && !Identifier(kind)) throw new ArgumentException("Invalid admitted job kind.", nameof(kind));
        if (stage is not null && !Identifier(stage)) throw new ArgumentException("Invalid admitted stage.", nameof(stage));
        using var transaction = _database.Connection.BeginTransaction();
        using var select = Command(transaction, """
            SELECT s.job_id,s.item_id,s.name,s.attempt,j.captured_context,s.checkpoint,i.captured_context
            FROM item_stage s JOIN job j ON j.id=s.job_id JOIN job_item i ON i.job_id=s.job_id AND i.id=s.item_id
            WHERE s.state='queued' AND j.paused=0 AND j.canceled=0 AND ($kind IS NULL OR j.kind=$kind) AND ($stage IS NULL OR s.name=$stage)
              AND NOT EXISTS(SELECT 1 FROM stage_dependency d JOIN item_stage parent
                ON parent.job_id=d.job_id AND parent.item_id=d.item_id AND parent.name=d.depends_on
                WHERE d.job_id=s.job_id AND d.item_id=s.item_id AND d.stage_name=s.name AND parent.state!='ready')
            ORDER BY s.job_id,s.item_id,s.name LIMIT 1
            """, ("$kind", kind is null ? DBNull.Value : kind), ("$stage", stage is null ? DBNull.Value : stage));
        Guid job; Guid item; string name; int attempt; string context; string? checkpoint; string itemContext;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) return null;
            job = Guid.Parse(reader.GetString(0)); item = Guid.Parse(reader.GetString(1)); name = reader.GetString(2);
            attempt = checked(reader.GetInt32(3) + 1); context = reader.GetString(4); checkpoint = reader.IsDBNull(5) ? null : reader.GetString(5);
            itemContext = reader.GetString(6);
        }
        var lease = Guid.NewGuid();
        using var update = Command(transaction, "UPDATE item_stage SET state='running',attempt=$attempt,lease_id=$lease WHERE job_id=$job AND item_id=$item AND name=$stage AND state='queued'",
            ("$attempt", attempt), ("$lease", Key(lease)), ("$job", Key(job)), ("$item", Key(item)), ("$stage", name));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Serial job claim lost its selected row.");
        transaction.Commit();
        return new(job, item, name, lease, attempt, context, checkpoint, itemContext);
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
        using (var unblock = Command(transaction, """
            UPDATE item_stage SET state='queued' WHERE job_id=$job AND item_id=$item AND state='waiting-dependency'
              AND NOT EXISTS(SELECT 1 FROM stage_dependency d JOIN item_stage parent
                ON parent.job_id=d.job_id AND parent.item_id=d.item_id AND parent.name=d.depends_on
                WHERE d.job_id=item_stage.job_id AND d.item_id=item_stage.item_id AND d.stage_name=item_stage.name AND parent.state!='ready')
            """, ("$job", Key(lease.JobId)), ("$item", Key(lease.ItemId)))) unblock.ExecuteNonQuery();
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
            using var cancel = Command(transaction, "UPDATE item_stage SET state='canceled' WHERE job_id=$job AND state IN('queued','waiting-dependency','failed')", ("$job", Key(jobId)));
            cancel.ExecuteNonQuery();
        }
        var leases = new List<StageLease>();
        using (var select = Command(transaction, """
            SELECT s.item_id,s.name,s.lease_id,s.attempt,j.captured_context,s.checkpoint,i.captured_context FROM item_stage s JOIN job j ON j.id=s.job_id
            JOIN job_item i ON i.job_id=s.job_id AND i.id=s.item_id
            WHERE s.job_id=$job AND s.state='canceling' ORDER BY s.item_id,s.name
            """, ("$job", Key(jobId))))
        using (var reader = select.ExecuteReader())
            while (reader.Read()) leases.Add(new(jobId, Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                reader.GetInt32(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
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

    /// <summary>Yield a bounded metadata operation only after its actual network/read owner
    /// has joined. A concurrent pause/cancel retains its durable job policy and checkpoint.</summary>
    public void ReturnJoinedRead(StageLease lease)
    {
        using var command = LeaseCommand(null, lease, """
            UPDATE item_stage SET state=CASE WHEN (SELECT canceled FROM job WHERE id=$job)=1 THEN 'canceled' ELSE 'queued' END,lease_id=NULL
            WHERE job_id=$job AND item_id=$item AND name=$stage AND lease_id=$lease AND attempt=$attempt AND state IN('running','canceling')
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
        return new(counts.GetValueOrDefault("queued") + counts.GetValueOrDefault("waiting-dependency"), counts.GetValueOrDefault("running") + counts.GetValueOrDefault("canceling"),
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
