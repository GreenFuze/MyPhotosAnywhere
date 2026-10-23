using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

/// <summary>Durable scan evidence, not signature/history verification or publication
/// authority. A failed or interrupted newer attempt invalidates older scan evidence.
/// Every process/access owner must still start closed and perform its own current scan.</summary>
public sealed record WorkspaceObservationAttempt(WorkspaceObservationContext Context, Guid Id, long Revision,
    long BaseCheckpointRevision, WorkspaceInventoryCheckpoint? Completed, string CandidateInventorySha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstUnresolvedCursor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InventoryStartCursor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SigningInventorySha256 = null);

public sealed partial class WorkspaceObservationRepository
{
    private const string AttemptKey = "workspace_observation_attempt_v1";
    // At most three opaque 4096-character cursors (checkpoint, first unresolved,
    // current start), each potentially JSON-escaped. Signed envelope caps are unchanged.
    private const int MaximumAttemptBytes = 131072;

    /// <summary>Commit this fence before any network inspection. Failures leave the
    /// attempt incomplete without requiring another successful write to record failure.</summary>
    public WorkspaceObservationAttempt BeginScan(WorkspaceObservationContext context)
    {
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(context, transaction);
        var previous = ReadAttempt(transaction); var checkpoint = ReadCheckpoint(transaction);
        if (previous is not null) AssertSameWorkspace(previous.Context, context);
        if (checkpoint is not null) AssertSameWorkspace(checkpoint.Context, context);
        if ((previous?.Context.AccountRevision ?? checkpoint?.Context.AccountRevision ?? 0) > context.AccountRevision)
            throw new WorkspaceObservationConflictException();
        var attempt = new WorkspaceObservationAttempt(context, Guid.NewGuid(), checked((previous?.Revision ?? 0) + 1), checkpoint?.Revision ?? 0, null,
            previous?.CandidateInventorySha256 ?? CandidateInventoryHash([]), previous?.FirstUnresolvedCursor,
            SigningInventorySha256: new ProtocolSigningJournal(_database, _budget).Inventory(context.LibraryId, transaction).Sha256);
        WriteAttempt(attempt, transaction); transaction.Commit(); return attempt;
    }

