using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Web;

/// <summary>Browser adapter borrowing engine-lifetime accounts, read owners, state and work.</summary>
public sealed class DesktopEngineApplication(Guid libraryId, ProcessorStateCoordinator state, ProcessorAccountService accounts,
    ProcessorWorkSupervisor work) : IDesktopWebApplication
{
    private readonly Guid _libraryId = libraryId != Guid.Empty ? libraryId : throw new ArgumentException("Library identity is required.", nameof(libraryId));
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly ProcessorWorkSupervisor _work = work ?? throw new ArgumentNullException(nameof(work));
    public async Task<DesktopSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _accounts.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new(_libraryId, _work.State.ToString().ToLowerInvariant(), snapshot.Accounts.Select(account => new DesktopAccount(account.PermissionId,
            account.DisplayName, account.Connected, account.Revision)).ToArray(),
            snapshot.Pending.Select(pending => new DesktopPendingAccount(pending.Id, pending.ExpectedPermissionId)).ToArray(), snapshot.Cleanup.Count);
    }
    public Task<DesktopPhotoPage> PhotosAsync(string accountId, string? cursor, CancellationToken cancellationToken) => _accounts.ReadDriveAsync(accountId, async (reader, _, token) =>
    {
        var page = await reader.ReadMediaPageAsync(cursor, token).ConfigureAwait(false);
        return new DesktopPhotoPage(page.Files.Select(file => new DesktopPhoto(file.Id, file.Name, file.MimeType, file.Width, file.Height,
            file.CapturedTime, file.ModifiedTime)).ToArray(), page.NextPageToken, page.IncompleteSearch);
    }, cancellationToken);
    public Task<DesktopPreview?> PreviewAsync(string accountId, string fileId, CancellationToken cancellationToken) => _accounts.ReadDriveAsync<DesktopPreview?>(accountId, async (reader, _, token) =>
    {
        var preview = await reader.ReadMediaPreviewAsync(fileId, token).ConfigureAwait(false);
        return preview is null ? null : new(preview.Bytes, preview.MimeType);
    }, cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return _work.PauseAsync(); }
    public Task ResumeAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); if (_work.State != ProcessorWorkState.Ready) _work.Resume(); return Task.CompletedTask; }

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
    }

    public Task<LibrarySourceSnapshot> SourcesAsync(CancellationToken cancellationToken) => _state.ExecuteAsync(database => new LibrarySourceRepository(database).Snapshot(), cancellationToken);
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
        return await _state.ExecuteAsync(database => new LibrarySourceRepository(database).Add(Guid.NewGuid(), request.AccountId,
            captured.AccountRevision, captured.Folder.Id, captured.Folder.Name, request.IncludeDescendants, request.ExpectedRevision), cancellationToken).ConfigureAwait(false);
    }
    public Task<LibrarySourceSnapshot> UpdateSourceAsync(DesktopUpdateSource request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _state.ExecuteAsync(database => new LibrarySourceRepository(database).Update(request.Id, request.Enabled, request.IncludeDescendants,
            request.ExpectedSourceRevision, request.ExpectedRevision), cancellationToken);
    }
}
