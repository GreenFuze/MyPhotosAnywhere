using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class FrozenCatalogSnapshot
{
    private sealed record RetentionHeader(string Format, int Major, WorkspacePublicationScope Scope, Guid SnapshotId, Guid CommitIntentId,
        long DiscoveryRevision, long Assets, CatalogSourceCoverage[] Coverage);
    private FrozenCatalogSnapshot(string path, RetentionHeader header)
    {
        _path = path; LibraryId = header.Scope.LibraryId; ConfigurationRevision = header.Scope.ConfigurationRevision;
        DiscoveryRevision = header.DiscoveryRevision; Count = header.Assets; Coverage = header.Coverage.ToList().AsReadOnly();
    }
    internal long RetainedWriteBudget()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var bytes = new FileInfo(_path).Length;
        if (bytes > CatalogGenerationCaptureRepository.MaximumSnapshotBytes - 1024 * 1024) throw new InvalidDataException("Frozen snapshot exceeds the supported retained-file budget.");
        return checked(bytes * 3 + 2 * 1024 * 1024);
    }
    internal void CopyForRetention(string destination, CatalogGenerationCapture claim, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_closed, this); token.ThrowIfCancellationRequested();
        if (claim.Scope.LibraryId != LibraryId || claim.Scope.ConfigurationRevision != ConfigurationRevision || claim.Phase != "capturing") throw new CatalogGenerationCaptureConflictException();
        var header = new RetentionHeader("mpa-retained-catalog", 1, claim.Scope, claim.SnapshotId, claim.CommitIntentId, DiscoveryRevision, Count, Coverage.ToArray());
        var bytes = new CanonicalJson(256 * 1024).Encode(JsonSerializer.SerializeToUtf8Bytes(header));
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        target.Open();
        // The native backup is synchronous. Its worker must be joined before the
        // destination lease or source is released even when cancellation arrives.
        _connection!.BackupDatabase(target); token.ThrowIfCancellationRequested();
        using var transaction = target.BeginTransaction(); using var command = target.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE retained_capture(id INTEGER PRIMARY KEY CHECK(id=1),header BLOB NOT NULL CHECK(length(header) BETWEEN 1 AND 262144),sha256 TEXT NOT NULL) STRICT";
        command.ExecuteNonQuery(); command.CommandText = "INSERT INTO retained_capture VALUES(1,$header,$hash)";
        command.Parameters.AddWithValue("$header", bytes); command.Parameters.AddWithValue("$hash", Hash(bytes)); command.ExecuteNonQuery();
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }

    internal static CatalogSnapshotDescriptor InspectRetained(string path, CatalogGenerationCapture claim, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length is < 4096 or > CatalogGenerationCaptureRepository.MaximumSnapshotBytes) throw new InvalidDataException("Retained catalog size exceeds its bound.");
        using var connection = ReadRetainedConnection(path); var header = ReadRetentionHeader(connection, claim);
        using var count = connection.CreateCommand(); count.CommandText = "SELECT count(*) FROM fact";
        if ((long)count.ExecuteScalar()! != header.Assets) throw new InvalidDataException("Retained capture fact count disagrees with its complete header.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 4096 or > CatalogGenerationCaptureRepository.MaximumSnapshotBytes) throw new InvalidDataException("Retained catalog size exceeds its bound.");
        using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; int length;
        while ((length = file.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); digest.AppendData(buffer.AsSpan(0, length)); }
        token.ThrowIfCancellationRequested();
        return new(claim.SnapshotId, header.Scope.LibraryId, header.Scope.ConfigurationRevision, header.DiscoveryRevision, header.Assets,
            file.Length, Convert.ToHexStringLower(digest.GetHashAndReset()));
    }

    internal static FrozenCatalogSnapshot OpenRetained(string path, CatalogGenerationCapture claim, StorageBudgetManager budget, Action release, CancellationToken token)
    {
        var expected = claim.Snapshot ?? throw new InvalidOperationException("Seal the complete file proof before reading a retained capture.");
        var actual = InspectRetained(path, claim, token);
        if (actual != expected) throw new InvalidDataException("Retained catalog bytes changed. Preserve the claimed snapshot and signing history for recovery.");
        SqliteConnection? connection = null; StorageReservation? reservation = null;
        try
        {
            reservation = budget.Reserve(18 * 1024 * 1024); reservation.RecheckBeforeWrite(0);
            connection = ReadRetainedConnection(path); var header = ReadRetentionHeader(connection, claim); token.ThrowIfCancellationRequested();
            var owner = new FrozenCatalogSnapshot(path, header) { _connection = connection, _storage = reservation, _releaseRetainedLease = release };
            connection = null; reservation = null; return owner;
        }
        finally { connection?.Dispose(); reservation?.Dispose(); }
    }
    private static SqliteConnection ReadRetainedConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON; PRAGMA cache_size=-2048; PRAGMA application_id";
            if ((long)command.ExecuteScalar()! != 1297105222) throw new InvalidDataException("Retained file is not a private frozen catalog.");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static RetentionHeader ReadRetentionHeader(SqliteConnection connection, CatalogGenerationCapture claim)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT length(header),header,sha256 FROM retained_capture WHERE id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) is < 1 or > 262144) throw new InvalidDataException("Retained catalog completion header is absent or oversized.");
        var bytes = reader.GetFieldValue<byte[]>(1);
        if (Hash(bytes) != reader.GetString(2)) throw new InvalidDataException("Retained catalog header integrity failed.");
        try
        {
            var header = JsonSerializer.Deserialize<RetentionHeader>(bytes) ?? throw new InvalidDataException("Retained catalog header is missing.");
            if (header.Format != "mpa-retained-catalog" || header.Major != 1 || header.Scope != claim.Scope || header.SnapshotId != claim.SnapshotId ||
                header.CommitIntentId != claim.CommitIntentId || header.DiscoveryRevision < 1 || header.Assets < 0 || header.Coverage is null || header.Coverage.Length > CatalogPublicationReader.MaximumCoverageSources ||
                !new CanonicalJson(262144).Encode(JsonSerializer.SerializeToUtf8Bytes(header)).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Retained capture belongs to a different generation or invalid metadata context.");
            var ids = new HashSet<Guid>();
            foreach (var source in header.Coverage)
                if (source.Id == Guid.Empty || !ids.Add(source.Id) || source.Revision < 1 || source.AccountRevision < 1 || string.IsNullOrEmpty(source.AccountId) || string.IsNullOrEmpty(source.FolderId))
                    throw new InvalidDataException("Retained source coverage identity/revision failed.");
            return header;
        }
        catch (Exception error) when (error is JsonException or ProtocolFormatException or ArgumentException)
        { throw new InvalidDataException("Retained catalog header is malformed; do not rebuild this generation from current photos.", error); }
    }
}
