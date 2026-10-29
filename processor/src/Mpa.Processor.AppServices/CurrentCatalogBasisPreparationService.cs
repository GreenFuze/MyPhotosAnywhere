using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Joined actual private-snapshot and native-basis preparation. Owns the
/// snapshot child; borrows the current reader/state. Retains original planned work
/// instead of replacing it after imports/restart. Dependency IO remains separate.</summary>
public sealed class CurrentCatalogBasisPreparationService : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly LocalCurrentControlReader _reader;
    private readonly CurrentCatalogSnapshotService _snapshot;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private Task? _disposal;
    private Exception? _failure;
    private bool _stopping,_exitConfirmed;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure ?? _snapshot.Failure;}}

    public CurrentCatalogBasisPreparationService(ProcessorStateCoordinator state,string directory,Guid installationId,
        StorageBudgetManager budget,LocalCurrentControlReader reader)
    {
        _state=state ?? throw new ArgumentNullException(nameof(state));_budget=budget ?? throw new ArgumentNullException(nameof(budget));
        _reader=reader ?? throw new ArgumentNullException(nameof(reader));_snapshot=new(state,directory,installationId,budget,reader);
    }
    public async Task<CurrentPreparedCatalogBasis> PrepareAsync(CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock(_gate) AssertRunning();var token=linked.Token;
                var claim=await _state.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,_budget).Active().SingleOrDefault(),token).ConfigureAwait(false);
                if(claim is null || claim.Phase is "capturing" or "frozen") claim=await _snapshot.PrepareAsync(token).ConfigureAwait(false);
                else if(claim.Phase!="publication-planned") throw new CatalogGenerationCaptureConflictException();
                return await _reader.PrepareCurrentPublicationBasisAsync(claim.Scope.GenerationId,token).ConfigureAwait(false);
            }
            finally {_serial.Release();}
        }
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping,this);
        if(_failure is not null || _snapshot.Failure is not null) throw new InvalidOperationException("Current basis cleanup is unconfirmed; retain borrowed owners.",_failure ?? _snapshot.Failure);
    }
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal ??=StopAsync());}}
    private async Task StopAsync()
    {
        var failures=new List<Exception>();try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error) {failures.Add(error);}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            try {await _snapshot.DisposeAsync().ConfigureAwait(false);}catch(Exception error) {failures.Add(error);}
            if(!_snapshot.ExitConfirmed) failures.Add(new InvalidOperationException("The actual snapshot child has not joined."));
            if(failures.Count!=0) {var error=new AggregateException("Current basis retirement failed; borrowed owners remain retained.",failures);lock(_gate) _failure=error;throw error;}
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        finally {_serial.Release();}
    }
}
