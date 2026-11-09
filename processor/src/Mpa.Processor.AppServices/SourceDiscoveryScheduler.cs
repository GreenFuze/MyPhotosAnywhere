using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Browser-independent metadata dispatcher. It borrows state/runner, owns its loop
/// and cancellation epochs, and acknowledges pause only after the actual read has joined.
/// The composition root must stop this owner before disposing runner/accounts/state.</summary>
public sealed class SourceDiscoveryScheduler : IProcessorWorkControl, IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly SourceDiscoveryRunner _runner;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _epoch = new();
    private TaskCompletionSource _pauseAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _loop;
    private Task? _disposal;
    private bool _paused;
    private bool _active;
    private bool _reconcile = true;
    private bool _stopping;
    private bool _stopped;
    private Exception? _failure;
    private StorageBudgetStatus? _storageAttention;
    public Task Completion => _loop;
    public Exception? Failure { get { lock (_gate) return _failure; } }
    public StorageBudgetStatus? StorageAttention { get { lock (_gate) return _storageAttention; } }
    public bool ExitConfirmed { get { lock (_gate) return _stopped; } }
    public ProcessorWorkState State
    {
        get
        {
            lock (_gate)
                return _failure is not null ? ProcessorWorkState.Faulted : _stopped ? ProcessorWorkState.Stopped : _stopping ? ProcessorWorkState.Stopping :
                    _paused ? _pauseAcknowledged.Task.IsCompleted ? ProcessorWorkState.Paused : ProcessorWorkState.Pausing : _active ? ProcessorWorkState.Running : ProcessorWorkState.Ready;
        }
    }
    public SourceDiscoveryScheduler(ProcessorStateCoordinator state, SourceDiscoveryRunner runner, bool initiallyPaused = false, TimeSpan? pollInterval = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state)); _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(15);
        if (_pollInterval < TimeSpan.FromSeconds(1) || _pollInterval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _paused = initiallyPaused;
        _loop = Task.Run(RunAsync, CancellationToken.None);
    }
    public void NotifySourcesChanged()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this); ThrowIfFailed(); _reconcile = true; Pulse();
        }
    }
    public async Task PauseAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            Task acknowledgement; CancellationTokenSource epoch;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this); ThrowIfFailed();
                _paused = true; epoch = _epoch; acknowledgement = _pauseAcknowledged.Task; Pulse();
            }
            // This epoch remains owned until Resume, after this acknowledgement. Its CTS
            // cannot be disposed by a completed step while cancellation is being requested.
            await epoch.CancelAsync().ConfigureAwait(false);
            await acknowledgement.ConfigureAwait(false);
            lock (_gate) ThrowIfFailed();
        }
        finally { _transition.Release(); }
    }
    public async Task ResumeAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this); ThrowIfFailed();
                if (!_paused || !_pauseAcknowledged.Task.IsCompleted) throw new InvalidOperationException("Source discovery must finish pausing before resume.");
                _epoch.Dispose(); _epoch = new(); _pauseAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously); _paused = false; Pulse();
            }
        }
        finally { _transition.Release(); }
    }
    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                bool paused; bool reconcile; CancellationToken epoch;
                lock (_gate)
                {
                    paused = _paused; reconcile = _reconcile;
                    if (!paused) _reconcile = false;
                    epoch = _epoch.Token;
                    if (paused) _pauseAcknowledged.TrySetResult();
                }
                if (paused) { await _wake.WaitAsync(_stop.Token).ConfigureAwait(false); continue; }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, epoch);
                try
                {
                    lock (_gate) _active = true;
                    if (reconcile)
                    {
                        var sources = await _state.ExecuteAsync(database => new LibrarySourceRepository(database).Snapshot().Sources, linked.Token).ConfigureAwait(false);
                        foreach (var source in sources.Where(source => source.Enabled && source.AccountConnected))
                        {
                            try { await _runner.EnsureScheduledAsync(source.Id, source.Revision, linked.Token).ConfigureAwait(false); }
                            catch (StaleLibraryConfigurationException) { lock (_gate) _reconcile = true; } // Re-read changed bindings; do not retarget a captured request.
                        }
                    }
                    var step = await _runner.RunNextAsync(linked.Token).ConfigureAwait(false);
                    lock (_gate) { _active = false; _storageAttention = null; }
                    if (step is null)
                    {
                        if (!await _wake.WaitAsync(_pollInterval, linked.Token).ConfigureAwait(false)) lock (_gate) _reconcile = true;
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                { lock (_gate) _reconcile = true; /* Resume rechecks any partially scheduled source list. */ }
                catch (InsufficientStorageBudgetException error)
                {
                    lock (_gate) { _storageAttention = error.Status; _reconcile = true; _active = false; }
                    try { await _wake.WaitAsync(_pollInterval, linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { /* Pause/Exit joins this bounded wait. */ }
                }
                finally { lock (_gate) _active = false; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Joined normal Exit. */ }
        catch (Exception error) { lock (_gate) _failure = error; throw; }
        finally { lock (_gate) { _active = false; _pauseAcknowledged.TrySetResult(); } }
    }
    private void ThrowIfFailed()
    {
        if (_failure is not null) throw new InvalidOperationException("Source discovery stopped after an internal failure. Inspect its diagnostic before restarting.", _failure);
    }
    private void Pulse() { if (_wake.CurrentCount == 0) _wake.Release(); }
    public ValueTask DisposeAsync()
    {
        lock (_gate) { _stopping = true; return new(_disposal ??= StopAsync()); }
    }
    private async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        lock (_gate) Pulse();
        try { await _loop.ConfigureAwait(false); }
        finally
        {
            await _transition.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate) { _epoch.Dispose(); _wake.Dispose(); _stop.Dispose(); _stopped = true; }
            }
            finally { _transition.Release(); }
        }
    }
}
