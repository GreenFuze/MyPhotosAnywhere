using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Web;

/// <summary>Browser adapter borrowing engine-lifetime accounts, read owners, state and work.</summary>
public sealed class DesktopEngineApplication(Guid libraryId, ProcessorStateCoordinator state, ProcessorAccountService accounts,
    IProcessorWorkControl work, ProcessorSourceRuntime? sources = null, CatalogViewerSessions? viewers = null, LibraryWorkspaceSetup? setup = null,
    InitialLibraryPublicationRuntime? publication = null) : IDesktopWebApplication, IDesktopLibraryCatalogApplication
{
    private readonly Guid _libraryId = libraryId != Guid.Empty ? libraryId : throw new ArgumentException("Library identity is required.", nameof(libraryId));
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly IProcessorWorkControl _work = work ?? throw new ArgumentNullException(nameof(work));
    private readonly ProcessorSourceRuntime? _sources = sources;
    private readonly DesktopCatalogReader _catalog = new(state);
    private readonly CatalogViewerSessions? _viewers = viewers;
    private readonly LibraryWorkspaceSetup? _setup = setup;
    private readonly InitialLibraryPublicationRuntime? _publication = publication;
    private InitialLibraryPublicationRuntime Publication => _publication ?? throw new InvalidOperationException("The engine library publication owner has not been configured.");
    private LibraryWorkspaceSetup Setup => _setup ?? throw new InvalidOperationException("The native library setup owner has not been configured.");
    public Task<LibraryControllerPin?> LibraryAsync(CancellationToken cancellationToken) => Setup.CurrentAsync(cancellationToken);
    public Task<LibraryWorkspaceInspection> InspectWorkspaceAsync(string accountId, string folderId, CancellationToken cancellationToken) => Setup.InspectAsync(accountId, folderId, cancellationToken);
    public async Task<LibraryControllerPin> ApproveWorkspaceAsync(ApproveLibraryWorkspace request, CancellationToken cancellationToken)
    {
        var owner = Publication;
        var controller = await Setup.ApproveAsync(request, cancellationToken).ConfigureAwait(false);
        // Once the human approval actually commits, the engine owns its durable wake-up.
        // A disconnected browser must not cancel it between commit and job admission.
        owner.Request(CancellationToken.None); return controller;
    }
    public Task<DesktopLibraryPublication> LibraryPublicationAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(PublicationActivity(Publication.Activity)); }
    public Task<DesktopLibraryPublication> RequestLibraryPublicationAsync(CancellationToken cancellationToken) => Task.FromResult(PublicationActivity(Publication.Request(cancellationToken)));
    private static DesktopLibraryPublication PublicationActivity(InitialLibraryPublicationActivity activity) => new(activity.Stage.ToString(), activity.Detail, activity.CanRetry);
    private CatalogViewerSessions Viewers => _viewers ?? throw new InvalidOperationException("The viewer session owner has not been configured.");
    public async Task<DesktopViewerWindow> OpenViewerAsync(Guid browser, CatalogViewerRequest request, CancellationToken cancellationToken) =>
        await ResolveWindowAsync(await ViewerCommandAsync(() => Viewers.OpenAsync(browser, request, cancellationToken)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    public async Task<DesktopViewerWindow> ReadViewerAsync(Guid browser, Guid id, long position, CancellationToken cancellationToken) =>
        await ResolveWindowAsync(await ViewerCommandAsync(() => Viewers.ReadAsync(browser, id, position, cancellationToken)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    public Task CloseViewerAsync(Guid browser, Guid id, CancellationToken cancellationToken) => Viewers.CloseAsync(browser, id, cancellationToken);
    public Task CloseBrowserViewersAsync(Guid browser, CancellationToken cancellationToken) => Viewers.CloseBrowserAsync(browser, cancellationToken);
    private async Task<T> ViewerCommandAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (ObjectDisposedException) when (_work.State is ProcessorWorkState.Stopping or ProcessorWorkState.Stopped or ProcessorWorkState.Faulted)
        { throw new CatalogViewerUnavailableException(); }
    }
    private Task<DesktopViewerWindow> ResolveWindowAsync(CatalogViewerWindow window,CancellationToken cancellationToken)=>window.StageId is null?
        ResolveDiscoveredWindowAsync(window,cancellationToken):new DesktopLibraryViewerReader(_publication?.CurrentCanonicalCatalog ?? throw new DesktopLibraryCatalogUnavailableException()).ReadAsync(window,cancellationToken);
    private Task<DesktopViewerWindow> ResolveDiscoveredWindowAsync(CatalogViewerWindow window, CancellationToken cancellationToken) => _state.ExecuteAsync(database =>
    {
        var catalog = new DiscoveredCatalogRepository(database);
        return new DesktopViewerWindow(window.Id, window.Count, window.Position, window.Entries.Select(entry =>
        {
            var photo = catalog.ResolveVisible(entry.AssetId); return new DesktopViewerEntry(entry.Position, entry.AssetId, photo is null ? null : DesktopCatalogReader.Photo(photo));
        }).ToArray());
    }, cancellationToken);
    public Task<DesktopLibraryCatalogPage> LibraryCatalogAsync(string? accountId,string? position,bool newer,long? dateUtc,CancellationToken cancellationToken)
    {
        var current=_publication?.CurrentCanonicalCatalog ?? throw new DesktopLibraryCatalogUnavailableException();
        return new DesktopLibraryCatalogReader(current).ReadAsync(accountId,position,newer,dateUtc,cancellationToken);
    }
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
        DriveAccountRecord? connected = null;
        switch (command.Operation)
        {
            case DesktopAccountOperation.Connect:
                connected = await _accounts.ConnectAsync(command.AccountId, new SystemAuthorizationBrowser(), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Check:
                if (await _accounts.CheckAsync(command.AccountId ?? throw new ArgumentException("Account identity required."), cancellationToken).ConfigureAwait(false) is null)
                    throw new HttpRequestException("Google Drive authorization requires reconnection.", null, System.Net.HttpStatusCode.Unauthorized);
                return;
            case DesktopAccountOperation.Disconnect:
                await _accounts.DisconnectAsync(command.AccountId ?? throw new ArgumentException("Account identity required."),
                    command.ExpectedRevision ?? throw new ArgumentException("Account revision required."), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Resume:
                connected = await _accounts.ResumePendingAsync(command.PendingId ?? throw new ArgumentException("Pending identity required."), new SystemAuthorizationBrowser(), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Abandon:
                await _accounts.AbandonPendingAsync(command.PendingId ?? throw new ArgumentException("Pending identity required."), cancellationToken).ConfigureAwait(false); break;
            case DesktopAccountOperation.Cleanup: break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        if (connected is not null)
        {
            // Promotion is durable. Neither browser cancellation nor credential
            // cleanup may lose its engine-owned wake-up after successful consent.
            _publication?.NotifyAccountConnected(connected);
            _sources?.NotifySourcesChanged();
        }
        await _accounts.CleanCredentialsAsync(cancellationToken).ConfigureAwait(false);
        if (connected is null) _sources?.NotifySourcesChanged();
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
        var boundary = await _state.ExecuteAsync(ManagedWorkspaceBoundary.Capture, cancellationToken).ConfigureAwait(false);
        var captured = await _accounts.ReadDriveAsync(request.AccountId, async (reader, revision, token) =>
        {
            var folder = await reader.ReadFolderAsync(request.FolderId, token).ConfigureAwait(false);
            if (!folder.CanListChildren) throw new HttpRequestException("Google does not allow listing this source folder.", null, System.Net.HttpStatusCode.Forbidden);
            await new SourceWorkspaceVerifier(reader, boundary).AssertOutsideAsync(folder, token).ConfigureAwait(false);
            return (Folder: folder, AccountRevision: revision);
        }, cancellationToken).ConfigureAwait(false);
        var result = await _state.ExecuteAsync(database =>
        {
            if (ManagedWorkspaceBoundary.Capture(database).RootFolderId != boundary.RootFolderId) throw new StaleLibraryConfigurationException();
            return new LibrarySourceRepository(database).Add(Guid.NewGuid(), request.AccountId,
                captured.AccountRevision, captured.Folder.Id, captured.Folder.Name, request.IncludeDescendants, request.ExpectedRevision);
        }, cancellationToken).ConfigureAwait(false);
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
