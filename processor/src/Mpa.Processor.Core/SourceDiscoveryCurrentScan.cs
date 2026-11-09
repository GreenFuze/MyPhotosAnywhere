using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class SourceDiscoveryRepository
{
    private Guid? CurrentScan(SourceDiscoveryScope expected)
    {
        using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT c.job_id,c.source_revision,c.account_revision,j.kind,j.captured_context,j.context_hash,
              EXISTS(SELECT 1 FROM item_stage s WHERE s.job_id=c.job_id)
            FROM current_source_scan c LEFT JOIN job j ON j.id=c.job_id WHERE c.source_id=$source
            """;
        command.Parameters.AddWithValue("$source", expected.SourceId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5) || !reader.GetBoolean(6) ||
            !Guid.TryParseExact(reader.GetString(0), "D", out var job) || job == Guid.Empty || reader.GetString(0) != job.ToString("D") ||
            reader.GetString(3) != JobKind || reader.GetString(5) != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reader.GetString(4)))))
            throw new InvalidDataException("Current source scan does not match its durable job identity/integrity record.");
        var scope = Scope(reader.GetString(4));
        if (scope.SourceId != expected.SourceId || scope.SourceRevision != reader.GetInt64(1) || scope.AccountRevision != reader.GetInt64(2) ||
            scope.AccountId != expected.AccountId || scope.RootFolderId != expected.RootFolderId ||
            scope.SourceRevision == expected.SourceRevision && scope.IncludeDescendants != expected.IncludeDescendants)
            throw new InvalidDataException("Current source scan does not match its captured source/account revisions.");
        return scope == expected ? job : null;
    }

    private void RecordCurrentScan(SqliteTransaction transaction, SourceDiscoveryScope scope, Guid jobId)
    {
        using var command = _database.Connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO current_source_scan(source_id,job_id,source_revision,account_revision)
            SELECT s.id,$job,s.revision,a.revision FROM source_binding s JOIN drive_account a ON a.permission_id=s.account_id
            WHERE s.id=$source AND s.account_id=$account AND s.folder_id=$folder AND s.descendants=$descendants
              AND s.enabled=1 AND s.revision=$sourceRevision AND a.connected=1 AND a.revision=$accountRevision
            ON CONFLICT(source_id) DO UPDATE SET job_id=excluded.job_id,source_revision=excluded.source_revision,account_revision=excluded.account_revision
            """;
        command.Parameters.AddWithValue("$job", jobId.ToString("D")); command.Parameters.AddWithValue("$source", scope.SourceId.ToString("D"));
        command.Parameters.AddWithValue("$account", scope.AccountId); command.Parameters.AddWithValue("$folder", scope.RootFolderId);
        command.Parameters.AddWithValue("$descendants", scope.IncludeDescendants); command.Parameters.AddWithValue("$sourceRevision", scope.SourceRevision);
        command.Parameters.AddWithValue("$accountRevision", scope.AccountRevision);
        if (command.ExecuteNonQuery() != 1) throw new StaleLibraryConfigurationException();
    }
    private sealed class ScanAdmission(SourceDiscoveryRepository owner, SourceDiscoveryScope scope) : IDurableJobAdmission
    {
        public void Record(SqliteTransaction transaction, Guid jobId) => owner.RecordCurrentScan(transaction, scope, jobId);
    }
}
