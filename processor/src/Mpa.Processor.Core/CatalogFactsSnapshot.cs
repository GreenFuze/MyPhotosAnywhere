using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record CatalogFactsPage(ReadOnlyCollection<DriveCatalogAsset> Assets, bool HasMore);

/// <summary>Owns a read-only WAL snapshot for bounded portable-facts projection. Open under
/// the serial coordinator, then read on a background owner outside that coordinator.
/// Source imports remain writable; dispose this owner before releasing the state database.
/// This is observation capture, not source coverage, a publication or active generation.</summary>
public sealed class CatalogFactsSnapshot : IDisposable
{
    private readonly ProcessorStateReadSnapshot _snapshot;
    private bool _closed;
    public Guid LibraryId { get; }
    public long DiscoveryRevision { get; }
    public long ConfigurationRevision { get; }
    private CatalogFactsSnapshot(ProcessorStateReadSnapshot snapshot)
    {
        _snapshot = snapshot;
        using var command = Command("""
            SELECT p.value,d.revision,c.revision FROM processor_meta p CROSS JOIN discovery_revision d
            CROSS JOIN library_configuration c WHERE p.key='library_id' AND d.id=1 AND c.id=1
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !Guid.TryParseExact(reader.GetString(0), "D", out var library) || library == Guid.Empty ||
            reader.GetString(0) != library.ToString("D") || reader.GetInt64(1) < 1 || reader.GetInt64(2) < 1)
            throw new InvalidDataException("A catalog publication requires an established library and captured revisions.");
        LibraryId = library; DiscoveryRevision = reader.GetInt64(1); ConfigurationRevision = reader.GetInt64(2);
    }

    public static CatalogFactsSnapshot Open(ProcessorStateDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var snapshot = database.OpenReadSnapshot();
        try { return new(snapshot); }
        catch { snapshot.Dispose(); throw; }
    }

    public CatalogFactsPage ReadPage(string prefix, Guid? after = null, int limit = 250, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (prefix is null || prefix.Length > 8 || prefix.Length % 2 != 0 || prefix.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A partition prefix contains zero to four hexadecimal bytes.", nameof(prefix));
        if (limit is < 1 or > 250) throw new ArgumentOutOfRangeException(nameof(limit));
        if (after is not null && (after == Guid.Empty || !after.Value.ToString("N").StartsWith(prefix, StringComparison.Ordinal)))
            throw new ArgumentException("The continuation identity must belong to this partition.", nameof(after));
        cancellationToken.ThrowIfCancellationRequested();
        using var command = Command($"""
            WITH page AS (
              SELECT a.id FROM discovered_asset a WHERE {DiscoveredCatalogRepository.Visible}
              AND a.id >= $prefix AND a.id < $end
              {(after is null ? "" : "AND a.id > $after")}
              ORDER BY a.id LIMIT $limit
            )
            SELECT a.id,a.account_id,a.file_id,a.drive_version,a.name,a.mime_type,a.width,a.height,
              a.captured_utc,a.raw_capture,a.modified_utc,s.id,s.revision,m.depth,s.account_id
            FROM page p JOIN discovered_asset a ON a.id=p.id
            JOIN source_asset m ON m.asset_id=a.id JOIN source_binding s ON s.id=m.source_id
            WHERE s.enabled=1 AND (s.descendants=1 OR m.depth=0) ORDER BY a.id,s.id
            """);
        // UUID hex precedes 'g'. This is an indexed ordinal range, including the
        // hyphen following an eight-digit prefix; no full-library substring scan.
        command.Parameters.AddWithValue("$prefix", prefix); command.Parameters.AddWithValue("$end", prefix + "g");
        if (after is not null) command.Parameters.AddWithValue("$after", after.Value.ToString("D"));
        command.Parameters.AddWithValue("$limit", limit + 1);
        var assets = new List<DriveCatalogAsset>(); AssetBuilder? current = null; var more = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Guid.ParseExact(reader.GetString(0), "D");
                if (current?.Id != id)
                {
                    if (current is not null) assets.Add(current.Build());
                    if (assets.Count == limit) { more = true; break; }
                    current = new(reader, id);
                }
                if (reader.GetString(14) != current.AccountId) throw new InvalidDataException("A captured source membership crossed account identities.");
                current.Add(new(Guid.ParseExact(reader.GetString(11), "D"), reader.GetInt64(12), reader.GetInt32(13)));
            }
        }
        if (!more && current is not null) assets.Add(current.Build());
        cancellationToken.ThrowIfCancellationRequested(); return new(assets.AsReadOnly(), more);
    }

    public void Dispose()
    {
        if (_closed) return;
        _snapshot.Dispose(); _closed = true;
    }
    private SqliteCommand Command(string sql)
    {
        var command = _snapshot.Connection.CreateCommand(); command.Transaction = _snapshot.Transaction;
        command.CommandText = sql; return command;
    }
    private sealed class AssetBuilder
    {
        private readonly DriveCatalogAsset _asset;
        private readonly List<DriveCatalogSource> _sources = [];
        public Guid Id => _asset.Id;
        public string AccountId => _asset.AccountId;
        public AssetBuilder(SqliteDataReader reader, Guid id) => _asset = new(id, reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
            _sources.AsReadOnly());
        public void Add(DriveCatalogSource source)
        {
            if (_sources.Count == DriveCatalogFacts.MaximumSources) throw new InvalidDataException("Captured source membership exceeds its supported bound.");
            _sources.Add(source);
        }
        public DriveCatalogAsset Build() => _asset;
    }
}
