using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record DiscoveredDriveAsset(string FileId, long DriveVersion, string Name, string MimeType, int? Width, int? Height,
    long? CapturedUtc, string? RawCapture, long? ModifiedUtc, int Depth);
public enum CatalogDateProvenance { Unknown, DriveCapture, DriveModification }
public sealed record CatalogAsset(Guid Id, string AccountId, DiscoveredDriveAsset Facts)
{
    public long? EffectiveUtc => Facts.CapturedUtc ?? Facts.ModifiedUtc;
    public CatalogDateProvenance DateProvenance => Facts.CapturedUtc is not null ? CatalogDateProvenance.DriveCapture :
        Facts.ModifiedUtc is not null ? CatalogDateProvenance.DriveModification : CatalogDateProvenance.Unknown;
}
public sealed record CatalogCursor(long? EffectiveUtc, Guid Id);
public sealed record CatalogPage(long DiscoveryRevision, long ConfigurationRevision, IReadOnlyList<CatalogAsset> Assets, bool HasMore);
public sealed class ConflictingDiscoveryException() : IOException("The same Drive revision has conflicting metadata; existing facts were retained.");

/// <summary>Bounded, serial local discovery projection. Never opens or modifies source bytes.
/// An incomplete or empty scan cannot remove previous facts. Verified generated catalog and
/// explicit tombstones are separate owners. Identity is account/file based, never content based.</summary>
public sealed class DiscoveredCatalogRepository
{
    private readonly ProcessorStateDatabase _database;
    private readonly DriveAssetIdentity _identity;
    private const string Columns = """
        a.id,a.account_id,a.file_id,a.drive_version,a.name,a.mime_type,a.width,a.height,a.captured_utc,a.raw_capture,a.modified_utc,
        (SELECT min(m.depth) FROM source_asset m JOIN source_binding s ON s.id=m.source_id
          WHERE m.asset_id=a.id AND s.enabled=1 AND (s.descendants=1 OR m.depth=0))
        """;
    private const string Visible = """
        EXISTS (SELECT 1 FROM source_asset m JOIN source_binding s ON s.id=m.source_id
          WHERE m.asset_id=a.id AND s.enabled=1 AND (s.descendants=1 OR m.depth=0))
        """;
    public DiscoveredCatalogRepository(ProcessorStateDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _identity = new DriveAssetIdentity(new ProcessorLibraryRepository(database).GetOrCreateIdentity());
    }

