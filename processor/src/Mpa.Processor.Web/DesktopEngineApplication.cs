using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Web;

/// <summary>Browser adapter borrowing engine-lifetime accounts, read owners, state and work.</summary>
public sealed class DesktopEngineApplication(Guid libraryId, ProcessorStateCoordinator state, ProcessorAccountService accounts,
    IProcessorWorkControl work, ProcessorSourceRuntime? sources = null) : IDesktopWebApplication
{
    private readonly Guid _libraryId = libraryId != Guid.Empty ? libraryId : throw new ArgumentException("Library identity is required.", nameof(libraryId));
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly IProcessorWorkControl _work = work ?? throw new ArgumentNullException(nameof(work));
    private readonly ProcessorSourceRuntime? _sources = sources;
    private readonly DesktopCatalogReader _catalog = new(state);
    public Task<DesktopCatalogPage> CatalogAsync(string? accountId, string? position, bool newer, long? dateUtc, CancellationToken cancellationToken) =>
        _catalog.ReadAsync(accountId, position, newer, dateUtc, cancellationToken);
    public async Task<DesktopPreview?> CatalogPreviewAsync(Guid id, CancellationToken cancellationToken)
    {
        var captured = await _catalog.ResolveAsync(id, cancellationToken).ConfigureAwait(false);
        if (captured is null) return null;
        var preview = await _accounts.ReadDriveAsync<DesktopPreview?>(captured.AccountId, async (reader, _, token) =>
        {
            var result = await reader.ReadMediaPreviewAsync(captured.Facts.FileId, token).ConfigureAwait(false);
            return result is null ? null : new(result.Bytes, result.MimeType);
        }, cancellationToken).ConfigureAwait(false);
        // A source can be disabled or its media updated while the actual Google read
        // finishes. Never publish that late preview as the newly visible revision.
        var current = await _catalog.ResolveAsync(id, cancellationToken).ConfigureAwait(false);
        return current is not null && current.AccountId == captured.AccountId && current.Facts.FileId == captured.Facts.FileId &&
            current.Facts.DriveVersion == captured.Facts.DriveVersion ? preview : null;
    }
    public async Task<DesktopSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _accounts.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new(_libraryId, _work.State.ToString().ToLowerInvariant(), snapshot.Accounts.Select(account => new DesktopAccount(account.PermissionId,
            account.DisplayName, account.Connected, account.Revision)).ToArray(),
            snapshot.Pending.Select(pending => new DesktopPendingAccount(pending.Id, pending.ExpectedPermissionId)).ToArray(), snapshot.Cleanup.Count, _sources?.StorageAttention);
    }
    public Task PauseAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ChangeWorkAsync(resume: false); }
    public Task ResumeAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ChangeWorkAsync(resume: true); }
    private async Task ChangeWorkAsync(bool resume)
    {
        try { if (resume) await _work.ResumeAsync().ConfigureAwait(false); else await _work.PauseAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) when (_work.State is ProcessorWorkState.Stopping or ProcessorWorkState.Stopped)
        { throw new ProcessorWorkStateConflictException(); }
    }

    public async Task AccountAsync(DesktopAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Operation)
        {
            case DesktopAccountOperation.Connect:
                await _accounts.ConnectAsync(command.AccountId, new SystemAuthorizationBrowser(), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Check:
                if (await _accounts.CheckAsync(command.AccountId ?? throw new ArgumentException("Account identity required."), cancellationToken).ConfigureAwait(false) is null)
                    throw new HttpRequestException("Google Drive authorization requires reconnection.", null, System.Net.HttpStatusCode.Unauthorized);
                return;
            case DesktopAccountOperation.Disconnect:
                await _accounts.DisconnectAsync(command.AccountId ?? throw new ArgumentException("Account identity required."),
                    command.ExpectedRevision ?? throw new ArgumentException("Account revision required."), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Resume:
                await _accounts.ResumePendingAsync(command.PendingId ?? throw new ArgumentException("Pending identity required."), new SystemAuthorizationBrowser(), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Abandon:
                await _accounts.AbandonPendingAsync(command.PendingId ?? throw new ArgumentException("Pending identity required."), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Cleanup: break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        await _accounts.CleanCredentialsAsync(cancellationToken).ConfigureAwait(false);
        _sources?.NotifySourcesChanged();
    }

    public Task<LibrarySourceSnapshot> SourcesAsync(CancellationToken cancellationToken) => _state.ExecuteAsync(database => new LibrarySourceRepository(database).Snapshot(), cancellationToken);
    public Task<SourceDiscoveryActivityPage> ActivityAsync(Guid? cursor, CancellationToken cancellationToken) =>
        _state.ExecuteAsync(database => new SourceDiscoveryRepository(database).ReadActivity(cursor), cancellationToken);
    public Task<SourceDiscoverySchedule> RequestScanAsync(SourceScanRequest request, CancellationToken cancellationToken) =>
        SourceCommandAsync(sources => sources.RequestScanAsync(request, cancellationToken));
    public Task<int> RetrySourceAsync(SourceRetryRequest request, CancellationToken cancellationToken) =>
        SourceCommandAsync(sources => sources.RetryFailedAsync(request, cancellationToken));
    private async Task<T> SourceCommandAsync<T>(Func<ProcessorSourceRuntime, Task<T>> operation)
    {
        try { return await operation(_sources ?? throw new InvalidOperationException("The source engine is not configured.")).ConfigureAwait(false); }
        catch (ObjectDisposedException) when (_work.State is ProcessorWorkState.Stopping or ProcessorWorkState.Stopped)
        { throw new ProcessorWorkStateConflictException(); }
    }
    public Task<DesktopFolderLocation> FoldersAsync(string accountId, string folderId, string? cursor, CancellationToken cancellationToken) => _accounts.ReadDriveAsync(accountId, async (reader, _, token) =>
    {
        var folder = await reader.ReadFolderAsync(folderId, token).ConfigureAwait(false);
        if (!folder.CanListChildren) throw new HttpRequestException("Google does not allow listing this folder.", null, System.Net.HttpStatusCode.Forbidden);
        var page = await reader.ReadFolderPageAsync(folder.Id, cursor, token).ConfigureAwait(false);
        return new DesktopFolderLocation(new(folder.Id, folder.Name, folder.CanListChildren), page.Folders.Select(child => new DesktopFolder(child.Id, child.Name, child.CanListChildren)).ToArray(), page.NextPageToken, page.IncompleteSearch);
    }, cancellationToken);
    public async Task<LibrarySourceSnapshot> AddSourceAsync(DesktopAddSource request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var captured = await _accounts.ReadDriveAsync(request.AccountId, async (reader, revision, token) =>
        {
            var folder = await reader.ReadFolderAsync(request.FolderId, token).ConfigureAwait(false);
            if (!folder.CanListChildren) throw new HttpRequestException("Google does not allow listing this source folder.", null, System.Net.HttpStatusCode.Forbidden);
            return (Folder: folder, AccountRevision: revision);
        }, cancellationToken).ConfigureAwait(false);
        var result = await _state.ExecuteAsync(database => new LibrarySourceRepository(database).Add(Guid.NewGuid(), request.AccountId,
            captured.AccountRevision, captured.Folder.Id, captured.Folder.Name, request.IncludeDescendants, request.ExpectedRevision), cancellationToken).ConfigureAwait(false);
        _sources?.NotifySourcesChanged(); return result;
    }
    public async Task<LibrarySourceSnapshot> UpdateSourceAsync(DesktopUpdateSource request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _state.ExecuteAsync(database => new LibrarySourceRepository(database).Update(request.Id, request.Enabled, request.IncludeDescendants,
            request.ExpectedSourceRevision, request.ExpectedRevision), cancellationToken).ConfigureAwait(false);
        _sources?.NotifySourcesChanged(); return result;
    }
}
