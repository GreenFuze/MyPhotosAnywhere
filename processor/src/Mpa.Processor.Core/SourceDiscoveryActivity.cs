using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mpa.Processor.Core;

public sealed record SourceDiscoveryProblem(Guid ItemId, string Stage, string Code, string Message, bool Retryable);
public sealed record SourceDiscoveryActivity(Guid JobId, SourceDiscoveryScope Scope, bool Current, JobSummary Summary,
    IReadOnlyList<SourceDiscoveryProblem> Problems, bool MoreProblems, int RetryableFailures);
public sealed record SourceDiscoveryActivityPage(IReadOnlyList<SourceDiscoveryActivity> Jobs, Guid? NextCursor);
public sealed record SourceScanRequest(Guid RequestId, Guid SourceId, long SourceRevision, long AccountRevision);
public sealed record SourceRetryRequest(Guid JobId, Guid SourceId, long SourceRevision, long AccountRevision);

public sealed partial class SourceDiscoveryRepository
{
    /// <summary>Client-generated request identity makes a lost HTTP reply safe to replay.
    /// No network call or replacement of completed work occurs during admission.</summary>
    public SourceDiscoverySchedule RequestScan(SourceScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty) throw new ArgumentException("A scan request identity is required.", nameof(request));
        var scope = CaptureScope(request.SourceId, request.SourceRevision, request.AccountRevision);
        return new(request.RequestId, _jobs.EnsureJobWithInitialItem(request.RequestId, JobKind, ScopeContext(scope), VerificationItem(scope)));
    }

    public int RetryFailed(SourceRetryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = ReadScope(request.JobId) ?? throw new StaleLibraryConfigurationException();
        if (scope.SourceId != request.SourceId || scope.SourceRevision != request.SourceRevision || scope.AccountRevision != request.AccountRevision)
            throw new StaleLibraryConfigurationException();
        ValidateCurrent(scope);
        return _jobs.RetryFailed(request.JobId);
    }

    /// <summary>Bounded keyset history, deliberately ordered by identity rather than claiming
    /// a creation timestamp that older jobs did not persist. Counts are processing steps,
    /// not photo totals. Existing checkpoints and errors remain owned by their jobs.</summary>
    public SourceDiscoveryActivityPage ReadActivity(Guid? after = null, int limit = 30)
    {
        if (after == Guid.Empty || limit is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(limit));
        var jobs = new List<(Guid Id, string Context, string Hash)>();
        using (var command = _database.Connection.CreateCommand())
        {
            command.CommandText = "SELECT id,captured_context,context_hash FROM job WHERE kind=$kind AND id>$after ORDER BY id LIMIT $limit";
            command.Parameters.AddWithValue("$kind", JobKind); command.Parameters.AddWithValue("$after", after?.ToString("D") ?? "");
            command.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read()) jobs.Add((Guid.ParseExact(reader.GetString(0), "D"), reader.GetString(1), reader.GetString(2)));
        }
        var more = jobs.Count > limit; if (more) jobs.RemoveAt(jobs.Count - 1);
        var result = new List<SourceDiscoveryActivity>();
        foreach (var job in jobs)
        {
            var scope = CheckedScope(job.Context, job.Hash); var current = true;
            try { ValidateCurrent(scope); } catch (StaleLibraryConfigurationException) { current = false; }
            var problems = new List<SourceDiscoveryProblem>();
            using (var command = _database.Connection.CreateCommand())
            {
                command.CommandText = "SELECT item_id,name,error_code,error_message,retryable FROM item_stage WHERE job_id=$job AND state='failed' ORDER BY item_id,name LIMIT 6";
                command.Parameters.AddWithValue("$job", job.Id.ToString("D"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(2) || reader.IsDBNull(3)) throw new InvalidDataException("A failed source step lost its actionable diagnostic.");
                    problems.Add(new(Guid.ParseExact(reader.GetString(0), "D"), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
                }
            }
            var moreProblems = problems.Count > 5; if (moreProblems) problems.RemoveAt(problems.Count - 1);
            using var retryable = _database.Connection.CreateCommand();
            retryable.CommandText = "SELECT count(*) FROM item_stage WHERE job_id=$job AND state='failed' AND retryable=1";
            retryable.Parameters.AddWithValue("$job", job.Id.ToString("D"));
            result.Add(new(job.Id, scope, current, _jobs.Summary(job.Id), problems.AsReadOnly(), moreProblems,
                Convert.ToInt32(retryable.ExecuteScalar(), CultureInfo.InvariantCulture)));
        }
        return new(result.AsReadOnly(), more ? jobs[^1].Id : null);
    }
    private SourceDiscoveryScope? ReadScope(Guid job)
    {
        using var command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT captured_context,context_hash FROM job WHERE id=$job AND kind=$kind";
        command.Parameters.AddWithValue("$job", job.ToString("D")); command.Parameters.AddWithValue("$kind", JobKind);
        using var reader = command.ExecuteReader(); return reader.Read() ? CheckedScope(reader.GetString(0), reader.GetString(1)) : null;
    }
    private SourceDiscoveryScope CheckedScope(string context, string hash)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(context))) != hash)
            throw new InvalidDataException("A source job's captured scope no longer matches its integrity record.");
        try { return Scope(context); }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("A persisted source job contains an invalid captured scope.", error); }
    }
}
