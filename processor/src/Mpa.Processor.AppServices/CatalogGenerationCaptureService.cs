using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

// Local native-copy validation only. It is never an upload/availability capability.
// The private provider resolves the current owned context inside the serial state
// callback, so a copy cannot retain a context retired by a reconciled credential change.
internal interface ICatalogGenerationLocalAccessProvider
{
    CatalogGenerationLocalAccess? ForScope(WorkspacePublicationScope scope);
}

// Internal private-copy admission only. A current owner supplies its own atomic
// state callbacks; it cannot fabricate the legacy renewed-credential capability.
internal interface ICurrentCatalogSnapshotAdmission
{
    CatalogGenerationCapture Begin(ProcessorStateDatabase database,WorkspacePublicationScope scope,Guid snapshotId,Guid intentId,CancellationToken token);
    CatalogGenerationCapture Seal(ProcessorStateDatabase database,Guid generationId,CatalogSnapshotDescriptor descriptor,CancellationToken token);
}

/// <summary>Serial application owner of journaled capture and retained readers. The
/// caller supplies the generation's original identities and complete current authority;
/// this service grants no cloud reconciliation, head scheduling or publication rights.
/// Consumers borrow the frozen reader and must join all work before returning.</summary>
public sealed class CatalogGenerationCaptureService : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly string _directory;
    private readonly IWorkspacePublicationAuthority _authority;
    private readonly CatalogPreparationService _preparation;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private CatalogGenerationSnapshotStore? _store;
    private FrozenCatalogSnapshot? _reader;
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;

    public CatalogGenerationCaptureService(ProcessorStateCoordinator state, string stateDirectory, Guid installationId,
        StorageBudgetManager budget, IWorkspacePublicationAuthority authority)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _directory = Path.GetFullPath(stateDirectory);
        _preparation = new(state, _directory, installationId, budget);
    }
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }

    public async Task<T> RunAsync<T>(WorkspacePublicationScope scope, Guid snapshotId, Guid commitIntentId,
        Func<CatalogGenerationCapture, FrozenCatalogSnapshot, CancellationToken, Task<T>> consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(consumer);
        CancellationTokenSource linked;
        lock (_gate) { AssertRunning(); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock (_gate) AssertRunning(); var token = linked.Token;
                await ValidateAsync(scope, token).ConfigureAwait(false);
                var claim=await _state.ExecuteAsync(database=>_authority is ICurrentCatalogSnapshotAdmission current
                    ? current.Begin(database,scope,snapshotId,commitIntentId,token)
                    : new CatalogGenerationCaptureRepository(database,_budget).Begin(scope,snapshotId,commitIntentId),token).ConfigureAwait(false);
                await OpenStoreAsync(token).ConfigureAwait(false);
                if (claim.Phase == "capturing")
                {
                    // A complete file can outlive a lost state checkpoint. Inspect it;
                    // never re-capture or overwrite an existing partial/corrupt file.
                    var exists = await Task.Run(() => _store!.ContainsCaptureFile(claim), CancellationToken.None).ConfigureAwait(false);
                    var descriptor = exists
                        ? await Task.Run(() => _store!.Inspect(claim, token), CancellationToken.None).ConfigureAwait(false)
                        : await _preparation.RunAsync((frozen, captureToken) => Task.Run(() => _store!.Save(frozen, claim, captureToken), CancellationToken.None), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); await ValidateAsync(scope, token).ConfigureAwait(false);
                    claim=await _state.ExecuteAsync(database=>_authority is ICurrentCatalogSnapshotAdmission current
                        ? current.Seal(database,scope.GenerationId,descriptor,token)
                        : new CatalogGenerationCaptureRepository(database,_budget).SealSnapshot(scope.GenerationId,descriptor),token).ConfigureAwait(false);
                    CloseResources(); await OpenStoreAsync(token).ConfigureAwait(false); // Refresh the durable frozen claim.
                }
                _reader = await Task.Run(() => _store!.Open(claim, token), CancellationToken.None).ConfigureAwait(false);
                await ValidateAsync(scope, token).ConfigureAwait(false);
                ThrowIfCanceled(cancellationToken, token);
                var result = await consumer(claim, _reader, token).ConfigureAwait(false);
                ThrowIfCanceled(cancellationToken, token); await ValidateAsync(scope, token).ConfigureAwait(false);
                CloseResources(); ThrowIfCanceled(cancellationToken, token); return result;
            }
            catch (Exception error)
            {
                if (_preparation.Failure is { } preparationFailure) lock (_gate) _failure ??= preparationFailure;
                try { CloseResources(); }
                catch (Exception cleanup) { throw new AggregateException("Catalog generation work and retained-owner cleanup failed.", error, cleanup); }
                throw;
            }
            finally { _serial.Release(); }
        }
    }
    private void ThrowIfCanceled(CancellationToken caller, CancellationToken linked)
    {
        lock (_gate)
            if (_stopping || caller.IsCancellationRequested || linked.IsCancellationRequested) throw new OperationCanceledException("Catalog generation capture was retired before result admission.", linked);
    }
    private async Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); await _authority.ValidateAsync(scope, token).ConfigureAwait(false);
        await _state.ExecuteAsync(database =>
        {
            var local = (_authority as ICatalogGenerationLocalAccessProvider)?.ForScope(scope);
            new LibraryAuthorityRepository(database).AssertLocalPublicationScope(scope, local); return true;
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
    private async Task OpenStoreAsync(CancellationToken token)
    {
        if (_store is not null) throw new InvalidOperationException("The previous retained store is still owned.");
        _store = await _state.ExecuteAsync(database => new CatalogGenerationSnapshotStore(_directory, database, _budget), token).ConfigureAwait(false);
    }
    private void CloseResources()
    {
        try { _reader?.Dispose(); _reader = null; _store?.Dispose(); _store = null; }
        catch (Exception error) { lock (_gate) _failure = error; throw; }
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_failure is not null) throw new InvalidOperationException("Catalog generation cleanup is pending; owners remain retained.", _failure);
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
            try { await _preparation.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Catalog generation shutdown is unconfirmed; retain its state owners.", errors);
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
}
