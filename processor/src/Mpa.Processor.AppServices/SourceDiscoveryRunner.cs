using System.Net;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed record SourceDiscoveryStep(Guid JobId, Guid ItemId, bool Repair, bool Complete, bool Failed)
{ public string Stage { get; init; } = Repair ? "repair-metadata" : "list-metadata"; }

/// <summary>Owns one bounded metadata step at a time, independent from any browser. It borrows
/// the engine account/state owners, never reads original bytes, and joins reads before yielding
/// their durable leases. This runner does not itself start a timer or admit unselected sources.</summary>
public sealed class SourceDiscoveryRunner(ProcessorStateCoordinator state, ProcessorAccountService accounts, StorageBudgetManager? storage = null) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly StorageBudgetManager? _storage = storage;
    // One bounded metadata page has at most 60 entries / 512 KiB. Reserve additional
    // SQLite index/WAL/checkpoint headroom; no original bytes are admitted by this lease.
    public const long MetadataStepReservationBytes = 16L * 1024 * 1024;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _stopping;
    private Task? _disposal;
    private Exception? _failure;
    private StorageReservation? _reservation;
    public Exception? Failure { get { lock (_gate) return _failure; } }

    public Task<int> RecoverInterruptedReadsAfterExclusiveRestartAsync(CancellationToken cancellationToken) => ExecuteAsync(token =>
        _state.ExecuteAsync(database => new SourceDiscoveryRepository(database).RecoverInterruptedReadsAfterExclusiveRestart(), token), cancellationToken, reserveWrites: false);

    public Task<SourceDiscoverySchedule> EnsureScheduledAsync(Guid sourceId, long expectedSourceRevision, CancellationToken cancellationToken) => ExecuteAsync(token =>
        StateAsync(database =>
        {
            var source = new LibrarySourceRepository(database).Snapshot().Sources.SingleOrDefault(source => source.Id == sourceId && source.Revision == expectedSourceRevision && source.Enabled && source.AccountConnected)
                ?? throw new StaleLibraryConfigurationException();
            var account = new DriveAccountRepository(database).Accounts().Single(account => account.PermissionId == source.AccountId && account.Connected);
            return new SourceDiscoveryRepository(database).EnsureScheduled(source.Id, source.Revision, account.Revision);
        }, token), cancellationToken);

    public Task<Guid> ScheduleAsync(Guid sourceId, long expectedSourceRevision, CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        var captured = await StateAsync(database =>
        {
            var source = new LibrarySourceRepository(database).Snapshot().Sources.SingleOrDefault(source => source.Id == sourceId && source.Revision == expectedSourceRevision && source.Enabled && source.AccountConnected)
                ?? throw new StaleLibraryConfigurationException();
            var account = new DriveAccountRepository(database).Accounts().Single(account => account.PermissionId == source.AccountId && account.Connected);
            return (Source: source, AccountRevision: account.Revision);
        }, token).ConfigureAwait(false);
        var root = await _accounts.ReadDriveAsync(captured.Source.AccountId, async (reader, revision, readToken) =>
        {
            if (revision != captured.AccountRevision) throw new StaleLibraryConfigurationException();
            return await reader.ReadFolderAsync(captured.Source.FolderId, readToken).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        if (!root.CanListChildren) throw new HttpRequestException("Google does not allow reading the selected source folder.", null, HttpStatusCode.Forbidden);
        var job = Guid.NewGuid();
        await StateAsync(database =>
        {
            new SourceDiscoveryRepository(database).Create(job, sourceId, expectedSourceRevision, captured.AccountRevision, root.Id, root.SharedDriveId); return true;
        }, token).ConfigureAwait(false);
        return job;
    }, cancellationToken);

    public Task<SourceDiscoveryStep?> RunNextAsync(CancellationToken cancellationToken) => ExecuteAsync<SourceDiscoveryStep?>(async token =>
    {
        var captured = await StateAsync(database =>
        {
            var repository = new SourceDiscoveryRepository(database);
            var verification = repository.ClaimVerification();
            var folder = verification is null ? repository.ClaimNext() : null;
            return (Verification: verification, Folder: folder, Repair: verification is null && folder is null ? repository.ClaimRepair() : null);
        }, token).ConfigureAwait(false);
        var lease = captured.Verification?.Lease ?? captured.Folder?.Lease ?? captured.Repair?.Lease;
        if (lease is null) return null;
        try
        {
            if (captured.Verification is not null) return await VerifyAsync(captured.Verification, token).ConfigureAwait(false);
            return captured.Folder is not null ? await FolderAsync(captured.Folder, token).ConfigureAwait(false) :
                await RepairAsync(captured.Repair!, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await ReturnAsync(lease).ConfigureAwait(false); throw; }
        catch (InsufficientStorageBudgetException)
        { await ReturnAsync(lease).ConfigureAwait(false); throw; }
        catch (StaleStageLeaseException)
        {
            var policy = await _state.ExecuteAsync(database => new DurableJobRepository(database).Summary(lease.JobId), CancellationToken.None).ConfigureAwait(false);
            if (!policy.Paused && !policy.IsCanceled)
            { lock (_gate) _failure = new StaleStageLeaseException(); throw; }
            await ReturnAsync(lease).ConfigureAwait(false); return new(lease.JobId, lease.ItemId, captured.Repair is not null, false, false) { Stage = lease.Stage };
        }
        catch (StaleLibraryConfigurationException)
        { return await FailAsync(lease, captured.Repair is not null, "source-changed", "The source or account changed. Start a scan using its current settings.", false).ConfigureAwait(false); }
        catch (DriveSourceMetadataException)
        { return await FailAsync(lease, captured.Repair is not null, "invalid-source-metadata", "The captured item's metadata is still invalid. Other items can continue; retry this item after correcting the cause.", true).ConfigureAwait(false); }
        catch (DriveSourceItemUnavailableException)
        { return await FailAsync(lease, captured.Repair is not null, "source-item-moved", "The captured item moved outside this source folder or shared drive. Check its source, then retry; existing media was retained.", true).ConfigureAwait(false); }
        catch (HttpRequestException error)
        {
            var authorization = error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            return await FailAsync(lease, captured.Repair is not null, authorization ? "drive-authorization" : "drive-unavailable",
                authorization ? "Google Drive access needs attention. Check this account and source, then retry the failed items." :
                    "Google Drive could not complete the metadata read. Other items can continue; retry the failed items.", true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { return await FailAsync(lease, captured.Repair is not null, "drive-timeout", "Google Drive did not finish the metadata read in time. Retry the failed items.", true).ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate) _failure = error;
            // The actual read has joined. Preserve its last checkpoint and fail visibly;
            // an internal invariant failure is never converted into an empty/ready result.
            try { await ReturnAsync(lease).ConfigureAwait(false); }
            catch (Exception release) { throw new AggregateException(error, release); }
            throw;
        }
    }, cancellationToken, faultOnError: true);

    private async Task<SourceDiscoveryStep> VerifyAsync(SourceDiscoveryVerification work, CancellationToken token)
    {
        var accepted = await StateAsync(database =>
        {
            var repository = new SourceDiscoveryRepository(database); repository.ValidateCurrent(work); return repository.VerifiedRoot(work);
        }, token).ConfigureAwait(false);
        if (accepted is null)
        {
            var root = await _accounts.ReadDriveAsync(work.Scope.AccountId, (reader, revision, readToken) =>
            {
                if (revision != work.Scope.AccountRevision) throw new StaleLibraryConfigurationException();
                return reader.ReadFolderAsync(work.Scope.RootFolderId, readToken);
            }, token).ConfigureAwait(false);
            if (!root.CanListChildren) throw new HttpRequestException("Google does not allow reading the captured source folder.", null, HttpStatusCode.Forbidden);
            work = await StateAsync(database => new SourceDiscoveryRepository(database).SaveVerifiedRoot(work, root.Id, root.SharedDriveId), token).ConfigureAwait(false);
        }
        await StateAsync(database => { new SourceDiscoveryRepository(database).CompleteVerification(work); return true; }, token).ConfigureAwait(false);
        return new(work.Lease.JobId, work.Lease.ItemId, false, true, false) { Stage = work.Lease.Stage };
    }

    private async Task<SourceDiscoveryStep> FolderAsync(SourceDiscoveryWork work, CancellationToken token)
    {
        var checkpoint = await StateAsync(database =>
        {
            var repository = new SourceDiscoveryRepository(database); repository.ValidateCurrent(work); return repository.Checkpoint(work);
        }, token).ConfigureAwait(false);
        if (checkpoint.Started && checkpoint.NextPageToken is null)
        {
            await StateAsync(database => { new SourceDiscoveryRepository(database).CompleteFolder(work); return true; }, token).ConfigureAwait(false);
            return new(work.Lease.JobId, work.Lease.ItemId, false, true, false);
        }
        var page = await ReadPageAsync(work.Scope, work.Folder, checkpoint.NextPageToken, token).ConfigureAwait(false);
        return await StateAsync(database =>
        {
            var repository = new SourceDiscoveryRepository(database); repository.ValidateCurrent(work);
            var failures = page.Failures.Select(failure => failure.FileId).ToList();
            foreach (var folder in page.Items.Where(item => item.Folder is not null).Select(item => item.Folder!))
                if (work.Scope.IncludeDescendants)
                {
                    if (folder.CanListChildren) repository.EnqueueChild(work, folder.Id, folder.SharedDriveId);
                    else failures.Add(folder.Id);
                }
            foreach (var failed in failures) repository.RecordFailure(work, failed);
            if (page.IncompleteSearch) repository.RecordFailure(work, null);
            ApplyMedia(database, work.Scope, work.Folder, page.Items);
            var advanced = repository.Advance(work, page.NextPageToken, page.Items.Count - (failures.Count - page.Failures.Count), failures.Count, page.IncompleteSearch);
            if (page.NextPageToken is null) repository.CompleteFolder(advanced);
            else new DurableJobRepository(database).ReturnJoinedRead(advanced.Lease);
            return new SourceDiscoveryStep(work.Lease.JobId, work.Lease.ItemId, false, page.NextPageToken is null, false);
        }, token).ConfigureAwait(false);
    }

    private async Task<SourceDiscoveryStep> RepairAsync(SourceDiscoveryRepair repair, CancellationToken token)
    {
        await StateAsync(database => { new SourceDiscoveryRepository(database).ValidateCurrent(repair); return true; }, token).ConfigureAwait(false);
        var page = repair.FileId is null ? await ReadPageAsync(repair.Scope, repair.Folder, repair.PageToken, token).ConfigureAwait(false) :
            new DriveSourcePage([await _accounts.ReadDriveAsync(repair.Scope.AccountId, (reader, revision, readToken) =>
            {
                if (revision != repair.Scope.AccountRevision) throw new StaleLibraryConfigurationException();
                return reader.ReadSourceItemAsync(repair.Folder.FolderId, repair.Folder.SharedDriveId, repair.FileId, readToken);
            }, token).ConfigureAwait(false)], [], null, false);
        return await StateAsync(database =>
        {
            var repository = new SourceDiscoveryRepository(database); repository.ValidateCurrent(repair);
            var inaccessible = false;
            foreach (var folder in page.Items.Where(item => item.Folder is not null).Select(item => item.Folder!))
                if (repair.Scope.IncludeDescendants)
                {
                    if (folder.CanListChildren) repository.EnqueueChild(repair, folder.Id, folder.SharedDriveId);
                    else inaccessible = true;
                }
            ApplyMedia(database, repair.Scope, repair.Folder, page.Items);
            var failed = page.Failures.Count != 0 || inaccessible || page.IncompleteSearch;
            if (failed) new DurableJobRepository(database).Fail(repair.Lease, "invalid-source-metadata", "This captured metadata read remains incomplete. Healthy entries were retained; retry this failed observation.", true);
            else repository.CompleteRepair(repair);
            return new SourceDiscoveryStep(repair.Lease.JobId, repair.Lease.ItemId, true, !failed, failed);
        }, token).ConfigureAwait(false);
    }

    private Task<DriveSourcePage> ReadPageAsync(SourceDiscoveryScope scope, SourceDiscoveryFolder folder, string? cursor, CancellationToken token) =>
        _accounts.ReadDriveAsync(scope.AccountId, (reader, revision, readToken) =>
        {
            if (revision != scope.AccountRevision) throw new StaleLibraryConfigurationException();
            return reader.ReadSourcePageAsync(folder.FolderId, folder.SharedDriveId, cursor, readToken);
        }, token);
    private static void ApplyMedia(ProcessorStateDatabase database, SourceDiscoveryScope scope, SourceDiscoveryFolder folder, IReadOnlyList<DriveSourceItem> items)
    {
        var facts = items.Where(item => item.Media is not null).Select(item =>
        {
            var media = item.Media!; var capture = DiscoveryTimestampReader.Read(media.CapturedTime); var modified = DiscoveryTimestampReader.Read(media.ModifiedTime);
            return new DiscoveredDriveAsset(item.Id, item.Version, media.Name, media.MimeType, media.Width, media.Height, capture.UtcMilliseconds, capture.Raw, modified.UtcMilliseconds, folder.Depth);
        }).ToArray();
        if (facts.Length != 0) new DiscoveredCatalogRepository(database).ApplyBatch(scope.SourceId, scope.SourceRevision, scope.AccountRevision, facts);
    }
    private Task<bool> ReturnAsync(StageLease lease) => _state.ExecuteAsync(database => { new DurableJobRepository(database).ReturnJoinedRead(lease); return true; }, CancellationToken.None);
    private async Task<SourceDiscoveryStep> FailAsync(StageLease lease, bool repair, string code, string message, bool retryable)
    {
        try
        {
            var failed = await StateAsync(database =>
            {
                var jobs = new DurableJobRepository(database); var policy = jobs.Summary(lease.JobId);
                if (policy.Paused || policy.IsCanceled) { jobs.ReturnJoinedRead(lease); return false; }
                jobs.Fail(lease, code, message, retryable); return true;
            }, CancellationToken.None).ConfigureAwait(false);
            return new(lease.JobId, lease.ItemId, repair, false, failed) { Stage = lease.Stage };
        }
        catch (InsufficientStorageBudgetException)
        { await ReturnAsync(lease).ConfigureAwait(false); throw; }
    }
    private Task<T> StateAsync<T>(Func<ProcessorStateDatabase, T> operation, CancellationToken token) => _state.ExecuteAsync(database =>
    {
        _reservation?.RecheckBeforeWrite(MetadataStepReservationBytes);
        return operation(database);
    }, token);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, bool faultOnError = false, bool reserveWrites = true)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                if (_failure is not null) throw new InvalidOperationException("Source discovery stopped after an internal failure. Inspect its diagnostic before restarting.", _failure);
            }
            using var reservation = _storage is null || !reserveWrites ? null : await _state.ExecuteAsync(_ => _storage.Reserve(MetadataStepReservationBytes), linked.Token).ConfigureAwait(false);
            _reservation = reservation;
            try { return await operation(linked.Token).ConfigureAwait(false); }
            finally { _reservation = null; }
        }
        catch (Exception error) when (faultOnError && error is not InsufficientStorageBudgetException && !(error is OperationCanceledException && linked.IsCancellationRequested))
        { lock (_gate) _failure ??= error; throw; }
        finally { _serial.Release(); }
    }
    public ValueTask DisposeAsync()
    { lock (_gate) { _stopping = true; return new(_disposal ??= StopAsync()); } }
    private async Task StopAsync()
    { await _stop.CancelAsync().ConfigureAwait(false); await _serial.WaitAsync().ConfigureAwait(false); _serial.Release(); _stop.Dispose(); }
}
