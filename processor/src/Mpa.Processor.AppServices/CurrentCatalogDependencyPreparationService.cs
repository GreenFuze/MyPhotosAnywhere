using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Joined current-only dependency producer and exact frozen-graph check.
/// Owns basis/sink and retained-file users; borrows actual reader/account/state/
/// budget owners. It stops at the original dependencies-ready plan, never signs,
/// selects Root, retires claimed snapshots or changes an original image.</summary>
public sealed class CurrentCatalogDependencyPreparationService : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly ProcessorAccountService _accounts;
    private readonly StorageBudgetManager _budget;
    private readonly LocalCurrentControlReader _reader;
    private readonly CurrentCatalogBasisPreparationService _basis;
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly object _gate=new();
    private readonly CancellationTokenSource _stop=new();
    private CurrentCatalogGenerationObjectSink? _sink;
    private CatalogGenerationSnapshotStore? _store;
    private FrozenCatalogSnapshot? _frozen;
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure??_basis.Failure??_sink?.Failure;}}

    public CurrentCatalogDependencyPreparationService(ProcessorStateCoordinator state,string directory,Guid installationId,
        ProcessorAccountService accounts,StorageBudgetManager budget,LocalCurrentControlReader reader)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(accounts);ArgumentNullException.ThrowIfNull(budget);ArgumentNullException.ThrowIfNull(reader);
        if(!reader.BorrowsDependencyAllocationOwners(state,accounts,budget)) throw new InvalidOperationException("Current producer must borrow the reader's exact owners.");
        _state=state;_accounts=accounts;_budget=budget;_reader=reader;
        _basis=new(state,directory,installationId,budget,reader);
    }

    public async Task<CapturedCatalogPublication> PrepareAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            CapturedCatalogPublication? result=null;Exception? operationFailure=null;
            try
            {
              try
              {
                lock(_gate) AssertRunning();var token=linked.Token;Check(token);
                var active=await _state.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,_budget).Active().SingleOrDefault(),token).ConfigureAwait(false);
                CapturedCatalogPublication publication;CurrentPreparedCatalogBasis? prepared=null;
                if(active?.Phase=="dependencies-ready")
                    publication=await _state.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,_budget).Publication(active.Scope.GenerationId)
                        ??throw new CatalogGenerationCaptureConflictException(),token).ConfigureAwait(false);
                else
                {
                    prepared=await _basis.PrepareAsync(token).ConfigureAwait(false);publication=prepared.Publication;
                }
                if(_store is not null || _frozen is not null || _sink is not null) throw new InvalidOperationException("A preceding dependency producer resource remains owned.");
                _store=await _state.ExecuteAsync(database=>new CatalogGenerationSnapshotStore(database.StateDirectory,database,_budget),token).ConfigureAwait(false);
                _frozen=await Task.Run(()=>_store.Open(publication.Capture,token),CancellationToken.None).ConfigureAwait(false);Check(token);
                var plan=publication.Plan;
                if(plan is null)
                {
                    if(prepared is null || prepared.Publication!=publication) throw new CatalogGenerationCaptureConflictException();
                    _sink=new(publication.Capture.Scope.GenerationId,_state,_accounts,_budget,_reader);
                    var graph=await new CatalogGenerationDependencyBuilder(new(publication.Capture.Scope.AccountId,publication.Capture.Scope.FolderId),
                        _sink,WorkspaceObservationRepository.MaximumDocuments).BuildAsync(_frozen,prepared.Sources,publication.Basis.Parent,token).ConfigureAwait(false);
                    if(graph.Assets!=_frozen.Count || graph.SourcePolicySha256!=publication.Basis.SourcePolicySha256)
                        throw new CatalogGenerationCaptureConflictException();
                    plan=new(publication.Basis,graph.RootIndex,graph.SourceCoverage);
                }
                result=await _reader.SealPreparedDependencyGraphAsync(publication.Capture.Scope.GenerationId,plan,_frozen,token).ConfigureAwait(false);
            }
              catch(Exception error) {operationFailure=error;}
                try {await CloseResourcesAsync().ConfigureAwait(false);}
                catch(Exception cleanup)
                {
                    lock(_gate) _failure=cleanup;
                    if(operationFailure is not null) throw new AggregateException("Current graph preparation and joined cleanup both failed.",operationFailure,cleanup);
                    throw;
                }
                if(operationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationFailure).Throw();
                lock(_gate) {Check(linked.Token);return result??throw new InvalidOperationException("Current dependency preparation produced no publication.");}
            }
            finally {_serial.Release();}
        }
    }

    private async Task CloseResourcesAsync()
    {
        if(_sink is not null)
        {
            await _sink.DisposeAsync().ConfigureAwait(false);
            if(!_sink.ExitConfirmed) throw new InvalidOperationException("Actual current dependency children have not joined.");
            _sink=null;
        }
        _frozen?.Dispose();_frozen=null;_store?.Dispose();_store=null;
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping,this);
        if(_failure is not null || _basis.Failure is not null) throw new InvalidOperationException("Current dependency cleanup is unconfirmed; retain borrowed owners.",_failure??_basis.Failure);
    }
    private void Check(CancellationToken token)
    {lock(_gate) if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Current dependency producer retired before admission.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error) {cancellationFailure=error;}
        // Stop children before waiting for a lane that may be inside those children.
        var basis=_basis.DisposeAsync().AsTask();var sink=_sink?.DisposeAsync().AsTask()??Task.CompletedTask;
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(basis,sink).ConfigureAwait(false);await CloseResourcesAsync().ConfigureAwait(false);
            if(!_basis.ExitConfirmed) throw new InvalidOperationException("The actual current basis child has not joined.");
            if(cancellationFailure is not null) throw new AggregateException("Current producer cancellation needs attention; retain borrowed owners.",cancellationFailure);
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        finally {_serial.Release();}
    }
}
