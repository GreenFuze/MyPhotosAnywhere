using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record WorkspacePublicationScope(Guid LibraryId, Guid ActorId, long ActorEpoch, Guid GenerationId, string AccountId,
    long AccountRevision, string FolderId, long ConfigurationRevision, string AuthoritySha256);
public sealed record WorkspacePublicationRecord(Guid Id, WorkspacePublicationScope Scope, string Role, string MimeType,
    string Sha256, string? FileId, long? VerifiedVersion, ReadOnlyMemory<byte> Payload);
public sealed class WorkspacePublicationConflictException() : InvalidOperationException("This publication identity is already bound to different captured work.");

/// <summary>Serial SQLite journal only. The generation owner must verify enrollment,
/// source authority and original-mutation consent separately. Capture payload/scope before
/// allocating a Drive ID; persist that ID before any create request. Never retarget a retry.
/// Verified rows are retained; this repository neither deletes originals nor activates heads.</summary>
public sealed partial class WorkspacePublicationRepository(ProcessorStateDatabase database, StorageBudgetManager budget)
{
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private const string Columns = "id,account_id,scope,context_hash,role,mime_type,payload_hash,file_id,verified_version,length(payload),payload";

    public WorkspacePublicationRecord Prepare(Guid id, WorkspacePublicationScope scope, string role, string mimeType, ReadOnlySpan<byte> payload,
        CatalogGenerationLocalAccess? localAccess = null)
    {
        CheckId(id); ValidateScope(scope); ValidateContent(role, mimeType);
        if (payload.Length is < 1 or > MaximumPayloadBytes) throw new ArgumentException("A bounded metadata payload is required.", nameof(payload));
        var owned = payload.ToArray(); var hash = Hash(owned); var scopeJson = EncodeScope(scope);
        using var transaction = _database.Connection.BeginTransaction();
        using (var selected = Command(transaction, "SELECT 1 FROM processor_meta WHERE key=$key", ("$key", "catalog_dependency_reference_v1:" + id.ToString("D"))))
            if (selected.ExecuteScalar() is not null) throw new WorkspacePublicationConflictException();
        var existing = Read(id, transaction);
        if (existing is not null)
        {
            if (existing.Scope != scope || existing.Role != role || existing.MimeType != mimeType || existing.Sha256 != hash ||
                !existing.Payload.Span.SequenceEqual(owned)) throw new WorkspacePublicationConflictException();
            transaction.Commit(); return existing;
        }
        CheckCurrent(scope, transaction, localAccess);
        // Includes payload, WAL copy and bounded page overhead. Actual writes still fail
        // explicitly if another process consumes disk space; no cleanup/reset is inferred.
        using var reservation = _budget.Reserve(checked(owned.LongLength * 3 + 65536));
        reservation.RecheckBeforeWrite(reservation.Bytes);
        using (var insert = Command(transaction, """
            INSERT INTO workspace_publication(id,account_id,scope,context_hash,role,mime_type,payload,payload_hash)
            VALUES($id,$account,$scope,$scopeHash,$role,$mime,$payload,$hash)
            """, ("$id", id.ToString("D")), ("$account", scope.AccountId), ("$scope", scopeJson),
            ("$scopeHash", ContextHash(id, scopeJson, role, mimeType, hash)), ("$role", role), ("$mime", mimeType), ("$payload", owned), ("$hash", hash)))
            if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Publication preparation did not insert exactly one record.");
        transaction.Commit(); return new(id, scope, role, mimeType, hash, null, null, owned);
    }

    public WorkspacePublicationRecord? Read(Guid id) { CheckId(id); return Read(id, null); }

    /// <summary>Complete bounded outbox identities, including verified work. Unlike a
    /// pending-work page this fails if the caller's inventory bound cannot cover it.</summary>
    public IReadOnlyList<Guid> Identities(int maximum = 128) => Identities(maximum, null);

