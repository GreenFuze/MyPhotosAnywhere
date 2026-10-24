namespace Mpa.Processor.Core;

/// <summary>One shared serial background owner for SQLite repository calls. Delegates are
/// synchronous bounded state work only; never network, native processing or user interaction.
/// The database is borrowed and must outlive this coordinator's joined disposal.</summary>
public sealed class ProcessorStateCoordinator(ProcessorStateDatabase database) : IAsyncDisposable
{
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private bool _stopping;
    private Task? _disposal;

    public async Task<T> ExecuteAsync<T>(Func<ProcessorStateDatabase, T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
            cancellationToken.ThrowIfCancellationRequested();
            // Once started, join SQLite work even if its caller cancels. The result may have
            // committed and must not be disguised as a rollback by a timeout wrapper.
            return await Task.Run(() => operation(_database), CancellationToken.None).ConfigureAwait(false);
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
        await _serial.WaitAsync().ConfigureAwait(false);
        _serial.Release();
    }
}
