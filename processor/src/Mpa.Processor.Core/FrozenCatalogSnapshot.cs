using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed class FrozenCatalogCaptureException(Exception cause, FrozenCatalogSnapshot retainedOwner)
    : Exception("Catalog capture failed and private cleanup is unconfirmed; ownership is retained.", cause)
{ public FrozenCatalogSnapshot RetainedOwner { get; } = retainedOwner; }

public sealed record FrozenCatalogPartitionSize(long Count, long EncodedUpperBound);

/// <summary>Disposable private metadata copy. Capture takes ownership of the live WAL
/// reader and releases it before returning, including on failure/cancellation. Later
/// hashing, compression and Drive uploads read only this bounded-cache SQLite copy.
/// This is neither an active catalog nor a durable upload or authority record.</summary>
public sealed partial class FrozenCatalogSnapshot : IDisposable
{
    private readonly string _path;
    private SqliteConnection? _connection;
    private StorageReservation? _storage;
    private bool _ownsFile;
    private bool _closed;
    private Action? _releaseRetainedLease;
    public Guid LibraryId { get; }
    public long DiscoveryRevision { get; }
    public long ConfigurationRevision { get; }
    public long Count { get; private set; }
    public ReadOnlyCollection<CatalogSourceCoverage> Coverage { get; private set; } = Array.AsReadOnly(Array.Empty<CatalogSourceCoverage>());
    private FrozenCatalogSnapshot(string path, CatalogFactsSnapshot source)
    { _path = path; LibraryId = source.LibraryId; DiscoveryRevision = source.DiscoveryRevision; ConfigurationRevision = source.ConfigurationRevision; }

