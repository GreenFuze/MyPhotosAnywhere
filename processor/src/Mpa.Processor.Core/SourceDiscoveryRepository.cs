using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record SourceDiscoveryScope(Guid SourceId, long SourceRevision, string AccountId, long AccountRevision, string RootFolderId, bool IncludeDescendants);
public sealed record SourceDiscoveryFolder(string FolderId, string? SharedDriveId, int Depth);
public sealed record SourceDiscoveryWork(StageLease Lease, SourceDiscoveryScope Scope, SourceDiscoveryFolder Folder);
public sealed record SourceDiscoveryCheckpoint(string? NextPageToken, long ObservedItems, long FailedItems, bool IncompleteSearch, bool Started);

/// <summary>Serial state adapter for metadata-only folder jobs. Network readers supply verified
/// folder facts; requests cannot retarget the captured source/account. No native worker or
/// original action is admitted here. Pause/release belongs to the joined discovery owner.</summary>
public sealed partial class SourceDiscoveryRepository
{
    public const string JobKind = "source-discovery";
    private const string Stage = "list-metadata";
    private static readonly string Recipe = Convert.ToHexStringLower(SHA256.HashData("mpa-source-metadata:1"u8));
    private readonly ProcessorStateDatabase _database;
    private readonly DurableJobRepository _jobs;
    private readonly DriveAssetIdentity _identity;
    private readonly CanonicalJson _codec = new(32768);

