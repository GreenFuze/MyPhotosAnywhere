using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Joined private metadata-file retirement only after an independently
/// retained current unsigned partial-work disposition. No selected completion, PublishedHead,
/// Drive operation, original action or successor admission is performed here. A
/// cancelled/uncertain unlink retains the exact original durable claim and owners.</summary>
public sealed class CurrentUnsignedGenerationRetirementService(ProcessorStateCoordinator state, StorageBudgetManager budget) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private CatalogGenerationSnapshotStore? _store;
    private CatalogSnapshotRetirement? _actual;
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }

    public async Task<CurrentUnsignedCatalogDisposition> FinishAsync(Guid generationId, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_gate) { AssertRunning(); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            var token = linked.Token; await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Check(token); var capture = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).Read(generationId)
                    ?? throw new CatalogGenerationCaptureConflictException(), token).ConfigureAwait(false);
                // This owner cannot create a disposition from historical JSON or
                // select an unselected commit. The live current owner records it.
                _=await _state.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,_budget).UnsignedDisposition(generationId)
                    ?? throw new CatalogGenerationCaptureConflictException(),token).ConfigureAwait(false);
                if (capture.Phase is "complete" or "snapshot-retiring")
                {
                    capture = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).BeginUnsignedSnapshotRetirement(generationId), token).ConfigureAwait(false);
                    _store = await _state.ExecuteAsync(database => new CatalogGenerationSnapshotStore(database.StateDirectory, database, _budget), token).ConfigureAwait(false);
                    _actual = await Task.Run(() => _store.RetireCompletedSnapshot(capture, token), CancellationToken.None).ConfigureAwait(false);
                    Check(token);
                    capture = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).ConfirmUnsignedSnapshotRetired(_actual), token).ConfigureAwait(false);
                    CloseResources();
                }
                if (capture.Phase != "retired") throw new CatalogGenerationCaptureConflictException();
                var result = await _state.ExecuteAsync(database =>
                {
                    return new CatalogGenerationCaptureRepository(database, _budget).UnsignedDisposition(generationId)
                        ?? throw new InvalidDataException("Retired unsigned generation lost its exact original disposition.");
                }, token).ConfigureAwait(false);
                Check(token); return result;
            }
            catch (Exception error)
            {
                try { CloseResources(); }
                catch (Exception cleanup) { throw new AggregateException("Unsigned snapshot retirement and native cleanup both failed.", error, cleanup); }
                throw;
            }
            finally { _serial.Release(); }
        }
    }
    private void CloseResources()
    {
        try { _actual?.Dispose(); _actual = null; _store?.Dispose(); _store = null; }
        catch (Exception error) { lock (_gate) _failure = error; throw; }
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_failure is not null) throw new InvalidOperationException("Generation retirement cleanup is unconfirmed; retain its native/state owners.", _failure);
    }
    private void Check(CancellationToken token)
    {
        lock (_gate)
        {
            if (_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Unsigned snapshot retirement was stopped.", token);
            AssertRunning();
        }
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
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            try { CloseResources(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Unsigned snapshot retirement failed; retain borrowed owners.", errors);
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
}
