using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Web;

/// <summary>Application adapter shared by browser sessions. It owns bounded read connections,
/// borrows durable state/accounts/work, and joins every active Google call before Exit.</summary>
public sealed class DesktopEngineApplication(Guid libraryId, ProcessorStateCoordinator state, ProcessorAccountService accounts,
    DesktopOAuthConfiguration configuration, ICredentialVault vault, ProcessorWorkSupervisor work) : IDesktopWebApplication, IAsyncDisposable
{
    private readonly Guid _libraryId = libraryId != Guid.Empty ? libraryId : throw new ArgumentException("Library identity is required.", nameof(libraryId));
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly DesktopOAuthConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ICredentialVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private readonly ProcessorWorkSupervisor _work = work ?? throw new ArgumentNullException(nameof(work));
    private readonly Dictionary<string, (long Revision, GoogleAccountConnection Connection)> _readers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _disposal;
    private bool _stopping;

    public async Task<DesktopSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _accounts.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new(_libraryId, _work.State.ToString().ToLowerInvariant(), snapshot.Accounts.Select(account => new DesktopAccount(account.PermissionId,
            account.DisplayName, account.Connected, account.Revision)).ToArray(),
            snapshot.Pending.Select(pending => new DesktopPendingAccount(pending.Id, pending.ExpectedPermissionId)).ToArray(), snapshot.Cleanup.Count);
    }
    public Task<DesktopPhotoPage> PhotosAsync(string accountId, string? cursor, CancellationToken cancellationToken) => ReadAsync(accountId, async (reader, token) =>
    {
        var page = await reader.ReadMediaPageAsync(cursor, token).ConfigureAwait(false);
        return new DesktopPhotoPage(page.Files.Select(file => new DesktopPhoto(file.Id, file.Name, file.MimeType, file.Width, file.Height,
            file.CapturedTime, file.ModifiedTime)).ToArray(), page.NextPageToken, page.IncompleteSearch);
    }, cancellationToken);
    public Task<DesktopPreview?> PreviewAsync(string accountId, string fileId, CancellationToken cancellationToken) => ReadAsync<DesktopPreview?>(accountId, async (reader, token) =>
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

    private async Task<T> ReadAsync<T>(string accountId, Func<GoogleAccountConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
            var account = await _state.ExecuteAsync(database => new DriveAccountRepository(database).Accounts().SingleOrDefault(account => account.PermissionId == accountId && account.Connected), linked.Token).ConfigureAwait(false)
                ?? throw new HttpRequestException("The selected Google account is disconnected.", null, System.Net.HttpStatusCode.Unauthorized);
            if (!_readers.TryGetValue(accountId, out var current) || current.Revision != account.Revision)
            {
                if (current.Connection is not null) { await current.Connection.DisposeAsync().ConfigureAwait(false); _readers.Remove(accountId); }
                current = (account.Revision, new GoogleAccountConnection(_configuration, _vault, account.ConnectionId, account.PermissionId));
                _readers.Add(accountId, current);
            }
            return await operation(current.Connection, linked.Token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) { _stopping = true; return new(_disposal ??= StopAsync()); }
    }
    private async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            var owned = Task.WhenAll(_readers.Values.Select(reader => reader.Connection.DisposeAsync().AsTask()));
            await owned.ConfigureAwait(false); _readers.Clear();
        }
        finally { _serial.Release(); _stop.Dispose(); }
    }
}
