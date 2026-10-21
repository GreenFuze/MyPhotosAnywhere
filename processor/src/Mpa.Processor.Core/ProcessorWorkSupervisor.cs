namespace Mpa.Processor.Core;

public enum ProcessorWorkState { Ready, Running, Pausing, Paused, Stopping, Stopped, Faulted }

/// <summary>One trusted Core adapter owns its snapshots, staging and reservations. Ownership
/// transfers only when Start accepts it. Dispose may run only after the exact worker exits.</summary>
public abstract class OwnedWorkerOperation : IAsyncDisposable
{
    public abstract WorkerLaunchSpec Launch { get; }
    public abstract Task ExecuteAsync(FramedWorkerProcess worker, CancellationToken cancellationToken);
    public abstract ValueTask DisposeAsync();
}

/// <summary>Single-heavy-worker owner. Durable repositories queue jobs; this owner never creates
/// an unbounded in-memory backlog. Pause/Exit stop admission and join before acknowledging.</summary>
public sealed class ProcessorWorkSupervisor : IProcessorWorkControl, IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _active;
    private Task? _transition;
    private CancellationTokenSource? _runCancellation;
    private ProcessorWorkState _state;
    private Exception? _failure;
    private RetainedOperation? _retained;
    private sealed record RetainedOperation(FramedWorkerProcess? Worker, OwnedWorkerOperation Operation);
    public ProcessorWorkState State { get { lock (_gate) return _state; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }

    public Task Start(OwnedWorkerOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (_state != ProcessorWorkState.Ready || _active is { IsCompleted: false })
                throw new InvalidOperationException("Processor is busy, paused, stopping, or faulted; retain durable queued work.");
            cancellationToken.ThrowIfCancellationRequested();
            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _runCancellation.Token;
            _state = ProcessorWorkState.Running;
            _active = Task.Run(() => ExecuteOwnedAsync(operation, token), CancellationToken.None);
            return _active;
        }
    }

    public Task PauseAsync()
    {
        lock (_gate)
        {
            if (_state == ProcessorWorkState.Pausing) return _transition!;
            if (_state == ProcessorWorkState.Paused) return Task.CompletedTask;
            if (_state is not (ProcessorWorkState.Ready or ProcessorWorkState.Running))
                throw new InvalidOperationException("Processor cannot pause in its current state.");
            _state = ProcessorWorkState.Pausing;
            _transition = Task.Run(() => GuardTransitionAsync(stopping: false), CancellationToken.None);
            return _transition;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != ProcessorWorkState.Paused) throw new InvalidOperationException("Only a completely paused Processor can resume.");
            _state = ProcessorWorkState.Ready;
        }
    }

    public Task ResumeAsync() { Resume(); return Task.CompletedTask; }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_state == ProcessorWorkState.Stopped) return Task.CompletedTask;
            if (_state == ProcessorWorkState.Stopping) return _transition!;
            var previous = _transition;
            var wasPausing = _state == ProcessorWorkState.Pausing;
            _state = ProcessorWorkState.Stopping;
            _transition = Task.Run(async () =>
            {
                try
                {
                    if (wasPausing && previous is not null)
                        try { await previous.ConfigureAwait(false); }
                        catch (Exception error) { lock (_gate) _failure = error; } // Still join retained ownership below.
                    await GuardTransitionAsync(stopping: true).ConfigureAwait(false);
                }
                catch (Exception error) { lock (_gate) { _failure = error; _state = ProcessorWorkState.Faulted; } throw; }
            }, CancellationToken.None);
            return _transition;
        }
    }

    private async Task GuardTransitionAsync(bool stopping)
    {
        try { await TransitionAsync(stopping).ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) { _failure = error; _state = ProcessorWorkState.Faulted; } throw; }
    }

    private async Task TransitionAsync(bool stopping)
    {
        Task? active; CancellationTokenSource? cancellation;
        lock (_gate) { active = _active; cancellation = _runCancellation; }
        if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
        if (active is not null)
        {
            try { await active.WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { /* Joined canceled work. */ }
            catch (TimeoutException)
            {
                lock (_gate) { _failure = new TimeoutException("Worker exit/resource release is not confirmed; ownership is retained. Retry stop and do not claim clean Exit."); _state = ProcessorWorkState.Faulted; }
                throw;
            }
            catch (Exception error) { lock (_gate) _failure = error; } // Joined failure is visible; never resumed automatically.
        }
        RetainedOperation? retained;
        lock (_gate) retained = _retained;
        if (retained is not null)
        {
            if (retained.Worker is not null)
            {
                try { await retained.Worker.JoinAfterFailedStopAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false); }
                catch (Exception error) when (retained.Worker.ExitConfirmed) { lock (_gate) _failure = error; }
                if (!retained.Worker.ExitConfirmed) throw new InvalidOperationException("Retained worker exit is still unconfirmed.");
            }
            await retained.Operation.DisposeAsync().ConfigureAwait(false);
            lock (_gate) _retained = null;
        }
        lock (_gate)
        {
            if (stopping)
            {
                _runCancellation?.Dispose(); _runCancellation = null; _state = ProcessorWorkState.Stopped;
            }
            else if (_state != ProcessorWorkState.Stopping)
                _state = _failure is null ? ProcessorWorkState.Paused : ProcessorWorkState.Faulted;
        }
    }

    private async Task ExecuteOwnedAsync(OwnedWorkerOperation operation, CancellationToken token)
    {
        FramedWorkerProcess? worker = null;
        Exception? failure = null;
        try
        {
            try { worker = await FramedWorkerProcess.StartAsync(operation.Launch, token).ConfigureAwait(false); }
            catch (WorkerStartCleanupException error) { worker = error.RetainedWorker; throw; }
            await operation.ExecuteAsync(worker, token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (worker is not null)
            {
                try { await worker.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    failure = Combine(failure, error);
                    // This join intentionally retains the operation if OS exit cannot yet be confirmed.
                    try { await worker.JoinAfterFailedStopAsync().ConfigureAwait(false); }
                    catch (Exception joinError) { failure = Combine(failure, joinError); }
                }
            }
            if (worker is not null && !worker.ExitConfirmed)
            {
                lock (_gate) _retained = new(worker, operation);
                failure = Combine(failure, new InvalidOperationException("Worker exit is unconfirmed; operation resources remain owned."));
            }
            else
            {
                try { await operation.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    lock (_gate) _retained = new(worker, operation);
                    failure = Combine(failure, error);
                }
            }
            lock (_gate)
            {
                if (failure is not null && !(failure is OperationCanceledException && token.IsCancellationRequested))
                { _failure = failure; if (_state == ProcessorWorkState.Running) _state = ProcessorWorkState.Faulted; }
                else if (_state == ProcessorWorkState.Running) _state = ProcessorWorkState.Ready;
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception Combine(Exception? previous, Exception next) => previous is null ? next : new AggregateException("Worker execution/cleanup failed.", previous, next);
    public ValueTask DisposeAsync() => new(StopAsync());
}
