using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Joined engine-lifetime current generation drain. Local input equality
/// schedules work only: an unchanged result still requires real selected traversal,
/// complete current inventory and Root-last canonical admission. Resume original
/// durable work before comparing hints. Owns publication; borrows actual shared
/// reader/canonical/pool/state/account/budget owners. No browser lifetime is used.</summary>
public sealed class CurrentCatalogGenerationRuntime : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly CurrentCanonicalReadRuntime _canonical;
    private readonly CurrentCatalogPublicationService _publication;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure??_publication.Failure;}}

    public CurrentCatalogGenerationRuntime(ProcessorStateCoordinator state,string directory,Guid installationId,
        ProcessorAccountService accounts,StorageBudgetManager budget,LocalCurrentControlReader reader,
        CurrentCanonicalReadRuntime canonical,LocalCurrentStagePool pool)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(budget);ArgumentNullException.ThrowIfNull(canonical);
        // The concrete publication child checks every borrowed owner before any
        // native/account IO; no accepted caller-supplied readiness implementation.
        _publication=new(state,directory,installationId,accounts,budget,reader,canonical,pool);
        _state=state;_budget=budget;_canonical=canonical;
    }

    public async Task<PublishedCatalogHead> RunUntilCurrentAsync(CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            var token=linked.Token;await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while(true)
                {
                    Check(token);
                    var pending=await _state.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,_budget).Active().SingleOrDefault(),token).ConfigureAwait(false);
                    if(pending is null && await InputsMatchAsync(token).ConfigureAwait(false))
                    {
                        // Neither a retained completion nor equal discovery input
                        // hints assert current remote availability or authority.
                        var selected=await _canonical.RefreshAsync(token).ConfigureAwait(false);
                        if(!await InputsMatchAsync(token).ConfigureAwait(false)) continue;
                        var head=await _state.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,_budget).PublishedHead()
                            ??throw new InvalidDataException("Matching current-generation inputs lost their retained completed head."),token).ConfigureAwait(false);
                        if(selected.Catalog!=head.Commit) throw new CurrentControlObservationConflictException();
                        Check(token);return head;
                    }
                    var published=await _publication.PublishForRuntimeAsync(token).ConfigureAwait(false);
                    // This operation already performed real canonical admission.
                    // Do not replace its index/cursor context with a duplicate
                    // refresh. Offline historical completion has no such result
                    // and must take the separate real-ready check next iteration.
                    if(published.Selection is {} admitted && await InputsMatchAsync(token).ConfigureAwait(false))
                    {
                        if(_canonical.Selection!=admitted || admitted.Catalog!=published.Head.Commit)
                            throw new CurrentControlObservationConflictException();
                        Check(token);return published.Head;
                    }
                    // Finish the exact original frozen generation even if discovery
                    // changed during its upload, then capture the next observation.
                    // An unresolved scope/history failure propagates with all work
                    // retained; it cannot be retried in an unbounded busy loop.
                }
            }
            finally {_serial.Release();}
        }
    }
    private Task<bool> InputsMatchAsync(CancellationToken token)=>_state.ExecuteAsync(database=>
    {
        Check(token);var inputs=new CatalogGenerationInputReader(database,_budget);
        return inputs.Current(token)==inputs.Published(token);
    },token);
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping,this);
        if(_failure is not null) throw new InvalidOperationException("Current generation retirement is unconfirmed; retain borrowed owners.",_failure);
    }
    private void Check(CancellationToken token)
    {lock(_gate) {if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Current generation drain retired before result admission.",token);AssertRunning();}}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is{IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        var errors=new List<Exception>();try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error) {errors.Add(error);}
        // Active canonical/reader/physical transport callbacks must actually join
        // before retiring the publication child or its borrowed engine owners.
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            try {await _publication.DisposeAsync().ConfigureAwait(false);}catch(Exception error) {errors.Add(error);}
            if(!_publication.ExitConfirmed) errors.Add(new InvalidOperationException("The actual outgoing publication owner has not joined."));
            if(errors.Count!=0) throw new AggregateException("Current generation runtime retirement needs attention; retain borrowed owners.",errors);
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        finally {_serial.Release();}
    }
}