    /// <summary>Retain the actual start before listing. Preserve the earliest unresolved
    /// start across failed attempts; a later start never proves that earlier changes were
    /// reconciled. General publication must independently drain that retained history.</summary>
    public WorkspaceObservationAttempt StartInventory(WorkspaceObservationAttempt attempt, string cursor)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!ValidAttemptCursor(cursor)) throw new ArgumentException("A bounded actual inventory start cursor is required.", nameof(cursor));
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(attempt.Context, transaction);
        if (attempt.Completed is not null || ReadAttempt(transaction) != attempt || attempt.InventoryStartCursor is not null)
            throw new WorkspaceObservationConflictException();
        var updated = attempt with { FirstUnresolvedCursor = attempt.FirstUnresolvedCursor ?? cursor, InventoryStartCursor = cursor };
        WriteAttempt(updated, transaction); transaction.Commit(); return updated;
    }

    /// <summary>Only the exact still-current attempt can complete. Its checkpoint and
    /// completion fence commit together, so neither can survive alone after a failed write.</summary>
    public WorkspaceInventoryCheckpoint CompleteScan(WorkspaceObservationAttempt attempt, string cursor,
        IReadOnlyCollection<WorkspaceDocumentObservation> completeInventory, WorkspacePublicationInventory? publications = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(attempt.Context, transaction);
        if (attempt.Completed is not null || ReadAttempt(transaction) != attempt) throw new WorkspaceObservationConflictException();
        AssertCandidatesRead(attempt.Context, transaction);
        var checkpoint = Complete(attempt.Context, cursor, completeInventory, transaction);
        WriteAttempt(attempt with { Completed = checkpoint }, transaction);
        if (publications is not null)
        {
            if (publications.Context != attempt.Context) throw new WorkspacePublicationConflictException();
            new WorkspacePublicationRepository(_database, _budget).AssertInventoryUnchanged(publications, transaction);
        }
        AssertSigningInventory(attempt, transaction);
        transaction.Commit(); return checkpoint;
    }

    /// <summary>Historical diagnostic read only. A completed stored attempt cannot
    /// authorize a new process or skip reconciliation after reconnecting.</summary>
    public WorkspaceObservationAttempt? LastAttempt()
    { using var transaction = _database.Connection.BeginTransaction(); var result = ReadAttempt(transaction); transaction.Commit(); return result; }

    public void AssertCompletedScan(WorkspaceInventoryCheckpoint checkpoint, Guid? expectedAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        using var transaction = _database.Connection.BeginTransaction(); AssertCompletedScan(checkpoint, expectedAttemptId, transaction); transaction.Commit();
    }

    internal void AssertCompletedScan(WorkspaceInventoryCheckpoint checkpoint, Guid? expectedAttemptId, SqliteTransaction transaction)
    {
        AssertContext(checkpoint.Context, transaction);
        var attempt = ReadAttempt(transaction);
        if (attempt?.Completed != checkpoint || expectedAttemptId is not null && attempt.Id != expectedAttemptId)
            throw new WorkspaceObservationConflictException();
        var known = ReadAll(checkpoint.Context, transaction);
        if (known.Count != checkpoint.Documents || InventoryHash(known) != checkpoint.InventorySha256)
            throw new WorkspaceObservationConflictException();
        AssertCandidatesRead(checkpoint.Context, transaction);
        AssertSigningInventory(attempt, transaction);
    }

    private void AssertSigningInventory(WorkspaceObservationAttempt attempt, SqliteTransaction transaction)
    {
        // Older attempts remain readable evidence but cannot acquire the new current
        // signing fence by completing or replaying their historical checkpoint.
        var hash = attempt.SigningInventorySha256 ?? throw new WorkspaceObservationConflictException();
        new ProtocolSigningJournal(_database, _budget).AssertInventoryUnchanged(attempt.Context.LibraryId, hash, transaction);
    }

    private WorkspaceObservationAttempt? ReadAttempt(SqliteTransaction transaction)
    {
        var text = AttemptMeta(AttemptKey, MaximumAttemptBytes, transaction); var digest = AttemptMeta(AttemptKey + ":sha256", 64, transaction);
        if (text is null && digest is null)
        {
            using var candidates = Command(transaction, "SELECT 1 FROM processor_meta WHERE key GLOB 'workspace_observed_candidate_v1:*' LIMIT 1");
            if (candidates.ExecuteScalar() is not null) throw new InvalidDataException("Workspace candidate evidence lost its scan owner.");
            return null;
        }
        if (text is null || digest is null) throw new InvalidDataException("Workspace scan attempt lost its durable integrity record.");
        WorkspaceObservationAttempt value;
        try
        {
            value = Decode<WorkspaceObservationAttempt>(text, digest, MaximumAttemptBytes);
            if (value.Context is null || value.Id == Guid.Empty || value.Revision < 1 || value.BaseCheckpointRevision < 0 || !Digest(value.CandidateInventorySha256) ||
                value.SigningInventorySha256 is not null && !Digest(value.SigningInventorySha256))
                throw new InvalidDataException("Retained workspace scan attempt is invalid.");
            ValidateContext(value.Context);
            if (value.FirstUnresolvedCursor is not null && !ValidAttemptCursor(value.FirstUnresolvedCursor) ||
                value.InventoryStartCursor is not null && (!ValidAttemptCursor(value.InventoryStartCursor) || value.FirstUnresolvedCursor is null))
                throw new InvalidDataException("Retained inventory start lost its bounded unresolved history.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("Retained workspace scan attempt changed its context.", error); }
        if (CandidateInventoryHash(ReadCandidates(value.Context, transaction).Values) != value.CandidateInventorySha256)
            throw new InvalidDataException("Workspace candidate history changed or disappeared after its scan fence.");
        var checkpoint = ReadCheckpoint(transaction);
        if (value.Completed is { } complete)
        {
            if (complete != checkpoint || complete.Context != value.Context || complete.Revision < value.BaseCheckpointRevision ||
                complete.Revision - value.BaseCheckpointRevision > 1)
                throw new InvalidDataException("Completed scan lost its exact atomic checkpoint.");
        }
        else if ((checkpoint?.Revision ?? 0) != value.BaseCheckpointRevision)
            throw new InvalidDataException("Incomplete scan checkpoint advanced outside its owner.");
        if (checkpoint is not null)
        {
            AssertSameWorkspace(checkpoint.Context, value.Context);
            if (checkpoint.Context.AccountRevision > value.Context.AccountRevision) throw new InvalidDataException("Scan attempt moved access history backwards.");
        }
        return value;
    }

    private void WriteAttempt(WorkspaceObservationAttempt attempt, SqliteTransaction transaction)
    {
        var text = Encode(attempt, MaximumAttemptBytes);
        using var reservation = _budget.Reserve(checked(Encoding.UTF8.GetByteCount(text) * 3L + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        foreach (var pair in new[] { (Key: AttemptKey, Value: text), (Key: AttemptKey + ":sha256", Value: Hash(Encoding.UTF8.GetBytes(text))) })
        {
            using var command = Command(transaction, """
                INSERT INTO processor_meta(key,value) VALUES($key,$value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """, ("$key", pair.Key), ("$value", pair.Value));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workspace scan attempt did not update exactly one record.");
        }
    }
    private string? AttemptMeta(string key, int maximum, SqliteTransaction transaction)
    {
        using var command = Command(transaction, "SELECT length(value),value FROM processor_meta WHERE key=$key", ("$key", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(0) is < 1 || reader.GetInt64(0) > maximum) throw new InvalidDataException("Workspace scan attempt exceeds its bound.");
        return reader.GetString(1);
    }
    private static bool ValidAttemptCursor(string? cursor) => cursor is { Length: > 0 and <= 4096 } && !cursor.Any(char.IsControl);
}
