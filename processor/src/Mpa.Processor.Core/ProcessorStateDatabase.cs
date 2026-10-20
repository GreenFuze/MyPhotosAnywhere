using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

/// <summary>Owns the local single-instance lease and SQLite connection. All calls belong to the
/// coordinator's serial background queue, never the UI thread. No source photo path is accepted.</summary>
public sealed class ProcessorStateDatabase : IDisposable
{
    private const int ApplicationId = 0x4d504130;
    private readonly FileStream _instanceLease;
    private readonly SqliteConnection _connection;
    private bool _disposed;
    internal SqliteConnection Connection => !_disposed ? _connection : throw new ObjectDisposedException(nameof(ProcessorStateDatabase));
    public string SqliteVersion { get; }
    public Guid InstallationId { get; }

    public ProcessorStateDatabase(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var root = Path.GetFullPath(stateDirectory);
        if (Path.TrimEndingDirectorySeparator(root) == Path.GetPathRoot(root)) throw new ArgumentException("A dedicated local state directory is required.", nameof(stateDirectory));
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Processor state directory cannot be a link/reparse point.");
        foreach (var name in new[] { "processor.lock", "processor.db", "processor.db-wal", "processor.db-shm" })
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Processor state/sidecar cannot be a link: " + name);
        }
        var lease = new FileStream(Path.Combine(root, "processor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SqliteConnection? connection = null;
        try
        {
            InstallationId = ValidateOrCreateMarker(root);
            var path = Path.Combine(root, "processor.db");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Processor database cannot be a link.");
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, DefaultTimeout = 5 }.ToString());
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA application_id";
                var application = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                command.CommandText = "PRAGMA user_version";
                var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'";
                var objects = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (application != ApplicationId && !(application == 0 && version == 0 && objects == 0))
                    throw new InvalidDataException("Unknown database identity; preserve it and choose an empty Processor state directory.");
                if (version is not (0 or 1 or 2 or 3 or 4 or 5)) throw new InvalidDataException("Unsupported Processor state version; update the Processor without deleting its state.");
                command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA journal_mode=WAL";
                if (!string.Equals(command.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Durable WAL mode was not established.");
                command.CommandText = "SELECT sqlite_version()";
                SqliteVersion = command.ExecuteScalar() as string ?? throw new InvalidDataException("SQLite did not identify its runtime version.");
                if (version == 0)
                {
                    using var transaction = connection.BeginTransaction();
                    command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE processor_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                        INSERT INTO processor_meta VALUES('format','mpa-processor-state');
                        CREATE TABLE job(
                          id TEXT PRIMARY KEY, kind TEXT NOT NULL, captured_context TEXT NOT NULL,
                          context_hash TEXT NOT NULL, paused INTEGER NOT NULL DEFAULT 0 CHECK(paused IN(0,1)),
                          canceled INTEGER NOT NULL DEFAULT 0 CHECK(canceled IN(0,1))
                        ) STRICT;
                        CREATE TABLE job_item(
                          job_id TEXT NOT NULL REFERENCES job(id), id TEXT NOT NULL, input_fingerprint TEXT NOT NULL,
                          PRIMARY KEY(job_id,id)
                        ) STRICT;
                        CREATE TABLE item_stage(
                          job_id TEXT NOT NULL, item_id TEXT NOT NULL, name TEXT NOT NULL, recipe_hash TEXT NOT NULL,
                          state TEXT NOT NULL CHECK(state IN('queued','waiting-dependency','running','canceling','ready','failed','unsupported','canceled')),
                          attempt INTEGER NOT NULL DEFAULT 0, lease_id TEXT, checkpoint TEXT, result_receipt TEXT,
                          error_code TEXT, error_message TEXT, retryable INTEGER NOT NULL DEFAULT 0 CHECK(retryable IN(0,1)),
                          PRIMARY KEY(job_id,item_id,name), FOREIGN KEY(job_id,item_id) REFERENCES job_item(job_id,id)
                        ) STRICT;
                        CREATE TABLE stage_dependency(
                          job_id TEXT NOT NULL, item_id TEXT NOT NULL, stage_name TEXT NOT NULL, depends_on TEXT NOT NULL,
                          PRIMARY KEY(job_id,item_id,stage_name,depends_on),
                          FOREIGN KEY(job_id,item_id,stage_name) REFERENCES item_stage(job_id,item_id,name),
                          FOREIGN KEY(job_id,item_id,depends_on) REFERENCES item_stage(job_id,item_id,name)
                        ) STRICT;
                        CREATE INDEX stage_ready_queue ON item_stage(state,job_id,item_id,name);
                        CREATE TABLE state_outbox(
                          id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES job(id), item_id TEXT NOT NULL,
                          stage_name TEXT NOT NULL, receipt TEXT NOT NULL, published INTEGER NOT NULL DEFAULT 0 CHECK(published IN(0,1))
                        ) STRICT;
                        PRAGMA application_id=1297105200;
                        PRAGMA user_version=1;
                        """;
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
                if (version < 2)
                {
                    using var transaction = connection.BeginTransaction();
                    command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE drive_account(
                          permission_id TEXT PRIMARY KEY, connection_id TEXT NOT NULL UNIQUE,
                          display_name TEXT NOT NULL, email_address TEXT,
                          connected INTEGER NOT NULL CHECK(connected IN(0,1)),
                          revision INTEGER NOT NULL CHECK(revision>0)
                        ) STRICT;
                        CREATE TABLE pending_drive_connection(
                          id TEXT PRIMARY KEY, expected_permission_id TEXT UNIQUE REFERENCES drive_account(permission_id),
                          expected_revision INTEGER
                        ) STRICT;
                        CREATE TABLE credential_cleanup(
                          connection_id TEXT PRIMARY KEY, reason TEXT NOT NULL CHECK(reason IN('reconnected','disconnected','abandoned'))
                        ) STRICT;
                        PRAGMA user_version=2;
                        """;
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
                if (version < 3)
                {
                    using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE library_configuration(
                          id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL CHECK(revision>=1)
                        ) STRICT;
                        INSERT INTO library_configuration VALUES(1,1);
                        CREATE TABLE source_binding(
                          id TEXT PRIMARY KEY, account_id TEXT NOT NULL REFERENCES drive_account(permission_id),
                          folder_id TEXT NOT NULL, display_name TEXT NOT NULL,
                          descendants INTEGER NOT NULL CHECK(descendants IN(0,1)),
                          enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
                          revision INTEGER NOT NULL CHECK(revision>=1), UNIQUE(account_id,folder_id)
                        ) STRICT;
                        CREATE TABLE configuration_outbox(
                          revision INTEGER PRIMARY KEY, snapshot TEXT NOT NULL,
                          published INTEGER NOT NULL DEFAULT 0 CHECK(published IN(0,1))
                        ) STRICT;
                        PRAGMA user_version=3;
                        """;
                    command.ExecuteNonQuery(); transaction.Commit();
                }
                if (version < 4)
                {
                    using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE discovered_asset(
                          id TEXT PRIMARY KEY, account_id TEXT NOT NULL REFERENCES drive_account(permission_id),
                          file_id TEXT NOT NULL, drive_version INTEGER NOT NULL CHECK(drive_version>0),
                          name TEXT NOT NULL, mime_type TEXT NOT NULL, width INTEGER, height INTEGER,
                          captured_utc INTEGER, raw_capture TEXT, modified_utc INTEGER,
                          date_unknown INTEGER NOT NULL CHECK(date_unknown IN(0,1)), date_sort INTEGER NOT NULL,
                          facts_hash TEXT NOT NULL, UNIQUE(account_id,file_id)
                        ) STRICT;
                        CREATE INDEX discovered_timeline ON discovered_asset(date_unknown,date_sort,id);
                        CREATE TABLE source_asset(
                          source_id TEXT NOT NULL REFERENCES source_binding(id),
                          asset_id TEXT NOT NULL REFERENCES discovered_asset(id),
                          depth INTEGER NOT NULL CHECK(depth>=0 AND depth<=256),
                          PRIMARY KEY(source_id,asset_id)
                        ) STRICT;
                        CREATE INDEX asset_sources ON source_asset(asset_id,source_id,depth);
                        CREATE TABLE discovery_revision(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL CHECK(revision>=1)) STRICT;
                        INSERT INTO discovery_revision VALUES(1,1);
                        PRAGMA user_version=4;
                        """;
                    command.ExecuteNonQuery(); transaction.Commit();
                }
                if (version < 5)
                {
                    using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
                    command.CommandText = "ALTER TABLE job_item ADD COLUMN captured_context TEXT NOT NULL DEFAULT '{}'; PRAGMA user_version=5;";
                    command.ExecuteNonQuery(); transaction.Commit();
                }
            }
            _instanceLease = lease;
            _connection = connection;
        }
        catch
        {
            connection?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    private static Guid ValidateOrCreateMarker(string root)
    {
        var path = Path.Combine(root, "mpa-processor-state.json");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("State marker cannot be a link.");
            using var input = File.OpenRead(path);
            if (input.Length is < 1 or > 4096) throw new InvalidDataException("Invalid state directory marker size.");
            using var marker = JsonDocument.Parse(input);
            if (marker.RootElement.GetProperty("format").GetString() != "mpa-processor-state" ||
                marker.RootElement.GetProperty("major").GetInt32() != 1 || !Guid.TryParseExact(marker.RootElement.GetProperty("installationId").GetString(), "D", out var id) || id == Guid.Empty)
                throw new InvalidDataException("Unknown Processor state directory marker.");
            return id;
        }
        if (Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) != "processor.lock"))
            throw new InvalidDataException("Refusing to claim a directory containing unrecognized files.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var installation = Guid.NewGuid();
        JsonSerializer.Serialize(output, new { format = "mpa-processor-state", major = 1, installationId = installation.ToString("D") });
        output.Flush(flushToDisk: true);
        return installation;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _connection.Dispose(); }
        finally { _instanceLease.Dispose(); }
    }
}
