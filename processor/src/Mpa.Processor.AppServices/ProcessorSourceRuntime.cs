using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed class ProcessorSourceStartException(Exception cause, ProcessorSourceRuntime retainedOwner)
    : Exception("Source startup failed and cleanup is not confirmed; its owner is retained.", cause)
{ public ProcessorSourceRuntime RetainedOwner { get; } = retainedOwner; }

/// <summary>Owns source scheduling, its runner and the shared control gate; borrows accounts,
/// SQLite and native work. Prepare paused before exposing listeners, activate once the host
/// is ready, and join this owner before releasing any borrowed account or state resource.</summary>
public sealed class ProcessorSourceRuntime : IAsyncDisposable
{
    private readonly SourceDiscoveryRunner _runner;
    private readonly ProcessorWorkSupervisor _native;
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _storage;
    private readonly object _gate = new();
    private SourceDiscoveryScheduler? _scheduler;
    private ProcessorWorkGroup? _work;
    private Task? _monitor;
    private Task? _quiescence;
    private Task? _disposal;
    private Exception? _failure;
    private bool _activated;
    private bool _stopping;
    private bool _exitConfirmed;
    private int _commands;
    private readonly TaskCompletionSource _commandsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<Exception>? Failed;
    public event Action? CatalogChanged;
    public ProcessorWorkGroup Work => _work ?? throw new InvalidOperationException("Source processing controls have not been prepared.");
    public StorageBudgetStatus? StorageAttention => _scheduler?.StorageAttention;
    public Exception? Failure { get { lock (_gate) return _failure ?? _work?.Failure ?? _runner.Failure; } }
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }

    private ProcessorSourceRuntime(ProcessorStateCoordinator state, ProcessorAccountService accounts, ProcessorWorkSupervisor native, StorageBudgetManager storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _runner = new(state, accounts, storage); _runner.CatalogChanged += NotifyCatalogChanged;
        _native = native ?? throw new ArgumentNullException(nameof(native)); _state = state; _storage = storage;
    }
    public static async Task<ProcessorSourceRuntime> CreatePausedAsync(ProcessorStateCoordinator state, ProcessorAccountService accounts,
        ProcessorWorkSupervisor native, StorageBudgetManager storage, CancellationToken cancellationToken)
    {
        var owner = new ProcessorSourceRuntime(state, accounts, native, storage);
        try
        {
            // The composition root already owns the exclusive state lease. No new scanner
            // exists until interrupted metadata attempts have been reconciled.
            await owner._runner.RecoverInterruptedReadsAfterExclusiveRestartAsync(cancellationToken).ConfigureAwait(false);
            owner._scheduler = new(state, owner._runner, initiallyPaused: true);
            await owner._scheduler.PauseAsync().ConfigureAwait(false);
            owner._work = new(native, owner._scheduler);
            owner._monitor = owner.ObserveAsync();
            cancellationToken.ThrowIfCancellationRequested(); return owner;
        }
        catch (Exception error)
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new ProcessorSourceStartException(new AggregateException(error, cleanup), owner); }
            throw;
        }
    }

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_activated) throw new InvalidOperationException("Source processing can only be activated once.");
            cancellationToken.ThrowIfCancellationRequested(); _activated = true;
        }
        if (_native.State == ProcessorWorkState.Paused) return;
        if (_native.State is not (ProcessorWorkState.Ready or ProcessorWorkState.Running)) throw new ProcessorWorkStateConflictException();
        await _scheduler!.ResumeAsync().ConfigureAwait(false);
    }
    public void NotifySourcesChanged()
    {
        // A policy can finish committing while Exit stops background admission. Its
        // durable outbox remains valid for restart; a stopped dispatcher needs no wake.
        lock (_gate) { if (!_stopping && Failure is null) _scheduler!.NotifySourcesChanged(); }
    }
    private void NotifyCatalogChanged()
    {
        Action? notification;
        lock (_gate) { if (_stopping) return; notification = CatalogChanged; }
        notification?.Invoke();
    }
    public Task<SourceDiscoverySchedule> RequestScanAsync(SourceScanRequest request, CancellationToken cancellationToken) =>
        CommandAsync(database => new SourceDiscoveryRepository(database).RequestScan(request), cancellationToken);
    public Task<int> RetryFailedAsync(SourceRetryRequest request, CancellationToken cancellationToken) =>
        CommandAsync(database => new SourceDiscoveryRepository(database).RetryFailed(request), cancellationToken);
    private async Task<T> CommandAsync<T>(Func<ProcessorStateDatabase, T> operation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (Failure is not null) throw new ProcessorWorkStateConflictException();
            _commands = checked(_commands + 1);
        }
        try
        {
            var result = await _state.ExecuteAsync(database =>
            {
                using var reservation = _storage.Reserve(SourceDiscoveryRunner.MetadataStepReservationBytes);
                reservation.RecheckBeforeWrite(reservation.Bytes); return operation(database);
            }, cancellationToken).ConfigureAwait(false);
            lock (_gate) { if (!_stopping && Failure is null) _scheduler!.NotifySourcesChanged(); }
            return result;
        }
        finally { lock (_gate) { _commands--; if (_stopping && _commands == 0) _commandsDrained.TrySetResult(); } }
    }
    private async Task ObserveAsync()
    {
        try { await _scheduler!.Completion.ConfigureAwait(false); }
        catch (Exception error) { ReportFailure(error); }
    }
    public void ReportFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            _failure ??= error;
            _quiescence ??= Task.Run(QuiesceAsync, CancellationToken.None);
        }
        Failed?.Invoke(error);
    }
    private async Task QuiesceAsync()
    {
        // A faulted group cannot accept ordinary pause. Stop the independent owners
        // directly; keep every failure and never resume another branch after an invariant.
        var metadata = StopSchedulerAsync();
        try { await _native.StopAsync().ConfigureAwait(false); }
        catch (Exception error) { Retain(error); }
        await metadata.ConfigureAwait(false);
    }
    private async Task StopSchedulerAsync()
    {
        if (_scheduler is null) return;
        try { await _scheduler.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            Retain(error);
            if (!_scheduler.ExitConfirmed) throw;
            // The loop threw, but its actual read and owned resources have joined.
            // Failure remains sticky so the application exits unsuccessfully.
        }
    }
    private void Retain(Exception error)
    { lock (_gate) _failure = _failure is null ? error : ReferenceEquals(_failure, error) ? _failure : new AggregateException(_failure, error); }
    public ValueTask DisposeAsync()
    { lock (_gate) { _stopping = true; if (_commands == 0) _commandsDrained.TrySetResult(); return new(_disposal ??= StopAsync()); } }
    private async Task StopAsync()
    {
        // Reject new commands and join admitted controls before closing their owners.
        await _commandsDrained.Task.ConfigureAwait(false);
        if (_work is not null) await _work.DisposeAsync().ConfigureAwait(false);
        await StopSchedulerAsync().ConfigureAwait(false);
        await _runner.DisposeAsync().ConfigureAwait(false);
        _runner.CatalogChanged -= NotifyCatalogChanged;
        if (_monitor is not null) await _monitor.ConfigureAwait(false);
        Task? quiescence; lock (_gate) quiescence = _quiescence;
        if (quiescence is not null) await quiescence.ConfigureAwait(false);
        lock (_gate) _exitConfirmed = true;
    }
}
