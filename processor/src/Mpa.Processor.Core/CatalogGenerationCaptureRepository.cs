using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record CatalogSnapshotDescriptor(Guid SnapshotId, Guid LibraryId, long ConfigurationRevision, long DiscoveryRevision,
    long Assets, long Bytes, string Sha256);
public sealed record CatalogGenerationCapture(WorkspacePublicationScope Scope, Guid SnapshotId, Guid CommitIntentId,
    string Phase, CatalogSnapshotDescriptor? Snapshot);
public sealed class CatalogGenerationInProgressException() : InvalidOperationException("Resume the existing catalog generation before starting another one.");
public sealed class CatalogGenerationCaptureConflictException() : InvalidOperationException("Captured catalog generation context conflicts with retained work. Preserve it for recovery.");

/// <summary>Serial durable capture identity/claim journal only. Local policy is checked
/// for fresh admission; native/cloud authority, generation scheduling, head ancestry and
/// signed publication are separate responsibilities. A retry never changes its original
/// access revision, snapshot, generation or signing-intent identity.</summary>
public sealed partial class CatalogGenerationCaptureRepository(ProcessorStateDatabase database, StorageBudgetManager budget)
{
    public const long MaximumSnapshotBytes = 512L * 1024 * 1024;
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));

    public CatalogGenerationCapture Begin(WorkspacePublicationScope scope, Guid snapshotId, Guid commitIntentId)
    {
        WorkspacePublicationRepository.ValidateScope(scope); Id(snapshotId); Id(commitIntentId);
        using var transaction = _database.Connection.BeginTransaction(); var old = Read(scope.GenerationId, transaction);
        if (old is not null)
        {
            if (old.Scope != scope || old.SnapshotId != snapshotId || old.CommitIntentId != commitIntentId || old.Phase == "abandoned") throw new CatalogGenerationCaptureConflictException();
            transaction.Commit(); return old; // Read/recover captured work even after a disconnect; live use requires fresh admission.
        }
        new LibraryAuthorityRepository(_database).AssertLocalPublicationScope(scope, transaction);
        using (var active = Command(transaction, "SELECT 1 FROM catalog_generation_capture WHERE library_id=$library AND phase!='abandoned'", ("$library", scope.LibraryId.ToString("D"))))
            if (active.ExecuteScalar() is not null) throw new CatalogGenerationInProgressException();
        using (var work = Command(transaction, """
            SELECT (SELECT count(*) FROM signing_intent WHERE id=$intent)+
              (SELECT count(*) FROM workspace_publication WHERE json_extract(scope,'$.GenerationId')=$id)
            """, ("$intent", commitIntentId.ToString("D")), ("$id", scope.GenerationId.ToString("D"))))
            if ((long)work.ExecuteScalar()! != 0) throw new CatalogGenerationCaptureConflictException();
        var encoded = WorkspacePublicationRepository.EncodeScope(scope); using var reservation = _budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
        using var insert = Command(transaction, """
            INSERT INTO catalog_generation_capture(id,library_id,snapshot_id,commit_intent_id,scope,context_hash,phase)
            VALUES($id,$library,$snapshot,$intent,$scope,$hash,'capturing')
            """, ("$id", scope.GenerationId.ToString("D")), ("$library", scope.LibraryId.ToString("D")), ("$snapshot", snapshotId.ToString("D")),
            ("$intent", commitIntentId.ToString("D")), ("$scope", encoded), ("$hash", ContextHash(encoded, snapshotId, commitIntentId)));
        if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Catalog capture admission did not insert exactly one record.");
        transaction.Commit(); return new(scope, snapshotId, commitIntentId, "capturing", null);
    }

    public CatalogGenerationCapture? Read(Guid generationId) { Id(generationId); return Read(generationId, null); }

    public ReadOnlyCollection<CatalogGenerationCapture> Active()
    {
        // This installation owns one library. Retain a bounded identity inventory and
        // validate every row before any caller decides which private files to clean.
        var ids = new List<Guid>();
        using (var command = Command(null, "SELECT id FROM catalog_generation_capture WHERE phase!='abandoned' ORDER BY id LIMIT 3"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(ParseId(reader.GetString(0)));
        if (ids.Count > 1) throw new InvalidDataException("More than one active catalog capture violates single-library ownership.");
        return ids.Select(id => Read(id) ?? throw new InvalidDataException("An active catalog capture disappeared.")).ToList().AsReadOnly();
    }

    /// <summary>Called only after the actual private SQLite owner closes and flushes its
    /// complete file and verifies this descriptor. Cancellation before this checkpoint
    /// leaves capturing work claimed; no current-library re-capture may replace frozen work.</summary>
    public CatalogGenerationCapture SealSnapshot(Guid generationId, CatalogSnapshotDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor); Id(generationId); ValidateDescriptor(descriptor);
        using var transaction = _database.Connection.BeginTransaction(); var current = Read(generationId, transaction) ?? throw new InvalidOperationException("The generation capture is missing.");
        MatchDescriptor(current, descriptor);
        if (current.Phase == "abandoned" || current.Snapshot is not null && current.Snapshot != descriptor) throw new CatalogGenerationCaptureConflictException();
        if (current.Snapshot is not null) { transaction.Commit(); return current; }
        var encoded = Encode(descriptor); using var reservation = _budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
        using var update = Command(transaction, "UPDATE catalog_generation_capture SET phase='frozen',snapshot=$snapshot,snapshot_hash=$hash WHERE id=$id AND phase='capturing' AND snapshot IS NULL",
            ("$snapshot", encoded), ("$hash", Hash(Encoding.UTF8.GetBytes(encoded))), ("$id", generationId.ToString("D")));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Snapshot sealing lost its durable captured owner.");
        transaction.Commit(); return current with { Phase = "frozen", Snapshot = descriptor };
    }

    /// <summary>No cleanup permission exists once any upload or signing work references
    /// the generation. Future published-head retirement needs a separate verified contract.</summary>
    public CatalogGenerationCapture AbandonUnpublished(Guid generationId)
    {
        Id(generationId); using var transaction = _database.Connection.BeginTransaction(); var current = Read(generationId, transaction) ?? throw new InvalidOperationException("The generation capture is missing.");
        if (current.Phase is "dependencies-ready" or "commit-sealed") throw new InvalidOperationException("Prepared publication work must be recovered; its snapshot claim cannot be abandoned.");
        using (var work = Command(transaction, """
            SELECT (SELECT count(*) FROM signing_intent WHERE id=$intent)+
              (SELECT count(*) FROM workspace_publication WHERE json_extract(scope,'$.GenerationId')=$id)
            """, ("$intent", current.CommitIntentId.ToString("D")), ("$id", generationId.ToString("D"))))
            if ((long)work.ExecuteScalar()! != 0) throw new InvalidOperationException("Signed or uploaded generation work must be recovered; its snapshot claim cannot be abandoned.");
        if (current.Phase != "abandoned")
        {
            using var reservation = _budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
            using var update = Command(transaction, "UPDATE catalog_generation_capture SET phase='abandoned' WHERE id=$id", ("$id", generationId.ToString("D")));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Unpublished capture abandonment lost its owner.");
        }
        transaction.Commit(); return current with { Phase = "abandoned" };
    }

    /// <summary>A filename/marker alone is not cleanup authority. Recovery requires
    /// this exact snapshot in retained abandoned history and no signed/upload work.
    /// Call under the serial state owner before deleting any validated orphan file.</summary>
    public void AssertUnpublishedSnapshotCleanup(Guid snapshotId)
    {
        Id(snapshotId); using var transaction = _database.Connection.BeginTransaction(); Guid generation;
        using (var lookup = Command(transaction, "SELECT id FROM catalog_generation_capture WHERE snapshot_id=$snapshot", ("$snapshot", snapshotId.ToString("D"))))
            generation = lookup.ExecuteScalar() is string id ? ParseId(id) : throw new InvalidDataException("Generated-looking snapshot has no retained ownership history; preserve it.");
        var current = Read(generation, transaction) ?? throw new InvalidDataException("Retained snapshot history disappeared.");
        if (current.Phase != "abandoned") throw new InvalidOperationException("A live generation snapshot cannot be removed by recovery.");
        using var work = Command(transaction, """
            SELECT (SELECT count(*) FROM signing_intent WHERE id=$intent)+
              (SELECT count(*) FROM workspace_publication WHERE json_extract(scope,'$.GenerationId')=$id)
            """, ("$intent", current.CommitIntentId.ToString("D")), ("$id", generation.ToString("D")));
        if ((long)work.ExecuteScalar()! != 0) throw new InvalidOperationException("Abandoned capture has later signed/uploaded work; preserve its file and reconcile.");
        transaction.Commit();
    }

    private CatalogGenerationCapture? Read(Guid generationId, SqliteTransaction? transaction)
    {
        using var command = Command(transaction, """
            SELECT id,library_id,snapshot_id,commit_intent_id,length(scope),scope,context_hash,phase,length(snapshot),snapshot,snapshot_hash,
              (SELECT value FROM processor_meta WHERE key='library_id') FROM catalog_generation_capture WHERE id=$id
            """, ("$id", generationId.ToString("D")));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(4) is < 1 or > 4096 || !reader.IsDBNull(8) && reader.GetInt64(8) is < 1 or > 4096) throw new InvalidDataException("Captured generation metadata exceeds its bound.");
        var snapshotId = ParseId(reader.GetString(2)); var intent = ParseId(reader.GetString(3)); var encoded = reader.GetString(5);
        try
        {
            var scope = JsonSerializer.Deserialize<WorkspacePublicationScope>(encoded) ?? throw new InvalidDataException("Captured generation scope is missing.");
            WorkspacePublicationRepository.ValidateScope(scope);
            if (reader.GetString(0) != generationId.ToString("D") || scope.GenerationId != generationId || reader.GetString(1) != scope.LibraryId.ToString("D") ||
                reader.IsDBNull(11) || reader.GetString(11) != scope.LibraryId.ToString("D") || WorkspacePublicationRepository.EncodeScope(scope) != encoded ||
                reader.GetString(6) != ContextHash(encoded, snapshotId, intent)) throw new InvalidDataException("Captured generation identity/scope integrity failed.");
            var phase = reader.GetString(7); if (phase is not ("capturing" or "frozen" or "publication-planned" or "dependencies-ready" or "commit-sealed" or "abandoned") || reader.IsDBNull(9) != reader.IsDBNull(10)) throw new InvalidDataException("Captured generation phase or snapshot proof is invalid.");
            CatalogSnapshotDescriptor? snapshot = null;
            if (!reader.IsDBNull(9))
            {
                var data = reader.GetString(9); snapshot = JsonSerializer.Deserialize<CatalogSnapshotDescriptor>(data) ?? throw new InvalidDataException("Retained snapshot descriptor is missing."); ValidateDescriptor(snapshot);
                if (data != Encode(snapshot) || reader.GetString(10) != Hash(Encoding.UTF8.GetBytes(data))) throw new InvalidDataException("Retained snapshot descriptor integrity failed.");
                MatchDescriptor(new(scope, snapshotId, intent, phase, null), snapshot);
            }
            if (phase is "frozen" or "publication-planned" or "dependencies-ready" or "commit-sealed" && snapshot is null || phase == "capturing" && snapshot is not null) throw new InvalidDataException("Captured generation phase disagrees with its frozen file proof.");
            var capture = new CatalogGenerationCapture(scope, snapshotId, intent, phase, snapshot);
            reader.Close(); ValidatePublicationState(capture, transaction); return capture;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or ProtocolFormatException or CatalogGenerationCaptureConflictException)
        { throw new InvalidDataException("Retained generation metadata is invalid; preserve it for recovery.", error); }
    }
    internal static void ValidateDescriptor(CatalogSnapshotDescriptor value)
    {
        Id(value.SnapshotId); Id(value.LibraryId);
        if (value.ConfigurationRevision < 1 || value.DiscoveryRevision < 1 || value.Assets < 0 || value.Bytes is < 4096 or > MaximumSnapshotBytes ||
            value.Sha256 is not { Length: 64 } || value.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A complete bounded frozen snapshot descriptor is required.", nameof(value));
    }
    private static void MatchDescriptor(CatalogGenerationCapture capture, CatalogSnapshotDescriptor descriptor)
    { if (descriptor.SnapshotId != capture.SnapshotId || descriptor.LibraryId != capture.Scope.LibraryId || descriptor.ConfigurationRevision != capture.Scope.ConfigurationRevision) throw new CatalogGenerationCaptureConflictException(); }
    private static string ContextHash(string scope, Guid snapshotId, Guid intent) => Hash(new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(new { scope, snapshotId, commitIntentId = intent })));
    private static string Encode(CatalogSnapshotDescriptor descriptor) => Encoding.UTF8.GetString(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(descriptor)));
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static void Id(Guid value) { if (value == Guid.Empty) throw new ArgumentException("A stable captured identity is required.", nameof(value)); }
    private static Guid ParseId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value ? id : throw new InvalidDataException("Retained generation identity is invalid.");
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Key, object Value)[] values)
    { var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var pair in values) command.Parameters.AddWithValue(pair.Key, pair.Value); return command; }
}