    public static FrozenCatalogSnapshot CaptureAndRelease(CatalogFactsSnapshot ownedSource, CatalogPreparationDirectory directory,
        StorageBudgetManager storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedSource);
        FrozenCatalogSnapshot? owner = null;
        try
        {
            using (ownedSource)
            {
                ArgumentNullException.ThrowIfNull(directory); ArgumentNullException.ThrowIfNull(storage); cancellationToken.ThrowIfCancellationRequested();
                owner = new(directory.NewIndexPath(), ownedSource); owner.Coverage = ownedSource.ReadCoverageSources(cancellationToken);
                var estimate = ownedSource.StagingEstimate(); owner._storage = storage.Reserve(estimate.Bytes);
                owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
                using (var file = new FileStream(owner._path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owner._ownsFile = true; }
                owner._connection = new(new SqliteConnectionStringBuilder { DataSource = owner._path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
                owner._connection.Open();
                using (var setup = owner._connection.CreateCommand())
                {
                    setup.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=-2048; PRAGMA temp_store=FILE;" +
                        "PRAGMA application_id=1297105222; CREATE TABLE fact(id TEXT PRIMARY KEY,payload BLOB NOT NULL,sha256 TEXT NOT NULL) STRICT;";
                    setup.ExecuteNonQuery();
                }
                using var transaction = owner._connection.BeginTransaction(); using var insert = owner._connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO fact(id,payload,sha256) VALUES($id,$payload,$hash)";
                var id = insert.Parameters.Add("$id", SqliteType.Text); var payload = insert.Parameters.Add("$payload", SqliteType.Blob); var hash = insert.Parameters.Add("$hash", SqliteType.Text); insert.Prepare();
                var codec = new DriveCatalogFacts(owner.LibraryId); Guid? after = null;
                while (true)
                {
                    var page = ownedSource.ReadPage("", after, 250, cancellationToken); owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
                    foreach (var asset in page.Assets)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var bytes = codec.Encode(asset.Id.ToString("N")[..2], [asset]);
                        id.Value = asset.Id.ToString("D"); payload.Value = bytes; hash.Value = Hash(bytes);
                        if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Catalog capture did not retain exactly one fact.");
                        owner.Count = checked(owner.Count + 1); after = asset.Id;
                    }
                    if (!page.HasMore) break;
                    if (page.Assets.Count == 0) throw new InvalidDataException("Catalog capture continuation did not advance.");
                }
                if (owner.Count != estimate.Count) throw new InvalidDataException("Catalog capture count changed inside its WAL observation.");
                cancellationToken.ThrowIfCancellationRequested(); owner._storage.RecheckBeforeWrite(owner._storage.Bytes); transaction.Commit();
                if (new FileInfo(owner._path).Length > owner._storage.Bytes) throw new InvalidOperationException("Catalog capture exceeded its reserved storage bound.");
                using var readOnly = owner._connection.CreateCommand(); readOnly.CommandText = "PRAGMA query_only=ON"; readOnly.ExecuteNonQuery();
            }
            return owner;
        }
        catch (Exception error)
        {
            if (owner is not null)
            {
                try { owner.Dispose(); }
                catch (Exception cleanup) { throw new FrozenCatalogCaptureException(new AggregateException(error, cleanup), owner); }
            }
            throw;
        }
    }

    public CatalogFactsPage ReadPage(string prefix, Guid? after = null, int limit = 250, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (prefix is null || prefix.Length > 8 || prefix.Length % 2 != 0 || prefix.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A partition prefix contains zero to four hexadecimal bytes.", nameof(prefix));
        if (limit is < 1 or > 250) throw new ArgumentOutOfRangeException(nameof(limit));
        if (after is not null && (after == Guid.Empty || !after.Value.ToString("N").StartsWith(prefix, StringComparison.Ordinal)))
            throw new ArgumentException("The continuation must belong to the captured partition.", nameof(after));
        cancellationToken.ThrowIfCancellationRequested(); using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT id,length(payload),payload,sha256 FROM fact WHERE id >= $prefix AND id < $end" +
            (after is null ? "" : " AND id > $after") + " ORDER BY id LIMIT $limit";
        command.Parameters.AddWithValue("$prefix", prefix); command.Parameters.AddWithValue("$end", prefix + "g"); command.Parameters.AddWithValue("$limit", limit + 1);
        if (after is not null) command.Parameters.AddWithValue("$after", after.Value.ToString("D"));
        var result = new List<DriveCatalogAsset>(); var codec = new DriveCatalogFacts(LibraryId); var more = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); if (result.Count == limit) { more = true; break; }
            if (reader.GetInt64(1) is < 1 or > CatalogChunkDecoder.MaximumExpandedBytes) throw new InvalidDataException("Captured fact exceeds its bound.");
            var text = reader.GetString(0); var bytes = reader.GetFieldValue<byte[]>(2);
            if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.ToString("D") != text || Hash(bytes) != reader.GetString(3))
                throw new InvalidDataException("Captured fact identity or digest failed.");
            DriveCatalogAsset asset;
            try
            {
                var decoded = codec.Read(bytes, id.ToString("N")[..2]);
                if (decoded.Assets.Count != 1) throw new InvalidDataException("Captured row must contain exactly one fact.");
                asset = decoded.Assets[0];
            }
            catch (Exception error) when (error is ProtocolFormatException or ProtocolCompatibilityException)
            { throw new InvalidDataException("Captured fact is invalid; never substitute another photo.", error); }
            if (asset.Id != id) throw new InvalidDataException("Captured fact changed its retained identity."); result.Add(asset);
        }
        cancellationToken.ThrowIfCancellationRequested(); return new(result.AsReadOnly(), more);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Indexed bounded scalar admission before materializing a candidate chunk.
    /// Per-row wrappers conservatively overestimate the combined canonical facts object.</summary>
    public FrozenCatalogPartitionSize PartitionSize(string prefix, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (prefix is null || prefix.Length is < 2 or > 8 || prefix.Length % 2 != 0 || prefix.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A complete byte prefix is required.", nameof(prefix));
        cancellationToken.ThrowIfCancellationRequested(); using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT count(*),coalesce(sum(length(payload)),0) FROM fact WHERE id >= $prefix AND id < $end";
        command.Parameters.AddWithValue("$prefix", prefix); command.Parameters.AddWithValue("$end", prefix + "g");
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidDataException("Captured partition size is missing.");
        var count = reader.GetInt64(0); var bytes = reader.GetInt64(1);
        if (count < 0 || bytes < 0 || count > Count) throw new InvalidDataException("Captured partition size is invalid.");
        cancellationToken.ThrowIfCancellationRequested(); return new(count, checked(bytes + 8));
    }

    public void Dispose()
    {
        if (_closed) return; _connection?.Dispose(); _connection = null;
        if (_ownsFile)
        {
            foreach (var path in new[] { _path + "-journal", _path })
            {
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Catalog capture cleanup refuses a linked generated file.");
                File.Delete(path);
            }
            _ownsFile = false;
        }
        _storage?.Dispose(); _storage = null; _releaseRetainedLease?.Invoke(); _releaseRetainedLease = null; _closed = true;
    }
}
