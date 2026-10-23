using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record WorkspaceObservationContext(Guid LibraryId, string AccountId, string FolderId, long AccountRevision, string? SharedDriveId);
public sealed record WorkspaceDocumentObservation(string FileId, long Version, long Bytes, string Sha256);
public sealed record WorkspaceInventoryCheckpoint(WorkspaceObservationContext Context, long Revision, string Cursor, int Documents, string InventorySha256);
public sealed class WorkspaceObservationConflictException() : InvalidOperationException("A previously observed immutable workspace document changed or disappeared. Preserve the previous state and reconcile explicitly.");

/// <summary>Retains actual read identities/digests before inventory cursor advancement.
/// These are observations, never signatures, policy acceptance, head ancestry or permission
/// to publish. Every process start still requires complete current reconciliation. The
/// supplied bytes/digests must come from the joined account reader's exact captured file.</summary>
public sealed class WorkspaceObservationRepository(ProcessorStateDatabase database, StorageBudgetManager budget)
{
    public const int MaximumDocuments = 4096;
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private sealed record StoredObservation(Guid LibraryId, string AccountId, string FolderId, WorkspaceDocumentObservation Observation);

    public ReadOnlyCollection<WorkspaceDocumentObservation> Known(WorkspaceObservationContext context)
    {
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(context, transaction);
        var result = ReadAll(context, transaction); transaction.Commit(); return result;
    }

