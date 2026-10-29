using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Engine-owned serial generation work, independent of any browser. Resume
/// original durable work before comparing bounded local input hints. Hints only schedule;
/// actual current reconciliation still owns every write and unchanged-ready admission.
/// Owns preparation/completion; borrows current reconciliation, native keys and state.</summary>
public sealed class CatalogGenerationRuntime : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly LocalCatalogReconciliationService _current;
    private readonly CatalogGenerationPreparationService _preparation;
    private readonly CatalogGenerationCompletionService _completion;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }
    internal bool Borrows(LocalCatalogReconciliationService current) => ReferenceEquals(current, _current);

    public CatalogGenerationRuntime(ProcessorStateCoordinator state, string directory, Guid installationId, StorageBudgetManager budget,
        LocalCatalogReconciliationService current, LibrarySigningIdentityOwner keys)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state)); _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _preparation = new(state, directory, installationId, budget, current, keys); _completion = new(state, budget, current);
    }

    public async Task<PublishedCatalogHead> RunUntilCurrentAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_gate) { AssertRunning(); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            var token = linked.Token; await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    Check(token); var pending = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).Active().SingleOrDefault(), token).ConfigureAwait(false);
                    if (pending is null && await InputsMatchAsync(token).ConfigureAwait(false))
                    {
                        // Equality of retained local inputs is never current remote
                        // availability, history or authority. Recheck the actual Drive
                        // library before admitting ready, then catch a concurrent import.
                        while (true)
                        {
                            try { await _current.ReconcileAsync(token).ConfigureAwait(false); break; }
                            catch (WorkspaceHistoryReplayPendingException) { Check(token); }
                        }
                        if (!await InputsMatchAsync(token).ConfigureAwait(false)) continue;
                        var head = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).PublishedHead()
                            ?? throw new InvalidDataException("Matching published inputs lost their completed head."), token).ConfigureAwait(false);
                        Check(token); return head;
                    }
                    if (pending is null || pending.Phase is "capturing" or "frozen" or "publication-planned")
                        pending = (await _preparation.PrepareAsync(token).ConfigureAwait(false)).Capture;
                    Check(token); _ = await _completion.FinishAsync(pending.Scope.GenerationId, token).ConfigureAwait(false);
                    // Imports and coverage may change while the frozen generation is
                    // uploaded. Finish it exactly, then capture the next observation.
                }
            }
            finally { _serial.Release(); }
        }
    }
    private Task<bool> InputsMatchAsync(CancellationToken token) => _state.ExecuteAsync(database =>
    {
        Check(token); var reader = new CatalogGenerationInputReader(database, _budget); return reader.Current(token) == reader.Published(token);
    }, token);
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_failure is not null) throw new InvalidOperationException("Generation runtime retirement is unconfirmed; retain borrowed owners.", _failure);
    }
    private void Check(CancellationToken token)
    {
        lock (_gate) { if (_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Generation runtime was retired.", token); AssertRunning(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_disposal is { IsFaulted: true } && !_exitConfirmed) _disposal = null;
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        var errors = new List<Exception>();
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        // Joined RunUntilCurrent owns all actual child calls. Do not retire a child
        // underneath a still-running parent and manufacture ObjectDisposed failures.
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await _completion.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            try { await _preparation.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0)
            {
                var error = new AggregateException("Generation runtime retirement failed; borrowed owners remain retained.", errors);
                lock (_gate) _failure = error; throw error;
            }
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
}
