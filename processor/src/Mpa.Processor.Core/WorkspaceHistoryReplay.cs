using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed record WorkspaceHistoryReplay(string StartCursor, string Cursor, long Pages, bool AtEnd, bool Active, int BatchPages);
public sealed record WorkspaceHistoryEvent(string Kind, string Id, bool RecoveryRequired, string EvidenceSha256);
public sealed record WorkspaceHistoryObservation(string Kind, string Id, bool RecoveryRequired, long Occurrences,
    string FirstEvidenceSha256, string LastEvidenceSha256, string HistorySha256);
public sealed class WorkspaceHistoryRecoveryRequiredException() : InvalidOperationException(
    "Drive change history contains a removal or access change that cannot be reconciled automatically. Preserve the library and review its history before publication.");

public sealed partial class WorkspaceObservationRepository
{
    private const string HistoryPrefix = "workspace_history_observed_v1:";
    private sealed record StoredHistory(Guid LibraryId, string AccountId, string FolderId, WorkspaceHistoryObservation Observation);
    private sealed record HashedHistory(string Document, string Sha256);

    /// <summary>Resume only at an atomically retained page cursor, initially the earliest
    /// unresolved start. Never replace it with today's start. Each new batch remains
    /// closed until actual replay/current verification succeeds; stored progress is not
    /// signed-history, predecessor or publication authority and cannot clear recovery.</summary>
    public WorkspaceObservationAttempt StartHistoryReplay(WorkspaceObservationAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(attempt.Context, transaction);
        if (ReadAttempt(transaction) != attempt || attempt.Completed is not null || attempt.HistoryReplay is { Active: true } ||
            attempt.FirstUnresolvedCursor is null || attempt.InventoryStartCursor is null) throw new WorkspaceObservationConflictException();
        var retained = attempt.HistoryReplay ?? new(attempt.FirstUnresolvedCursor, attempt.FirstUnresolvedCursor, 0, false, false, 0);
        var updated = attempt with { HistoryReplay = retained with { Active = true, AtEnd = false, BatchPages = 0 },
            HistoryInventorySha256 = HistoryDigest(ReadHistory(attempt.Context, transaction).Values) };
        WriteAttempt(updated, transaction); transaction.Commit(); return updated;
    }

    /// <summary>Atomically retain the entire observed page and its supported candidates
    /// before another network read or a recovery error. Recovery evidence is sticky;
    /// later successful listings/replays cannot erase it. No original is mutated.</summary>
    public WorkspaceObservationAttempt RememberHistoryPage(WorkspaceObservationAttempt attempt, string cursor, string nextCursor, bool atEnd,
        IReadOnlyCollection<WorkspaceHistoryEvent> events, IReadOnlyCollection<WorkspaceCandidateObservation> candidates)
    {
        ArgumentNullException.ThrowIfNull(attempt); ArgumentNullException.ThrowIfNull(events); ArgumentNullException.ThrowIfNull(candidates);
        if (!ValidAttemptCursor(cursor) || !ValidAttemptCursor(nextCursor) || !atEnd && cursor == nextCursor || events.Count > 100 || candidates.Count > 100)
            throw new ArgumentException("A bounded actual change page and explicit continuation are required.");
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(attempt.Context, transaction);
        if (ReadAttempt(transaction) != attempt || attempt.Completed is not null || attempt.HistoryReplay is not { AtEnd: false, Active: true } replay ||
            replay.Cursor != cursor || replay.BatchPages >= 128) throw new WorkspaceObservationConflictException();
        var retained = ReadHistory(attempt.Context, transaction); var changed = new Dictionary<string, WorkspaceHistoryObservation>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            ValidateHistoryIdentity(item.Kind, item.Id);
            if (!Digest(item.EvidenceSha256)) throw new ArgumentException("Observed change evidence needs its exact bounded digest.", nameof(events));
            var key = item.Kind + ":" + item.Id; retained.TryGetValue(key, out var previous);
            var chain = Hash(Encoding.UTF8.GetBytes(Encode(new { previous = previous?.HistorySha256, evidence = item }, 1024)));
            var value = new WorkspaceHistoryObservation(item.Kind, item.Id, item.RecoveryRequired || previous?.RecoveryRequired == true,
                checked((previous?.Occurrences ?? 0) + 1), previous?.FirstEvidenceSha256 ?? item.EvidenceSha256, item.EvidenceSha256, chain);
            retained[key] = value; changed[key] = value;
        }
        if (retained.Count > MaximumDocuments) throw new InvalidDataException("Workspace change history exceeds its complete retained identity bound.");
        using var reservation = _budget.Reserve(checked(changed.Count * 16384L + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        foreach (var pair in changed)
        {
            var document = Encode(new StoredHistory(attempt.Context.LibraryId, attempt.Context.AccountId, attempt.Context.FolderId, pair.Value), 2048);
            var encoded = Encode(new HashedHistory(document, Hash(Encoding.UTF8.GetBytes(document))), 4096);
            using var command = Command(transaction, "INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$key", HistoryPrefix + pair.Key), ("$value", encoded));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Change observation did not retain exactly one identity.");
        }
        var updated = attempt with { HistoryReplay = replay with { Cursor = nextCursor, Pages = checked(replay.Pages + 1), BatchPages = replay.BatchPages + 1, AtEnd = atEnd }, HistoryInventorySha256 = HistoryDigest(retained.Values) };
        WriteAttempt(updated, transaction);
        updated = RememberCandidates(updated, candidates, transaction);
        transaction.Commit(); return updated;
    }