    public WorkspaceDocumentObservation RememberRead(WorkspaceObservationContext context, WorkspaceDocumentObservation observation)
    {
        Validate(observation); using var transaction = _database.Connection.BeginTransaction(); AssertContext(context, transaction);
        if (observation.FileId == context.FolderId) throw new ArgumentException("A metadata document cannot alias its containing workspace.", nameof(observation));
        var old = ReadOne(context, observation.FileId, transaction);
        if (old is not null)
        {
            if (old.Bytes != observation.Bytes || old.Sha256 != observation.Sha256 || old.Version > observation.Version) throw new WorkspaceObservationConflictException();
            if (old == observation) { transaction.Commit(); return old; }
        }
        else
        {
            using var count = Command(transaction, "SELECT count(*) FROM workspace_document_observation");
            if ((long)count.ExecuteScalar()! >= MaximumDocuments) throw new InvalidOperationException("Workspace observation inventory exceeds its bound.");
        }
        var document = Encode(new StoredObservation(context.LibraryId, context.AccountId, context.FolderId, observation), 4096);
        using var reservation = _budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
        using var write = Command(transaction, """
            INSERT INTO workspace_document_observation(id,document,row_hash) VALUES($id,$document,$hash)
            ON CONFLICT(id) DO UPDATE SET document=excluded.document,row_hash=excluded.row_hash
            """, ("$id", observation.FileId), ("$document", document), ("$hash", Hash(Encoding.UTF8.GetBytes(document))));
        if (write.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workspace observation did not checkpoint exactly one identity.");
        transaction.Commit(); return observation;
    }

    /// <summary>The caller first verifies all signatures/history, persists accepted
    /// policies and completes its final unchanged-Drive check. This checkpoint records
    /// that scan only; its cursor cannot authorize a future process or access attempt.</summary>
    public WorkspaceInventoryCheckpoint Complete(WorkspaceObservationContext context, string cursor, IReadOnlyCollection<WorkspaceDocumentObservation> completeInventory)
    {
        ArgumentNullException.ThrowIfNull(completeInventory);
        if (cursor is not { Length: > 0 and <= 4096 } || cursor.Any(char.IsControl)) throw new ArgumentException("A bounded actual Drive cursor is required.", nameof(cursor));
        if (completeInventory.Count > MaximumDocuments) throw new ArgumentException("Inventory exceeds its bound.", nameof(completeInventory));
        var expected = completeInventory.OrderBy(document => document.FileId, StringComparer.Ordinal).ToArray();
        foreach (var document in expected) Validate(document);
        if (expected.Select(document => document.FileId).Distinct(StringComparer.Ordinal).Count() != expected.Length) throw new WorkspaceObservationConflictException();
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(context, transaction);
        var known = ReadAll(context, transaction);
        if (!known.SequenceEqual(expected)) throw new WorkspaceObservationConflictException(); // Missing known files never reset history.
        var digest = InventoryHash(expected); var old = ReadCheckpoint(transaction);
        if (old is not null)
        {
            AssertSameWorkspace(old.Context, context);
            if (old.Context.AccountRevision > context.AccountRevision) throw new WorkspaceObservationConflictException();
            if (old.Context == context && old.Cursor == cursor && old.InventorySha256 == digest && old.Documents == known.Count)
            { transaction.Commit(); return old; }
        }
        var checkpoint = new WorkspaceInventoryCheckpoint(context, checked((old?.Revision ?? 0) + 1), cursor, known.Count, digest);
        var encoded = Encode(checkpoint, 32768); using var reservation = _budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded) * 3L + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        using var write = Command(transaction, """
            INSERT INTO workspace_inventory_checkpoint(id,document,row_hash) VALUES(1,$document,$hash)
            ON CONFLICT(id) DO UPDATE SET document=excluded.document,row_hash=excluded.row_hash
            """, ("$document", encoded), ("$hash", Hash(Encoding.UTF8.GetBytes(encoded))));
        if (write.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workspace inventory checkpoint did not update exactly one row.");
        transaction.Commit(); return checkpoint;
    }

    public WorkspaceInventoryCheckpoint? LastCheckpoint()
    { using var transaction = _database.Connection.BeginTransaction(); var result = ReadCheckpoint(transaction); transaction.Commit(); return result; }

    private ReadOnlyCollection<WorkspaceDocumentObservation> ReadAll(WorkspaceObservationContext context, SqliteTransaction transaction)
    {
        var result = new List<WorkspaceDocumentObservation>();
        using var command = Command(transaction, "SELECT id,length(document),document,row_hash FROM workspace_document_observation ORDER BY id LIMIT 4097");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (result.Count == MaximumDocuments) throw new InvalidDataException("Retained workspace observation inventory exceeds its bound.");
            result.Add(DecodeObservation(context, reader));
        }
        return result.AsReadOnly();
    }
    private WorkspaceDocumentObservation? ReadOne(WorkspaceObservationContext context, string fileId, SqliteTransaction transaction)
    {
        using var command = Command(transaction, "SELECT id,length(document),document,row_hash FROM workspace_document_observation WHERE id=$id", ("$id", fileId));
        using var reader = command.ExecuteReader(); return reader.Read() ? DecodeObservation(context, reader) : null;
    }
    private static WorkspaceDocumentObservation DecodeObservation(WorkspaceObservationContext context, SqliteDataReader reader)
    {
        if (reader.GetInt64(1) is < 1 or > 4096) throw new InvalidDataException("Retained observation exceeds its bound.");
        var value = Decode<StoredObservation>(reader.GetString(2), reader.GetString(3), 4096);
        if (value.LibraryId != context.LibraryId || value.AccountId != context.AccountId || value.FolderId != context.FolderId || value.Observation is null || value.Observation.FileId != reader.GetString(0))
            throw new InvalidDataException("Retained observation changed workspace or file identity.");
        Validate(value.Observation);
        if (value.Observation.FileId == context.FolderId) throw new InvalidDataException("Retained metadata aliases its containing workspace.");
        return value.Observation;
    }
    private WorkspaceInventoryCheckpoint? ReadCheckpoint(SqliteTransaction transaction)
    {
        using var command = Command(transaction, "SELECT length(document),document,row_hash FROM workspace_inventory_checkpoint WHERE id=1");
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(0) is < 1 or > 32768) throw new InvalidDataException("Retained inventory checkpoint exceeds its bound.");
        var value = Decode<WorkspaceInventoryCheckpoint>(reader.GetString(1), reader.GetString(2), 32768);
        if (value.Context is null || value.Revision < 1 || value.Documents is < 0 or > MaximumDocuments ||
            value.Cursor is not { Length: > 0 and <= 4096 } || value.Cursor.Any(char.IsControl) || !Digest(value.InventorySha256)) throw new InvalidDataException("Retained inventory checkpoint is invalid.");
        ValidateContext(value.Context); return value;
    }
    private void AssertContext(WorkspaceObservationContext context, SqliteTransaction transaction)
    {
        ValidateContext(context); var pin = new LibraryAuthorityRepository(_database).Controller(transaction);
        if (pin is null || pin.LibraryId != context.LibraryId || pin.AccountId != context.AccountId || pin.FolderId != context.FolderId) throw new StaleLibraryConfigurationException();
        using var account = Command(transaction, "SELECT 1 FROM drive_account WHERE permission_id=$account AND connected=1 AND revision=$revision", ("$account", context.AccountId), ("$revision", context.AccountRevision));
        if (account.ExecuteScalar() is null) throw new StaleDriveConnectionException();
    }
    private static void AssertSameWorkspace(WorkspaceObservationContext old, WorkspaceObservationContext current)
    {
        if (old.LibraryId != current.LibraryId || old.AccountId != current.AccountId || old.FolderId != current.FolderId || old.SharedDriveId != current.SharedDriveId) throw new WorkspaceObservationConflictException();
    }
    private static void ValidateContext(WorkspaceObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.LibraryId == Guid.Empty || !Identity(context.AccountId) || !Identity(context.FolderId) || context.FolderId == "root" || context.AccountRevision < 1 || context.SharedDriveId is not null && !Identity(context.SharedDriveId)) throw new ArgumentException("An exact pinned workspace and current access revision are required.", nameof(context));
    }
    private static void Validate(WorkspaceDocumentObservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Identity(value.FileId) || value.FileId == "root" || value.Version < 1 || value.Bytes is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes || !Digest(value.Sha256)) throw new ArgumentException("A bounded actual immutable document observation is required.", nameof(value));
    }
    private static bool Identity(string? text) => text is { Length: > 0 and <= 128 } && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static bool Digest(string? text) => text is { Length: 64 } && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string InventoryHash(IEnumerable<WorkspaceDocumentObservation> ordered)
    {
        // The inventory exceeds the protocol's 1000-element array cap. Frame each
        // independently bounded canonical record instead of relaxing that shared cap.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("mpa-workspace-observations-v1\0"u8);
        Span<byte> length = stackalloc byte[4];
        foreach (var observation in ordered)
        {
            var bytes = Encoding.UTF8.GetBytes(Encode(observation, 4096)); BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static string Encode<T>(T value, int maximum) => Encoding.UTF8.GetString(new CanonicalJson(maximum).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static T Decode<T>(string text, string digest, int maximum)
    {
        try
        {
            if (Hash(Encoding.UTF8.GetBytes(text)) != digest) throw new InvalidDataException("Retained workspace observation digest changed.");
            var value = JsonSerializer.Deserialize<T>(text) ?? throw new InvalidDataException("Retained workspace observation is missing.");
            if (Encode(value, maximum) != text) throw new InvalidDataException("Retained workspace observation lost its canonical context."); return value;
        }
        catch (JsonException error) { throw new InvalidDataException("Retained workspace observation cannot be decoded.", error); }
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private SqliteCommand Command(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value); return command;
    }
}