    public SourceDiscoveryRepository(ProcessorStateDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database)); _jobs = new(database);
        _identity = new(new ProcessorLibraryRepository(database).GetOrCreateIdentity());
    }
    public void Create(Guid jobId, Guid sourceId, long sourceRevision, long accountRevision, string verifiedRootFolderId, string? verifiedSharedDriveId)
    {
        if (jobId == Guid.Empty || sourceId == Guid.Empty || sourceRevision < 1 || accountRevision < 1 || (!Identity(verifiedRootFolderId) || verifiedRootFolderId == "root") ||
            verifiedSharedDriveId is not null && !Identity(verifiedSharedDriveId)) throw new ArgumentException("Invalid captured discovery source.");
        var scope = CaptureScope(sourceId, sourceRevision, accountRevision);
        if (scope.RootFolderId != verifiedRootFolderId) throw new StaleLibraryConfigurationException();
        _jobs.CreateWithInitialItem(jobId, JobKind, ScopeContext(scope), Item(scope, new(verifiedRootFolderId, verifiedSharedDriveId, 0)),
            new ScanAdmission(this, scope));
    }
    private SourceDiscoveryScope CaptureScope(Guid sourceId, long sourceRevision, long accountRevision)
    {
        if (sourceId == Guid.Empty || sourceRevision < 1 || accountRevision < 1) throw new ArgumentException("Invalid captured discovery source.");
        using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT s.account_id,s.descendants,s.folder_id FROM source_binding s JOIN drive_account a ON a.permission_id=s.account_id
            WHERE s.id=$id AND s.revision=$sourceRevision AND s.enabled=1 AND a.connected=1 AND a.revision=$accountRevision
            """;
        command.Parameters.AddWithValue("$id", sourceId.ToString("D")); command.Parameters.AddWithValue("$sourceRevision", sourceRevision);
        command.Parameters.AddWithValue("$accountRevision", accountRevision);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new StaleLibraryConfigurationException();
        var folder = reader.GetString(2);
        if (!Identity(folder) || folder == "root") throw new InvalidDataException("Persisted source root is not canonical.");
        return new(sourceId, sourceRevision, reader.GetString(0), accountRevision, folder, reader.GetBoolean(1));
    }
    private byte[] ScopeContext(SourceDiscoveryScope scope) => _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-discovery-1", sourceId = scope.SourceId.ToString("D"),
        sourceRevision = scope.SourceRevision.ToString(CultureInfo.InvariantCulture), accountId = scope.AccountId,
        accountRevision = scope.AccountRevision.ToString(CultureInfo.InvariantCulture), rootFolderId = scope.RootFolderId, includeDescendants = scope.IncludeDescendants }));
    public SourceDiscoveryWork? ClaimNext()
    {
        var lease = _jobs.ClaimNext(JobKind, Stage);
        if (lease is null) return null;
        var work = new SourceDiscoveryWork(lease, Scope(lease.CapturedContext), Folder(lease.CapturedItemContext));
        ValidateShape(work); return work;
    }
    public bool EnqueueChild(SourceDiscoveryWork parent, string verifiedFolderId, string? verifiedSharedDriveId)
    {
        ValidateActive(parent); ValidateCurrent(parent);
        if (!parent.Scope.IncludeDescendants) return false;
        if (!Identity(verifiedFolderId) || verifiedSharedDriveId is not null && !Identity(verifiedSharedDriveId)) throw new ArgumentException("Invalid verified child folder.");
        if (ManagedWorkspaceBoundary.Capture(_database).ContainsRoot(verifiedFolderId)) return false;
        if (parent.Folder.Depth == 256 || verifiedFolderId == parent.Folder.FolderId || verifiedFolderId == parent.Scope.RootFolderId)
            throw new InvalidDataException("Source traversal contains a cycle or exceeds the supported depth.");
        if (parent.Folder.SharedDriveId is not null && parent.Folder.SharedDriveId != verifiedSharedDriveId)
            throw new InvalidDataException("A child folder cannot escape its captured shared-drive scope.");
        return _jobs.EnsureItem(parent.Lease.JobId, Item(parent.Scope, new(verifiedFolderId, verifiedSharedDriveId, parent.Folder.Depth + 1)));
    }
    public void ValidateCurrent(SourceDiscoveryWork work)
    {
        ValidateActive(work); ValidateCurrent(work.Scope);
    }

    public void CompleteFolder(SourceDiscoveryWork work)
    {
        if (TryExcludeManagedWorkspace(work)) return;
        ValidateCurrent(work); var checkpoint = Checkpoint(work);
        if (!checkpoint.Started || checkpoint.NextPageToken is not null) throw new InvalidOperationException("A folder requires a terminal checkpoint before completion.");
        _jobs.Complete(work.Lease, _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-folder-receipt-1",
            sourceId = work.Scope.SourceId.ToString("D"), folderId = work.Folder.FolderId,
            observedItems = checkpoint.ObservedItems.ToString(CultureInfo.InvariantCulture),
            failedItems = checkpoint.FailedItems.ToString(CultureInfo.InvariantCulture), incompleteSearch = checkpoint.IncompleteSearch })));
    }
    private void ValidateCurrent(SourceDiscoveryScope scope)
    {
        using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM source_binding s JOIN drive_account a ON a.permission_id=s.account_id
            WHERE s.id=$id AND s.revision=$sourceRevision AND s.account_id=$account AND s.folder_id=$root AND s.enabled=1
              AND s.descendants=$descendants AND a.connected=1 AND a.revision=$accountRevision
            """;
        command.Parameters.AddWithValue("$id", scope.SourceId.ToString("D")); command.Parameters.AddWithValue("$sourceRevision", scope.SourceRevision);
        command.Parameters.AddWithValue("$account", scope.AccountId); command.Parameters.AddWithValue("$root", scope.RootFolderId);
        command.Parameters.AddWithValue("$descendants", scope.IncludeDescendants); command.Parameters.AddWithValue("$accountRevision", scope.AccountRevision);
        if (command.ExecuteScalar() is null) throw new StaleLibraryConfigurationException();
    }
    public SourceDiscoveryCheckpoint Checkpoint(SourceDiscoveryWork work)
    {
        ValidateShape(work);
        return _journal.Checkpoint(work.Lease.Checkpoint);
    }

    public SourceDiscoveryWork Advance(SourceDiscoveryWork work, string? nextPageToken, int observedItems, int failedItems, bool incompleteSearch)
    {
        ValidateActive(work); ValidateCurrent(work);
        if (observedItems is < 0 or > 60 || failedItems is < 0 or > 60 || observedItems + failedItems > 60 ||
            nextPageToken is not null && (nextPageToken.Length is < 1 or > 4096 || nextPageToken.Any(char.IsControl))) throw new ArgumentException("Invalid bounded discovery checkpoint.");
        var prior = Checkpoint(work);
        if (prior.Started && prior.NextPageToken is null) throw new InvalidOperationException("A completed folder cannot admit another page.");
        if (nextPageToken is not null && nextPageToken == prior.NextPageToken) throw new InvalidDataException("Source pagination repeated its current checkpoint.");
        var context = Encoding.UTF8.GetString(_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-checkpoint-1", nextPageToken,
            observedItems = checked(prior.ObservedItems + observedItems).ToString(CultureInfo.InvariantCulture),
            failedItems = checked(prior.FailedItems + failedItems).ToString(CultureInfo.InvariantCulture), incompleteSearch = prior.IncompleteSearch || incompleteSearch })));
        _jobs.SaveCheckpoint(work.Lease, Encoding.UTF8.GetBytes(context));
        return work with { Lease = work.Lease with { Checkpoint = context } };
    }
    private void ValidateActive(SourceDiscoveryWork work)
    {
        ValidateShape(work); ValidateActive(work.Lease);
    }
    private void ValidateActive(StageLease lease)
    {
        using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM item_stage s JOIN job j ON j.id=s.job_id JOIN job_item i ON i.job_id=s.job_id AND i.id=s.item_id
            WHERE s.job_id=$job AND s.item_id=$item AND s.name=$stage AND s.lease_id=$lease AND s.attempt=$attempt AND s.state='running'
              AND j.kind=$kind AND j.paused=0 AND j.canceled=0 AND j.captured_context=$jobContext AND i.captured_context=$itemContext
              AND s.checkpoint IS $checkpoint
            """;
        command.Parameters.AddWithValue("$job", lease.JobId.ToString("D")); command.Parameters.AddWithValue("$item", lease.ItemId.ToString("D"));
        command.Parameters.AddWithValue("$stage", lease.Stage); command.Parameters.AddWithValue("$lease", lease.LeaseId.ToString("D"));
        command.Parameters.AddWithValue("$attempt", lease.Attempt); command.Parameters.AddWithValue("$kind", JobKind);
        command.Parameters.AddWithValue("$jobContext", lease.CapturedContext); command.Parameters.AddWithValue("$itemContext", lease.CapturedItemContext);
        command.Parameters.AddWithValue("$checkpoint", (object?)lease.Checkpoint ?? DBNull.Value);
        if (command.ExecuteScalar() is null) throw new StaleStageLeaseException();
    }
    private void ValidateShape(SourceDiscoveryWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Scope != Scope(work.Lease.CapturedContext) || work.Folder != Folder(work.Lease.CapturedItemContext) || work.Lease.Stage != Stage ||
            _identity.Derive(work.Scope.AccountId, work.Folder.FolderId) != work.Lease.ItemId || work.Folder.Depth == 0 && work.Folder.FolderId != work.Scope.RootFolderId)
            throw new InvalidDataException("Discovery work does not match its captured durable identity.");
    }
    private JobItemSpec Item(SourceDiscoveryScope scope, SourceDiscoveryFolder folder)
    {
        var context = _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-folder-1", folderId = folder.FolderId, sharedDriveId = folder.SharedDriveId, depth = folder.Depth }));
        return new(_identity.Derive(scope.AccountId, folder.FolderId), Convert.ToHexStringLower(SHA256.HashData(context)), [new(Stage, Recipe, [])], Encoding.UTF8.GetString(context));
    }
    private readonly SourceDiscoveryJournalCodec _journal = new();
    private SourceDiscoveryScope Scope(string value) => _journal.Scope(value);
    private SourceDiscoveryFolder Folder(string value) => _journal.Folder(value);
    private JsonDocument Object(string value, int count, string format) => _journal.Object(value, count, format);
    private static long Decimal(JsonElement root, string name, bool zero) => SourceDiscoveryJournalCodec.Decimal(root, name, zero);
    private static bool Identity(string? value) => SourceDiscoveryJournalCodec.Identity(value);
}