    public ReadOnlyCollection<WorkspaceHistoryObservation> KnownHistory(WorkspaceObservationContext context)
    {
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(context, transaction); _ = ReadAttempt(transaction);
        var result = ReadHistory(context, transaction).Values.ToList().AsReadOnly(); transaction.Commit(); return result;
    }

    private Dictionary<string, WorkspaceHistoryObservation> ReadHistory(WorkspaceObservationContext context, SqliteTransaction transaction)
    {
        var result = new Dictionary<string, WorkspaceHistoryObservation>(StringComparer.Ordinal);
        using var command = Command(transaction, "SELECT key,length(value),value FROM processor_meta WHERE key GLOB 'workspace_history_observed_v1:*' ORDER BY key LIMIT 4097");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (result.Count == MaximumDocuments || reader.GetInt64(1) is < 1 or > 4096) throw new InvalidDataException("Retained change evidence exceeds its complete bound.");
            try
            {
                var text = reader.GetString(2); var wrapper = JsonSerializer.Deserialize<HashedHistory>(text) ?? throw new InvalidDataException("Change observation lost its integrity wrapper.");
                if (wrapper.Document is null || !Digest(wrapper.Sha256) || Encode(wrapper, 4096) != text) throw new InvalidDataException("Change observation lost its canonical integrity wrapper.");
                var stored = Decode<StoredHistory>(wrapper.Document, wrapper.Sha256, 2048); var value = stored.Observation;
                if (stored.LibraryId != context.LibraryId || stored.AccountId != context.AccountId || stored.FolderId != context.FolderId || value is null ||
                    value.Occurrences < 1 || !Digest(value.FirstEvidenceSha256) || !Digest(value.LastEvidenceSha256) || !Digest(value.HistorySha256))
                    throw new InvalidDataException("Retained change history changed its context or evidence.");
                ValidateHistoryIdentity(value.Kind, value.Id); var key = value.Kind + ":" + value.Id;
                if (reader.GetString(0) != HistoryPrefix + key) throw new InvalidDataException("Change history substituted an observed identity.");
                result.Add(key, value);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or Mpa.Processor.Protocol.ProtocolFormatException)
            { throw new InvalidDataException("Retained change evidence cannot be decoded safely.", error); }
        }
        return result;
    }
    private void AssertHistoryReplay(WorkspaceObservationAttempt attempt, SqliteTransaction transaction)
    {
        if (attempt.HistoryReplay is { } replay && (!replay.Active || !replay.AtEnd)) throw new WorkspaceObservationConflictException();
        var history = ReadHistory(attempt.Context, transaction);
        if (attempt.HistoryInventorySha256 is null ? history.Count != 0 : HistoryDigest(history.Values) != attempt.HistoryInventorySha256)
            throw new InvalidDataException("Workspace change evidence changed during result admission.");
        if (history.Values.Any(item => item.RecoveryRequired)) throw new WorkspaceHistoryRecoveryRequiredException();
    }
    private static void ValidateHistoryIdentity(string kind, string id)
    {
        if (kind is not ("file" or "drive") || !Identity(id) || id == "root") throw new ArgumentException("A canonical observed change kind and identity are required.");
    }
    private static string HistoryDigest(IEnumerable<WorkspaceHistoryObservation> observations)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("mpa-workspace-history-v1\0"u8); Span<byte> length = stackalloc byte[4];
        foreach (var value in observations.OrderBy(item => item.Kind + ":" + item.Id, StringComparer.Ordinal))
        {
            var bytes = Encoding.UTF8.GetBytes(Encode(value, 1024)); BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
