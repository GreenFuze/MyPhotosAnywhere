using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>One control surface for independently owned native and metadata work. Transitions
/// serialize and join both owners; a half-failed resume quiesces the owner that resumed.
/// It owns its control gate only: disposal joins admitted commands, while the composition
/// root remains responsible for stopping the borrowed processing owners.</summary>
public sealed class ProcessorWorkGroup(IProcessorWorkControl nativeWork, IProcessorWorkControl discovery) : IProcessorWorkControl, IAsyncDisposable
{
    private readonly IProcessorWorkControl _native = nativeWork ?? throw new ArgumentNullException(nameof(nativeWork));
    private readonly IProcessorWorkControl _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _gate = new();
    private Exception? _failure;
    private bool _pausing;
    private bool _stopping;
    private int _admitted;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposal;
    public Exception? Failure
    {
        get
        {
            lock (_gate) if (_failure is not null) return _failure;
            var native = _native.Failure; var metadata = _discovery.Failure;
            return native is not null && metadata is not null ? new AggregateException(native, metadata) : native ?? metadata;
        }
    }
    public ProcessorWorkState State
    {
        get
        {
            bool pausing;
            lock (_gate) { if (_failure is not null) return ProcessorWorkState.Faulted; pausing = _pausing; }
            var native = _native.State; var metadata = _discovery.State;
            if (native == ProcessorWorkState.Faulted || metadata == ProcessorWorkState.Faulted) return ProcessorWorkState.Faulted;
            if (native == ProcessorWorkState.Stopped && metadata == ProcessorWorkState.Stopped) return ProcessorWorkState.Stopped;
            if (native is ProcessorWorkState.Stopping or ProcessorWorkState.Stopped || metadata is ProcessorWorkState.Stopping or ProcessorWorkState.Stopped) return ProcessorWorkState.Stopping;
            if (pausing) return ProcessorWorkState.Pausing;
            if (native == ProcessorWorkState.Paused && metadata == ProcessorWorkState.Paused) return ProcessorWorkState.Paused;
            if (native is ProcessorWorkState.Paused or ProcessorWorkState.Pausing || metadata is ProcessorWorkState.Paused or ProcessorWorkState.Pausing) return ProcessorWorkState.Pausing;
            return native == ProcessorWorkState.Running || metadata == ProcessorWorkState.Running ? ProcessorWorkState.Running : ProcessorWorkState.Ready;
        }
    }
    public Task PauseAsync() => ExecuteTransitionAsync(async () =>
    {
            var state = State;
            if (state == ProcessorWorkState.Paused) return;
            if (state is not (ProcessorWorkState.Ready or ProcessorWorkState.Running)) throw new ProcessorWorkStateConflictException();
            lock (_gate) _pausing = true;
            try { await JoinAsync(_native.PauseAsync, _discovery.PauseAsync).ConfigureAwait(false); }
            catch (Exception error) { lock (_gate) _failure = error; throw; }
            finally { lock (_gate) _pausing = false; }
            if (State != ProcessorWorkState.Paused)
            {
                var error = new InvalidOperationException("Work owners acknowledged pause without reaching a joined paused state.", Failure);
                lock (_gate) _failure = error; throw error;
            }
    });
    public Task ResumeAsync() => ExecuteTransitionAsync(async () =>
    {
            var state = State;
            if (state is ProcessorWorkState.Ready or ProcessorWorkState.Running) return;
            if (state != ProcessorWorkState.Paused) throw new ProcessorWorkStateConflictException();
            try
            {
                await JoinAsync(_native.ResumeAsync, _discovery.ResumeAsync).ConfigureAwait(false);
                if (State is not (ProcessorWorkState.Ready or ProcessorWorkState.Running))
                    throw new InvalidOperationException("Work owners acknowledged resume without becoming runnable.", Failure);
            }
            catch (Exception error)
            {
                lock (_gate) _failure = error;
                try { await JoinAsync(_native.PauseAsync, _discovery.PauseAsync).ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    var combined = new AggregateException(error, cleanup); lock (_gate) _failure = combined; throw combined;
                }
                throw;
            }
    });
    private async Task ExecuteTransitionAsync(Func<Task> operation)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); _admitted = checked(_admitted + 1); }
        var entered = false;
        try
        {
            await _transition.WaitAsync().ConfigureAwait(false); entered = true;
            lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
            await operation().ConfigureAwait(false);
        }
        finally
        {
            if (entered) _transition.Release();
            lock (_gate) { _admitted--; if (_stopping && _admitted == 0) _drained.TrySetResult(); }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true; if (_admitted == 0) _drained.TrySetResult();
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync() { await _drained.Task.ConfigureAwait(false); _transition.Dispose(); }
    private static async Task InvokeAsync(Func<Task> operation) => await operation().ConfigureAwait(false);
    private static async Task JoinAsync(Func<Task> native, Func<Task> metadata)
    {
        var joined = Task.WhenAll(InvokeAsync(native), InvokeAsync(metadata));
        try { await joined.ConfigureAwait(false); }
        catch { if (joined.Exception is not null) throw joined.Exception; throw; }
    }
}
