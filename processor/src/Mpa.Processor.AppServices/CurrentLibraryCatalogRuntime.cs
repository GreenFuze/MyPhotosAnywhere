using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Engine-lifetime ownership of current control, the shared two-index
/// pool, actual canonical readers and the outgoing generation drain. Accounts,
/// native signing keys, serialized state and the global budget are borrowed.
/// Stop joins every real child before their borrowed owners may retire.</summary>
public sealed class CurrentLibraryCatalogRuntime : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly ProcessorAccountService _accounts;
    private readonly LibrarySigningIdentityOwner _keys;
    private readonly StorageBudgetManager _budget;
    private readonly InitialCurrentControlPublicationService _bootstrap;
    private readonly LocalCurrentControlReader _reader;
    private readonly LocalCurrentStagePool _pool;
    private readonly CurrentCanonicalReadRuntime _canonical;
    private readonly CurrentCatalogGenerationRuntime _generations;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure??_bootstrap.Failure??_generations.Failure??_canonical.Failure;}}
    // Borrow for bounded historical gallery queries. This is not a publication
    // grant and does not transfer ownership of the canonical handles or pool.
    public CurrentCanonicalReadRuntime Catalog=>_canonical;
    internal bool Borrows(ProcessorStateCoordinator state,ProcessorAccountService accounts,LibrarySigningIdentityOwner keys,StorageBudgetManager budget)=>
        ReferenceEquals(state,_state) && ReferenceEquals(accounts,_accounts) && ReferenceEquals(keys,_keys) && ReferenceEquals(budget,_budget);

    public CurrentLibraryCatalogRuntime(ProcessorStateCoordinator state,string directory,Guid installationId,
        ProcessorAccountService accounts,LibrarySigningIdentityOwner keys,StorageBudgetManager budget)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(accounts);ArgumentNullException.ThrowIfNull(keys);ArgumentNullException.ThrowIfNull(budget);
        // Validate structural arguments before constructing any semantic child.
        if(installationId==Guid.Empty) throw new ArgumentException("An installation identity is required.",nameof(installationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);directory=Path.GetFullPath(directory);
        _state=state;_accounts=accounts;_keys=keys;_budget=budget;
        _bootstrap=new(state,accounts,keys,budget);
        _reader=new(state,accounts,keys,budget);_pool=new(state,budget);_canonical=new(state,budget,_reader,_pool);
        _generations=new(state,directory,installationId,accounts,budget,_reader,_canonical,_pool);
    }
    public Task<CurrentCatalogActivation?> InitializeHistoricalAsync(CancellationToken caller)=>OperationAsync(
        token=>_canonical.InitializeHistoricalAsync(token),caller);
    public Task<PublishedCatalogHead> RunUntilCurrentAsync(CancellationToken caller)=>OperationAsync(async token=>
    {
        // Bootstrap the fixed initial no-catalog Root exactly once after the two
        // real initial policies complete. A historical bootstrap receipt merely
        // avoids replaying create-only publication over a later current Root;
        // the generation drain still performs actual current admission.
        var first=await _state.ExecuteAsync(database=>new InitialCurrentControlPublicationRepository(database,_budget).Read(),token).ConfigureAwait(false);
        if(first is null) _=await _bootstrap.PublishAsync(token).ConfigureAwait(false);
        return await _generations.RunUntilCurrentAsync(token).ConfigureAwait(false);
    },caller);
    private async Task<T> OperationAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try {Check(linked.Token);var result=await action(linked.Token).ConfigureAwait(false);Check(linked.Token);return result;}
            finally {_serial.Release();}
        }
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping,this);
        if(_failure is not null) throw new InvalidOperationException("Current library retirement is unconfirmed; retain borrowed accounts, keys, state and budget.",_failure);
    }
    private void Check(CancellationToken token)
    {lock(_gate) {if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Current library work retired before result admission.",token);AssertRunning();}}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is{IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? cancellation=null;try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error) {cancellation=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            // Order is semantic: borrowers first, then canonical handles, directory
            // pool, then the actual current reader. Failure retains every later
            // owner rather than disposing underneath unjoined physical work.
            await _generations.DisposeAsync().ConfigureAwait(false);
            if(!_generations.ExitConfirmed) throw new InvalidOperationException("Current generation work has not joined.");
            await _bootstrap.DisposeAsync().ConfigureAwait(false);
            if(!_bootstrap.ExitConfirmed) throw new InvalidOperationException("Actual initial Root publication has not joined.");
            await _canonical.DisposeAsync().ConfigureAwait(false);
            if(!_canonical.ExitConfirmed) throw new InvalidOperationException("Actual canonical handles have not joined.");
            await _pool.DisposeAsync().ConfigureAwait(false);
            if(!_pool.ExitConfirmed) throw new InvalidOperationException("The shared current index pool has not joined.");
            await _reader.DisposeAsync().ConfigureAwait(false);
            if(!_reader.ExitConfirmed) throw new InvalidOperationException("The actual current control reader has not joined.");
            if(cancellation is not null) throw new AggregateException("Current library cancellation failed; keep the engine's borrowed owners retained.",cancellation);
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        finally {_serial.Release();}
    }
}
