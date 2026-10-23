using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Actual inventory metadata retained even if its subsequent download or
/// verification fails. It is not a verified content hash, signature or publication receipt.</summary>
public sealed record WorkspaceCandidateObservation(string FileId, long Version, long Bytes);

public sealed partial class WorkspaceObservationRepository
{
    private const string CandidatePrefix = "workspace_observed_candidate_v1:";
    private sealed record StoredCandidate(Guid LibraryId, string AccountId, string FolderId, WorkspaceCandidateObservation Observation);
    private sealed record HashedCandidate(string Document, string Sha256);

    public ReadOnlyCollection<string> KnownIdentities(WorkspaceObservationContext context)
    {
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(context, transaction); _ = ReadAttempt(transaction);
        var ids = ReadCandidates(context, transaction).Keys.Union(ReadAll(context, transaction).Select(value => value.FileId), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        if (ids.Count > MaximumDocuments) throw new InvalidDataException("Retained workspace identities exceed their bound.");
        transaction.Commit(); return ids.AsReadOnly();
    }

    /// <summary>Retain every listed candidate atomically before downloading the first.
    /// Unknown unreadable documents cannot disappear from a later supposedly complete scan.</summary>
    public WorkspaceObservationAttempt RememberCandidates(WorkspaceObservationAttempt attempt, IReadOnlyCollection<WorkspaceCandidateObservation> candidates)
    {
        ArgumentNullException.ThrowIfNull(attempt); ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > MaximumDocuments) throw new ArgumentException("Workspace candidates exceed their bound.", nameof(candidates));
        using var transaction = _database.Connection.BeginTransaction(); AssertContext(attempt.Context, transaction);
        if (attempt.Completed is not null || ReadAttempt(transaction) != attempt) throw new WorkspaceObservationConflictException();
        var retained = ReadCandidates(attempt.Context, transaction); var read = ReadAll(attempt.Context, transaction).ToDictionary(value => value.FileId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); var changed = new List<WorkspaceCandidateObservation>();
        foreach (var candidate in candidates)
        {
            ValidateCandidate(candidate, attempt.Context);
            if (!seen.Add(candidate.FileId)) throw new WorkspaceObservationConflictException();
            if (retained.TryGetValue(candidate.FileId, out var old) && (old.Bytes != candidate.Bytes || old.Version > candidate.Version) ||
                read.TryGetValue(candidate.FileId, out var actual) && (actual.Bytes != candidate.Bytes || actual.Version > candidate.Version))
                throw new WorkspaceObservationConflictException();
            if (candidate != old) changed.Add(candidate);
            retained[candidate.FileId] = candidate;
        }
        if (retained.Keys.Union(read.Keys, StringComparer.Ordinal).Count() > MaximumDocuments) throw new InvalidDataException("Workspace candidate history exceeds its bound.");
        using var reservation = _budget.Reserve(checked(changed.Count * 16384L + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        foreach (var candidate in changed)
        {
            var document = Encode(new StoredCandidate(attempt.Context.LibraryId, attempt.Context.AccountId, attempt.Context.FolderId, candidate), 2048);
            var encoded = Encode(new HashedCandidate(document, Hash(Encoding.UTF8.GetBytes(document))), 4096);
            using var command = Command(transaction, """
                INSERT INTO processor_meta(key,value) VALUES($key,$value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """, ("$key", CandidatePrefix + candidate.FileId), ("$value", encoded));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workspace candidate did not retain exactly one identity.");
        }
        var updated = attempt with { CandidateInventorySha256 = CandidateInventoryHash(retained.Values) };
        WriteAttempt(updated, transaction); transaction.Commit(); return updated;
    }

    private Dictionary<string, WorkspaceCandidateObservation> ReadCandidates(WorkspaceObservationContext context, SqliteTransaction transaction)
    {
        var result = new Dictionary<string, WorkspaceCandidateObservation>(StringComparer.Ordinal);
        using var command = Command(transaction, "SELECT key,length(value),value FROM processor_meta WHERE key GLOB 'workspace_observed_candidate_v1:*' ORDER BY key LIMIT 4097");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (result.Count == MaximumDocuments || reader.GetInt64(1) is < 1 or > 4096) throw new InvalidDataException("Retained workspace candidate exceeds its bound.");
            try
            {
                var text = reader.GetString(2); var wrapper = JsonSerializer.Deserialize<HashedCandidate>(text) ?? throw new InvalidDataException("Workspace candidate lost its integrity wrapper.");
                if (wrapper.Document is null || !Digest(wrapper.Sha256) || Encode(wrapper, 4096) != text) throw new InvalidDataException("Workspace candidate lost its canonical integrity wrapper.");
                var value = Decode<StoredCandidate>(wrapper.Document, wrapper.Sha256, 2048);
                if (value.LibraryId != context.LibraryId || value.AccountId != context.AccountId || value.FolderId != context.FolderId || value.Observation is null ||
                    CandidatePrefix + value.Observation.FileId != reader.GetString(0)) throw new InvalidDataException("Retained candidate changed workspace or identity.");
                ValidateCandidate(value.Observation, context); result.Add(value.Observation.FileId, value.Observation);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException)
            { throw new InvalidDataException("Retained workspace candidate cannot be decoded safely.", error); }
        }
        return result;
    }

    private void AssertCandidatesRead(WorkspaceObservationContext context, SqliteTransaction transaction)
    {
        var read = ReadAll(context, transaction).ToDictionary(value => value.FileId, StringComparer.Ordinal);
        foreach (var candidate in ReadCandidates(context, transaction).Values)
            if (!read.TryGetValue(candidate.FileId, out var document) || document.Bytes != candidate.Bytes || document.Version < candidate.Version)
                throw new WorkspaceObservationConflictException();
    }
    private static void ValidateCandidate(WorkspaceCandidateObservation value, WorkspaceObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Identity(value.FileId) || value.FileId == "root" || value.FileId == context.FolderId || value.Version < 1 ||
            value.Bytes is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes) throw new ArgumentException("A bounded actual workspace document candidate is required.", nameof(value));
    }
    private static string CandidateInventoryHash(IEnumerable<WorkspaceCandidateObservation> candidates)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("mpa-workspace-candidates-v1\0"u8); Span<byte> length = stackalloc byte[4];
        foreach (var candidate in candidates.OrderBy(value => value.FileId, StringComparer.Ordinal))
        {
            var bytes = Encoding.UTF8.GetBytes(Encode(candidate, 1024)); BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
