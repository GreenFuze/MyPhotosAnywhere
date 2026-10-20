using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed record ProcessorAccountSnapshot(IReadOnlyList<DriveAccountRecord> Accounts,
    IReadOnlyList<PendingDriveConnection> Pending, IReadOnlyList<CredentialCleanupRecord> Cleanup);

/// <summary>Serial account setup owner shared by desktop/headless hosts. It borrows state/vault,
/// owns each active Google connection and joins it before acknowledging cancellation or Exit.</summary>
public sealed class ProcessorAccountService(ProcessorStateCoordinator state, ICredentialVault vault,
    IGoogleAccountConnectionFactory connections) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ICredentialVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private readonly IGoogleAccountConnectionFactory _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _stopping;
    private Task? _disposal;

    public Task<ProcessorAccountSnapshot> SnapshotAsync(CancellationToken cancellationToken) => ExecuteAsync(token =>
        _state.ExecuteAsync(database =>
        {
            var accounts = new DriveAccountRepository(database);
            return new ProcessorAccountSnapshot(accounts.Accounts(), accounts.PendingConnections(), accounts.PendingCleanup());
        }, token), cancellationToken);

    public Task<DriveAccountRecord> ConnectAsync(string? expectedPermissionId, IAuthorizationBrowser browser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return ExecuteAsync(async token =>
        {
            var pending = await _state.ExecuteAsync(database => new DriveAccountRepository(database).Begin(Guid.NewGuid(), expectedPermissionId), token).ConfigureAwait(false);
            return await FinishPendingAsync(pending, browser, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<DriveAccountRecord> ResumePendingAsync(Guid pendingId, IAuthorizationBrowser browser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return ExecuteAsync(async token =>
        {
            var pending = await _state.ExecuteAsync(database => new DriveAccountRepository(database).PendingConnections().SingleOrDefault(item => item.Id == pendingId)
                ?? throw new StaleDriveConnectionException(), token).ConfigureAwait(false);
            return await FinishPendingAsync(pending, browser, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<DriveAccountRecord> FinishPendingAsync(PendingDriveConnection pending, IAuthorizationBrowser browser, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create(pending.Id, pending.ExpectedPermissionId);
        var verified = await connection.AuthorizeAsync(browser, cancellationToken).ConfigureAwait(false);
        if (verified.ConnectionId != pending.Id) throw new InvalidDataException("Google connection owner returned a different attempt identity.");
        return await _state.ExecuteAsync(database => new DriveAccountRepository(database).Promote(pending.Id,
            verified.PermissionId, verified.DisplayName, verified.EmailAddress), cancellationToken).ConfigureAwait(false);
    }

    public Task<ConnectedDriveAccount?> CheckAsync(string permissionId, CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        var account = await _state.ExecuteAsync(database => new DriveAccountRepository(database).Accounts().SingleOrDefault(item => item.PermissionId == permissionId && item.Connected)
            ?? throw new InvalidOperationException("This account is disconnected. Reconnect it before checking access."), token).ConfigureAwait(false);
        await using var connection = _connections.Create(account.ConnectionId, account.PermissionId);
        var verified = await connection.CheckAsync(token).ConfigureAwait(false);
        if (verified is not null && (verified.ConnectionId != account.ConnectionId || verified.PermissionId != account.PermissionId))
            throw new InvalidDataException("Google account check returned a different captured connection/account identity.");
        return verified;
    }, cancellationToken);

    public Task DisconnectAsync(string permissionId, long expectedRevision, CancellationToken cancellationToken) => ExecuteAsync(token =>
        _state.ExecuteAsync(database => { new DriveAccountRepository(database).Disconnect(permissionId, expectedRevision); return true; }, token), cancellationToken);

    public Task AbandonPendingAsync(Guid id, CancellationToken cancellationToken) => ExecuteAsync(token =>
        _state.ExecuteAsync(database => { new DriveAccountRepository(database).Abandon(id); return true; }, token), cancellationToken);

    public Task<int> CleanCredentialsAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        var pending = await _state.ExecuteAsync(database => new DriveAccountRepository(database).PendingCleanup(), token).ConfigureAwait(false);
        var removed = 0;
        foreach (var record in pending)
        {
            await _state.ExecuteAsync(database => { new DriveAccountRepository(database).AssertCleanupAllowed(record.ConnectionId); return true; }, token).ConfigureAwait(false);
            var store = new GoogleCredentialDataStore(_vault, record.ConnectionId, () => token);
            await store.ClearAsync().ConfigureAwait(false);
            await _state.ExecuteAsync(database => { new DriveAccountRepository(database).ConfirmCleanup(record.ConnectionId); return true; }, token).ConfigureAwait(false);
            removed++;
        }
        return removed;
    }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
            return await operation(linked.Token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        _serial.Release();
        _stop.Dispose();
    }
}
