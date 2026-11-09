using System.Net;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

/// <summary>One engine-wide owner for foreground and background Google readers. Callbacks
/// cannot outlive their serial lease. Retirement joins any refresh/save before credential
/// deletion, and disconnected/replaced connections cannot be admitted again.</summary>
internal sealed class ProcessorDriveReaders(ProcessorStateCoordinator state, IGoogleLibraryConnectionFactory connections) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly IGoogleLibraryConnectionFactory _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly Dictionary<string, (Guid Id, long Revision, IGoogleLibraryConnection Connection)> _readers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _stopping;
    private Task? _disposal;

    public Task<T> ReadAsync<T>(string accountId, Func<IGoogleLibraryConnection, long, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync(async token =>
        {
            var account = await _state.ExecuteAsync(database => new DriveAccountRepository(database).Accounts()
                .SingleOrDefault(account => account.PermissionId == accountId && account.Connected), token).ConfigureAwait(false)
                ?? throw new HttpRequestException("The selected Google account is disconnected.", null, HttpStatusCode.Unauthorized);
            if (!_readers.TryGetValue(accountId, out var current) || current.Id != account.ConnectionId || current.Revision != account.Revision)
            {
                if (current.Connection is not null)
                {
                    await current.Connection.DisposeAsync().ConfigureAwait(false);
                    _readers.Remove(accountId);
                }
                current = (account.ConnectionId, account.Revision, _connections.Create(account.ConnectionId, account.PermissionId));
                _readers.Add(accountId, current);
            }
            return await operation(current.Connection, account.Revision, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task RetireAsync(Guid connectionId, CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        // Capture/refresh and retirement use this same lease. After this check, account
        // promotion cannot reuse the retired UUID, even if another account command starts.
        await _state.ExecuteAsync(database => { new DriveAccountRepository(database).AssertCleanupAllowed(connectionId); return true; }, token).ConfigureAwait(false);
        var entry = _readers.SingleOrDefault(reader => reader.Value.Id == connectionId);
        if (entry.Value.Connection is not null)
        {
            await entry.Value.Connection.DisposeAsync().ConfigureAwait(false);
            _readers.Remove(entry.Key); // Only surrender ownership after joined disposal.
        }
        return true;
    }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
            var result = await operation(linked.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_stopping) throw new OperationCanceledException("The account reader owner stopped before admitting this result.", _stop.Token);
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
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
            await Task.WhenAll(_readers.Values.Select(reader => reader.Connection.DisposeAsync().AsTask())).ConfigureAwait(false);
            _readers.Clear();
        }
        finally { _serial.Release(); _stop.Dispose(); }
    }
}
