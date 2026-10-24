using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed record CatalogSessionEntry(long Position, Guid AssetId);
public sealed class CatalogSessionSelectionUnavailableException() : IOException("The selected photo is no longer visible in this gallery. Its identity was retained; no replacement was opened.");
public sealed class CatalogSessionCaptureException(Exception cause, CatalogViewerSessionIndex retainedOwner)
    : Exception("Viewer indexing failed and its private index cleanup is not confirmed; ownership is retained.", cause)
{ public CatalogViewerSessionIndex RetainedOwner { get; } = retainedOwner; }

/// <summary>A disposable private SQLite identity sequence, separate from durable media state.
/// Serial owner only. Capture uses an owned read-only WAL snapshot outside the source
/// coordinator; rows stream into a bounded-cache SQLite index, never a full-library list.</summary>
public sealed class CatalogViewerSessionIndex : IDisposable
{
    private const int ApplicationId = 0x4d504156;
    private readonly string _path;
    private SqliteConnection? _connection;
    private StorageReservation? _storage;
    private bool _ownsFile;
    private bool _closed;
    public long Count { get; private set; }
    public long SelectedPosition { get; private set; } = -1;
    public bool ExitConfirmed => _closed;
    private CatalogViewerSessionIndex(string path) => _path = Path.GetFullPath(path);