    public int ApplyBatch(Guid sourceId, long sourceRevision, long accountRevision, IReadOnlyList<DiscoveredDriveAsset> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (sourceId == Guid.Empty || sourceRevision < 1 || accountRevision < 1 || facts.Count is < 1 or > 250)
            throw new ArgumentException("A captured source and a bounded nonempty discovery batch are required.");
        foreach (var item in facts) Validate(item);
        if (facts.Select(item => item.FileId).Distinct(StringComparer.Ordinal).Count() != facts.Count)
            throw new ArgumentException("A discovery batch cannot repeat a file identity.", nameof(facts));
        using var transaction = _database.Connection.BeginTransaction();
        string accountId; bool descendants;
        using (var source = Command(transaction, """
            SELECT s.account_id,s.descendants FROM source_binding s JOIN drive_account a ON a.permission_id=s.account_id
            WHERE s.id=$source AND s.revision=$sourceRevision AND s.enabled=1 AND a.connected=1 AND a.revision=$accountRevision
            """, ("$source", sourceId.ToString("D")), ("$sourceRevision", sourceRevision), ("$accountRevision", accountRevision)))
        {
            using var reader = source.ExecuteReader();
            if (!reader.Read()) throw new StaleLibraryConfigurationException();
            accountId = reader.GetString(0); descendants = reader.GetBoolean(1);
        }
        var changed = 0;
        foreach (var item in facts)
        {
            if (!descendants && item.Depth != 0) throw new StaleLibraryConfigurationException();
            var id = _identity.Derive(accountId, item.FileId).ToString("D");
            var hash = FactsHash(item); long? priorVersion = null; string? priorHash = null;
            using (var prior = Command(transaction, "SELECT drive_version,facts_hash FROM discovered_asset WHERE id=$id", ("$id", id)))
            {
                using var reader = prior.ExecuteReader();
                if (reader.Read()) { priorVersion = reader.GetInt64(0); priorHash = reader.GetString(1); }
            }
            if (priorVersion == item.DriveVersion && priorHash != hash) throw new ConflictingDiscoveryException();
            var itemChanged = false;
            if (priorVersion is null || priorVersion < item.DriveVersion)
            {
                var effective = item.CapturedUtc ?? item.ModifiedUtc;
                using var upsert = Command(transaction, """
                    INSERT INTO discovered_asset VALUES($id,$account,$file,$version,$name,$mime,$width,$height,$captured,$raw,$modified,$unknown,$sort,$hash)
                    ON CONFLICT(id) DO UPDATE SET drive_version=excluded.drive_version,name=excluded.name,mime_type=excluded.mime_type,
                      width=excluded.width,height=excluded.height,captured_utc=excluded.captured_utc,raw_capture=excluded.raw_capture,
                      modified_utc=excluded.modified_utc,date_unknown=excluded.date_unknown,date_sort=excluded.date_sort,facts_hash=excluded.facts_hash
                    """, ("$id", id), ("$account", accountId), ("$file", item.FileId), ("$version", item.DriveVersion),
                    ("$name", item.Name), ("$mime", item.MimeType), ("$width", item.Width), ("$height", item.Height),
                    ("$captured", item.CapturedUtc), ("$raw", item.RawCapture), ("$modified", item.ModifiedUtc),
                    ("$unknown", effective is null), ("$sort", effective is null ? 0L : -effective.Value), ("$hash", hash));
                if (upsert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Catalog upsert did not affect one identity.");
                itemChanged = true;
            }
            using (var membership = Command(transaction, """
                INSERT INTO source_asset VALUES($source,$asset,$depth)
                ON CONFLICT(source_id,asset_id) DO UPDATE SET depth=excluded.depth WHERE depth<>excluded.depth
                """, ("$source", sourceId.ToString("D")), ("$asset", id), ("$depth", item.Depth)))
                itemChanged |= membership.ExecuteNonQuery() != 0;
            if (itemChanged) changed++;
        }
        if (changed != 0)
        {
            using var advance = Command(transaction, "UPDATE discovery_revision SET revision=revision+1 WHERE id=1 AND revision<9223372036854775807");
            if (advance.ExecuteNonQuery() != 1) throw new InvalidOperationException("Discovery revision cannot advance.");
        }
        transaction.Commit(); return changed;
    }

    public CatalogPage ReadPage(CatalogCursor? cursor = null, int limit = 60, bool before = false, string? accountId = null)
    {
        if (limit is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(limit));
        if (cursor is not null && (cursor.Id == Guid.Empty || !Time(cursor.EffectiveUtc))) throw new ArgumentException("Invalid catalog cursor.", nameof(cursor));
        if (accountId is not null) _identity.Derive(accountId, "validation-only");
        return Read(cursor, limit, before, accountId, null);
    }
    public CatalogPage SeekDate(long utc, int limit = 60, string? accountId = null)
    {
        if (!Time(utc)) throw new ArgumentOutOfRangeException(nameof(utc));
        if (limit is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(limit));
        if (accountId is not null) _identity.Derive(accountId, "validation-only");
        return Read(null, limit, false, accountId, utc);
    }
    public CatalogAsset? ResolveVisible(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Asset identity is required.", nameof(id));
        using var command = Command(null, "SELECT " + Columns + " FROM discovered_asset a WHERE " + Visible + " AND a.id=$id", ("$id", id.ToString("D")));
        using var reader = command.ExecuteReader(); return reader.Read() ? Asset(reader) : null;
    }
    private CatalogPage Read(CatalogCursor? cursor, int limit, bool before, string? accountId, long? seekUtc)
    {
        using var transaction = _database.Connection.BeginTransaction();
        using var revision = Command(transaction, "SELECT d.revision,c.revision FROM discovery_revision d CROSS JOIN library_configuration c WHERE d.id=1 AND c.id=1");
        long discovered; long configured;
        using (var reader = revision.ExecuteReader())
        { if (!reader.Read()) throw new InvalidDataException("Catalog revision is missing."); discovered = reader.GetInt64(0); configured = reader.GetInt64(1); }
        var comparison = before ? "<" : ">"; var direction = before ? " DESC" : " ASC";
        var conditions = new List<string> { Visible };
        if (accountId is not null) conditions.Add("a.account_id=$account");
        if (cursor is not null) conditions.Add("(a.date_unknown,a.date_sort,a.id) " + comparison + " ($unknown,$sort,$id)");
        if (seekUtc is not null) conditions.Add("(a.date_unknown,a.date_sort) >= (0,$seek)");
        using var command = Command(transaction, "SELECT " + Columns + " FROM discovered_asset a WHERE " + string.Join(" AND ", conditions) +
            " ORDER BY a.date_unknown" + direction + ",a.date_sort" + direction + ",a.id" + direction + " LIMIT $limit", ("$limit", limit + 1));
        if (accountId is not null) command.Parameters.AddWithValue("$account", accountId);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$unknown", cursor.EffectiveUtc is null);
            command.Parameters.AddWithValue("$sort", cursor.EffectiveUtc is null ? 0L : -cursor.EffectiveUtc.Value);
            command.Parameters.AddWithValue("$id", cursor.Id.ToString("D"));
        }
        if (seekUtc is not null) command.Parameters.AddWithValue("$seek", -seekUtc.Value);
        var assets = new List<CatalogAsset>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) assets.Add(Asset(reader));
        var more = assets.Count > limit; if (more) assets.RemoveAt(assets.Count - 1);
        if (before) assets.Reverse();
        transaction.Commit(); return new(discovered, configured, assets.AsReadOnly(), more);
    }
    private static CatalogAsset Asset(SqliteDataReader reader) => new(Guid.ParseExact(reader.GetString(0), "D"), reader.GetString(1),
        new(reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), NullableInt(reader, 6), NullableInt(reader, 7),
            NullableLong(reader, 8), reader.IsDBNull(9) ? null : reader.GetString(9), NullableLong(reader, 10), reader.GetInt32(11)));
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static long? NullableLong(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static string FactsHash(DiscoveredDriveAsset item)
    {
        // Folder depth is membership-specific and must not conflict across overlapping sources.
        var canonical = new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(new { item.FileId, item.DriveVersion,
            item.Name, item.MimeType, item.Width, item.Height, item.CapturedUtc, item.RawCapture, item.ModifiedUtc }));
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
    private static void Validate(DiscoveredDriveAsset item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.DriveVersion < 1 || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 4096 || item.Name.Any(char.IsControl) ||
            item.MimeType is null || item.MimeType.Length > 256 || item.MimeType.Any(char.IsControl) ||
            !(item.MimeType.StartsWith("image/", StringComparison.Ordinal) || item.MimeType.StartsWith("video/", StringComparison.Ordinal)) ||
            item.Width is <= 0 or > 1000000 || item.Height is <= 0 or > 1000000 || !Time(item.CapturedUtc) || !Time(item.ModifiedUtc) ||
            item.RawCapture is { Length: > 128 } || item.Depth is < 0 or > 256)
            throw new ArgumentException("Invalid bounded discovery facts.", nameof(item));
        _ = new DriveAssetIdentity(Guid.Parse("00000000-0000-4000-8000-000000000001")).Derive("validation-only", item.FileId);
    }
    private static bool Time(long? value) => value is null or (>= -62135596800000L and <= 253402300799999L);
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