    internal IReadOnlyList<Guid> Identities(int maximum, SqliteTransaction? transaction)
    {
        if (maximum is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var command = Command(transaction, "SELECT id FROM workspace_publication ORDER BY id LIMIT $limit", ("$limit", maximum + 1));
        using var reader = command.ExecuteReader(); var ids = new List<Guid>();
        while (reader.Read())
        {
            if (ids.Count == maximum) throw new InvalidDataException("Publication inventory exceeds the owner's complete reconciliation bound.");
            ids.Add(ParseId(reader.GetString(0)));
        }
        return ids.AsReadOnly();
    }

    /// <summary>Fence current account/configuration before writes or receipt admission.
    /// Existing retry data remains readable after reconnection; it is never retargeted.</summary>
    public void AssertCurrent(Guid id, CatalogGenerationLocalAccess? localAccess = null)
    {
        CheckId(id); var record = Read(id) ?? throw new InvalidOperationException("The captured publication is missing.");
        CheckCurrent(record.Scope, null, localAccess);
    }

    private void CheckCurrent(WorkspacePublicationScope scope, SqliteTransaction? transaction, CatalogGenerationLocalAccess? localAccess = null)
    {
        AssertCurrentPartialWorkMutable(scope,transaction);
        if (localAccess is not null) { localAccess.AssertCurrent(_database, scope, transaction); return; }
        using var command = Command(transaction, """
            SELECT 1 FROM drive_account a JOIN library_configuration c ON c.id=1
            JOIN processor_meta m ON m.key='library_id'
            WHERE a.permission_id=$account AND a.connected=1 AND a.revision=$accountRevision
              AND c.revision=$configuration AND m.value=$library
            """, ("$account", scope.AccountId), ("$accountRevision", scope.AccountRevision),
            ("$configuration", scope.ConfigurationRevision), ("$library", scope.LibraryId.ToString("D")));
        if (command.ExecuteScalar() is null) throw new StaleLibraryConfigurationException();
    }

    /// <summary>Bounded identities only; payloads are read one at a time by the owner.</summary>
    public IReadOnlyList<Guid> Pending(int limit = 32)
    {
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit));
        using var transaction=_database.Connection.BeginTransaction();
        var pending=new List<Guid>();
        using(var command=Command(transaction,"SELECT id FROM workspace_publication WHERE verified_version IS NULL ORDER BY id LIMIT $bound",
            ("$bound",WorkspaceObservationRepository.MaximumDocuments+1)))
        using(var reader=command.ExecuteReader())
            while(reader.Read())
            {
                if(pending.Count==WorkspaceObservationRepository.MaximumDocuments)
                    throw new InvalidDataException("Pending metadata exceeds its existing complete inventory bound.");
                pending.Add(ParseId(reader.GetString(0)));
            }
        var ids=new List<Guid>();var terminal=new Dictionary<Guid,bool>();
        var captures=new CatalogGenerationCaptureRepository(_database,_budget);
        foreach(var id in pending)
        {
            // Enumeration deliberately keeps damaged ordinary records visible.
            // Their payload/scope is validated only when an owner reads the ID.
            if(captures.IsCurrentPartialPublicationDisposedIn(id,transaction,terminal)) continue;
            ids.Add(id);if(ids.Count==limit) break;
        }
        transaction.Commit();return ids.AsReadOnly();
    }

    public WorkspacePublicationRecord AssignFileId(Guid id, string expectedSha256, string allocatedId)
    {
        CheckId(id); if (!Digest(expectedSha256) || !Identity(allocatedId) || allocatedId == "root") throw new ArgumentException("Captured file identity and digest are required.");
        using var transaction = _database.Connection.BeginTransaction(); var captured = Required(id, transaction);
        AssertCurrentPartialWorkMutable(captured.Scope,transaction);
        if (captured.Sha256 != expectedSha256 || allocatedId == captured.Scope.FolderId || captured.FileId is not null && captured.FileId != allocatedId)
            throw new WorkspacePublicationConflictException();
        if (captured.FileId is null)
        {
            using (var duplicate = Command(transaction, "SELECT 1 FROM workspace_publication WHERE account_id=$account AND file_id=$file", ("$account", captured.Scope.AccountId), ("$file", allocatedId)))
                if (duplicate.ExecuteScalar() is not null) throw new WorkspacePublicationConflictException();
            using var update = Command(transaction, "UPDATE workspace_publication SET file_id=$file WHERE id=$id AND file_id IS NULL", ("$file", allocatedId), ("$id", id.ToString("D")));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Publication ID assignment did not affect exactly one record.");
        }
        transaction.Commit(); return captured with { FileId = allocatedId };
    }

    /// <summary>Called only after transport independently verifies downloaded bytes,
    /// actual membership/type and revision. This local receipt is not catalog activation.</summary>
    public WorkspacePublicationRecord ConfirmVerified(Guid id, string accountId, string folderId, string fileId, string sha256, long bytes, long driveVersion,
        CatalogGenerationLocalAccess? localAccess = null)
    {
        CheckId(id); ArgumentOutOfRangeException.ThrowIfLessThan(driveVersion, 1L);
        using var transaction = _database.Connection.BeginTransaction(); var captured = Required(id, transaction);
        if (captured.Scope.AccountId != accountId || captured.Scope.FolderId != folderId || captured.FileId is null || captured.FileId != fileId ||
            captured.Sha256 != sha256 || captured.Payload.Length != bytes || captured.VerifiedVersion is not null && captured.VerifiedVersion != driveVersion)
            throw new WorkspacePublicationConflictException();
        CheckCurrent(captured.Scope, transaction, localAccess);
        if (captured.VerifiedVersion is null)
        {
            using var update = Command(transaction, "UPDATE workspace_publication SET verified_version=$version WHERE id=$id AND verified_version IS NULL",
                ("$version", driveVersion), ("$id", id.ToString("D")));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Publication verification did not affect exactly one record.");
        }
        transaction.Commit(); return captured with { VerifiedVersion = driveVersion };
    }

    private WorkspacePublicationRecord Required(Guid id, SqliteTransaction transaction) => Read(id, transaction) ?? throw new InvalidOperationException("The captured publication is missing.");
    internal WorkspacePublicationRecord? Read(Guid id, SqliteTransaction? transaction)
    {
        using var command = Command(transaction, "SELECT " + Columns + " FROM workspace_publication WHERE id=$id", ("$id", id.ToString("D")));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(9) is < 1 or > MaximumPayloadBytes) throw new InvalidDataException("Stored publication exceeds its payload bound.");
        var storedId = ParseId(reader.GetString(0)); var scopeJson = reader.GetString(2);
        if (scopeJson.Length > 4096 || reader.GetString(3) != ContextHash(storedId, scopeJson, reader.GetString(4), reader.GetString(5), reader.GetString(6)))
            throw new InvalidDataException("Stored publication context integrity failed.");
        WorkspacePublicationScope scope;
        try
        {
            scope = JsonSerializer.Deserialize<WorkspacePublicationScope>(scopeJson) ?? throw new InvalidDataException("Stored publication scope is missing.");
            ValidateScope(scope);
            if (scopeJson != EncodeScope(scope) || reader.GetString(1) != scope.AccountId) throw new InvalidDataException("Stored publication scope is not canonical or account-bound.");
            ValidateContent(reader.GetString(4), reader.GetString(5));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException)
        { throw new InvalidDataException("Stored publication scope or role is invalid.", error); }
        var file = reader.IsDBNull(7) ? null : reader.GetString(7); long? version = reader.IsDBNull(8) ? null : reader.GetInt64(8);
        if (file is not null && (!Identity(file) || file == "root" || file == scope.FolderId) || version is not null && (version < 1 || file is null))
            throw new InvalidDataException("Stored publication allocation or receipt is invalid.");
        var payload = reader.GetFieldValue<byte[]>(10); var hash = reader.GetString(6);
        if (!Digest(hash) || hash != Hash(payload)) throw new InvalidDataException("Stored publication payload integrity failed; retry bytes were preserved.");
        return new(storedId, scope, reader.GetString(4), reader.GetString(5), hash, file, version, payload);
    }

    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return command;
    }
    internal static void ValidateScope(WorkspacePublicationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.LibraryId == Guid.Empty || scope.ActorId == Guid.Empty || scope.ActorEpoch < 1 || scope.GenerationId == Guid.Empty || !Identity(scope.AccountId) ||
            !Identity(scope.FolderId) || scope.FolderId == "root" || scope.AccountRevision < 1 || scope.ConfigurationRevision < 1 || !Digest(scope.AuthoritySha256))
            throw new ArgumentException("An immutable library/actor/generation, account/workspace and authority revision/digest are required.", nameof(scope));
    }
    private static void ValidateContent(string role, string mime)
    {
        if (role is not ("facts-chunk" or "facts-index" or "coverage-page" or "coverage-root" or "publisher-authorization" or "source-configuration" or "catalog-commit") ||
            mime is not ("application/json" or "application/gzip" or "application/octet-stream")) throw new ArgumentException("A supported metadata publication role/type is required.");
    }
    internal static string EncodeScope(WorkspacePublicationScope scope) => Encoding.UTF8.GetString(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(scope)));
    private static string ContextHash(Guid id, string scope, string role, string mimeType, string payloadSha256) =>
        Hash(new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(new { id = id.ToString("D"), scope, role, mimeType, payloadSha256 })));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Identity(string? value) => value is { Length: >= 1 and <= 256 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void CheckId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A publication identity is required.", nameof(id)); }
    private static Guid ParseId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw new InvalidDataException("Stored publication identity is not canonical.");
}