    public static CatalogViewerSessionIndex Capture(ProcessorStateDatabase source, string ownedIndexPath, Guid selected, string? accountId,
        StorageBudgetManager storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var snapshot = source.OpenReadSnapshot();
        return Capture(snapshot, ownedIndexPath, selected, accountId, storage, cancellationToken);
    }
    public static CatalogViewerSessionIndex Capture(ProcessorStateReadSnapshot source, string ownedIndexPath, Guid selected, string? accountId,
        StorageBudgetManager storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedIndexPath);
        if (selected == Guid.Empty) throw new ArgumentException("A selected canonical media identity is required.", nameof(selected));
        if (accountId is not null && (accountId.Length is < 1 or > 256 || !accountId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Invalid captured account filter.", nameof(accountId));
        var owner = new CatalogViewerSessionIndex(ownedIndexPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(owner._path) ?? throw new ArgumentException("A private session directory is required.", nameof(ownedIndexPath));
            if (!Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The owned viewer session directory is missing or linked.");
            if (File.Exists(owner._path + "-journal") || File.Exists(owner._path + "-wal") || File.Exists(owner._path + "-shm"))
                throw new IOException("Viewer index creation refuses pre-existing sidecars.");
            var snapshot = source.Transaction;
            using var count = SourceCommand(source, snapshot, "SELECT count(*) FROM discovered_asset a WHERE " + DiscoveredCatalogRepository.Visible +
                (accountId is null ? "" : " AND a.account_id=$account"), accountId);
            var total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            // Conservative SQLite page/index/journal overhead; retain the reservation
            // until the actual generated file has closed and been removed.
            owner._storage = storage.Reserve(checked(4L * 1024 * 1024 + total * 512));
            owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
            using (var file = new FileStream(owner._path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owner._ownsFile = true; }
            owner._connection = new(new SqliteConnectionStringBuilder { DataSource = owner._path, Mode = SqliteOpenMode.ReadWrite,
                Pooling = false, DefaultTimeout = 5 }.ToString());
            owner._connection.Open();
            using (var setup = owner._connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=-2048; PRAGMA temp_store=FILE;" +
                    "PRAGMA application_id=" + ApplicationId.ToString(CultureInfo.InvariantCulture) + "; CREATE TABLE entry(position INTEGER PRIMARY KEY,asset_id TEXT NOT NULL UNIQUE) STRICT;";
                setup.ExecuteNonQuery();
            }
            using var destination = owner._connection.BeginTransaction();
            using var insert = owner._connection.CreateCommand(); insert.Transaction = destination;
            insert.CommandText = "INSERT INTO entry(position,asset_id) SELECT $start+CAST(key AS INTEGER),value FROM json_each($batch)";
            var start = insert.Parameters.Add("$start", SqliteType.Integer); var encoded = insert.Parameters.Add("$batch", SqliteType.Text); insert.Prepare();
            var batch = new List<string>(256);
            void FlushBatch()
            {
                if (batch.Count == 0) return;
                cancellationToken.ThrowIfCancellationRequested(); owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
                start.Value = owner.Count - batch.Count; encoded.Value = JsonSerializer.Serialize(batch);
                if (insert.ExecuteNonQuery() != batch.Count) throw new InvalidOperationException("Viewer index batch did not retain every captured identity.");
                batch.Clear();
            }
            using (var query = SourceCommand(source, snapshot, "SELECT a.id FROM discovered_asset a WHERE " + DiscoveredCatalogRepository.Visible +
                (accountId is null ? "" : " AND a.account_id=$account") + " ORDER BY a.date_unknown,a.date_sort,a.id", accountId))
            using (var rows = query.ExecuteReader())
            {
                while (rows.Read())
                {
                    if (owner.Count % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
                    var text = rows.GetString(0); var id = Guid.ParseExact(text, "D");
                    if (id == Guid.Empty || id.ToString("D") != text) throw new InvalidDataException("The source catalog contains a noncanonical identity.");
                    if (id == selected) owner.SelectedPosition = owner.Count;
                    batch.Add(text);
                    owner.Count = checked(owner.Count + 1);
                    if (batch.Count == 256) FlushBatch();
                }
                FlushBatch();
            }
            if (owner.Count != total) throw new InvalidOperationException("Viewer snapshot count changed inside its transaction.");
            if (owner.SelectedPosition < 0) throw new CatalogSessionSelectionUnavailableException();
            cancellationToken.ThrowIfCancellationRequested(); owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
            destination.Commit();
            if (new FileInfo(owner._path).Length > owner._storage.Bytes) throw new InvalidOperationException("Viewer index exceeded its reserved storage bound.");
            return owner;
        }
        catch (Exception error)
        {
            try { owner.Dispose(); }
            catch (Exception cleanup) { throw new CatalogSessionCaptureException(new AggregateException(error, cleanup), owner); }
            throw;
        }
    }
    public IReadOnlyList<CatalogSessionEntry> ReadWindow(long position, int before = 30, int after = 30)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (position < 0 || position >= Count || before < 0 || after < 0 || (long)before + after + 1 > 90) throw new ArgumentOutOfRangeException(nameof(position));
        using var query = (_connection ?? throw new InvalidOperationException("Viewer index has not opened.")).CreateCommand();
        query.CommandText = "SELECT position,asset_id FROM entry WHERE position >= $start AND position <= $end ORDER BY position LIMIT 90";
        query.Parameters.AddWithValue("$start", Math.Max(0, position - before)); query.Parameters.AddWithValue("$end", Math.Min(Count - 1, position + after));
        using var rows = query.ExecuteReader(); var result = new List<CatalogSessionEntry>();
        while (rows.Read()) result.Add(new(rows.GetInt64(0), Guid.ParseExact(rows.GetString(1), "D")));
        if (!result.Any(entry => entry.Position == position)) throw new InvalidDataException("The frozen viewer index lost a captured position.");
        return result.AsReadOnly();
    }
    private static SqliteCommand SourceCommand(ProcessorStateReadSnapshot source, SqliteTransaction transaction, string sql, string? accountId)
    {
        var command = source.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        if (accountId is not null) command.Parameters.AddWithValue("$account", accountId); return command;
    }
    public void Dispose()
    {
        if (_closed) return;
        _connection?.Dispose(); _connection = null;
        if (_ownsFile)
        {
            foreach (var path in new[] { _path + "-journal", _path })
            {
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Viewer index cleanup refuses a linked generated file.");
                File.Delete(path);
            }
            _ownsFile = false;
        }
        _storage?.Dispose(); _storage = null; _closed = true;
    }
}
