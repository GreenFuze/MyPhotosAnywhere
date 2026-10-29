using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Joined current dependency and first-header preparation. Owns the
/// dependency child and its actual native/file users; borrows the reader's exact
/// state/account/budget/key owners. Stops at the original commit-sealed pending
/// header. Provider allocation/upload, Root activation and completion are separate.</summary>
public sealed class CurrentCatalogHeaderPreparationService : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly LocalCurrentControlReader _reader;
    private readonly CurrentCatalogDependencyPreparationService _dependencies;
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly object _gate=new();
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure??_dependencies.Failure;}}

    public CurrentCatalogHeaderPreparationService(ProcessorStateCoordinator state,string directory,Guid installationId,
        ProcessorAccountService accounts,StorageBudgetManager budget,LocalCurrentControlReader reader)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(accounts);ArgumentNullException.ThrowIfNull(budget);ArgumentNullException.ThrowIfNull(reader);
        if(!reader.BorrowsDependencyAllocationOwners(state,accounts,budget))
            throw new InvalidOperationException("Current header preparation must borrow the reader's exact owners.");
        _state=state;_budget=budget;_reader=reader;_dependencies=new(state,directory,installationId,accounts,budget,reader);
    }

    public async Task<WorkspacePublicationRecord> PrepareAsync(CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock(_gate) AssertRunning();var token=linked.Token;Check(token);
                var active=await _state.ExecuteAsync(database=>
                {
                    var capture=new CatalogGenerationCaptureRepository(database,_budget).Active().SingleOrDefault();
                    return(Capture:capture,HasHeader:capture is not null && new ProtocolSigningJournal(database,_budget).Read(capture.CommitIntentId) is not null,
                        HumanRevision:new LibrarySourceRepository(database).Snapshot().Revision);
                },token).ConfigureAwait(false);
                Guid generation;
                if(active.HasHeader)
                {
                    var original=active.Capture??throw new CatalogGenerationCaptureConflictException();
                    if(original.Phase is not("dependencies-ready" or "commit-sealed")) throw new CatalogGenerationCaptureConflictException();
                    generation=original.Scope.GenerationId;
                    if(active.HumanRevision>original.Scope.ConfigurationRevision)
                    {
                        // Complete inventory can be impossible after the first
                        // signature but before durable outbox insertion. Repair
                        // only the exact old LOCAL header under a real live fence;
                        // this grants no obsolete-scope cloud/write admission.
                        var capture=await _reader.CaptureAsync(token).ConfigureAwait(false);
                        _=await _reader.ReadSelectedPoliciesAsync(capture,token).ConfigureAwait(false);
                        var fence=await _reader.FenceAsync(capture,token).ConfigureAwait(false);
                        var recovered=await _reader.RecoverCatalogHeaderPendingAsync(fence,generation,token).ConfigureAwait(false);
                        if(recovered.Scope!=original.Scope || recovered.Id!=original.CommitIntentId)
                            throw new CatalogGenerationCaptureConflictException();
                        lock(_gate) {caller.ThrowIfCancellationRequested();Check(token);return recovered;}
                    }
                }
                else generation=(await _dependencies.PrepareAsync(token).ConfigureAwait(false)).Capture.Scope.GenerationId;
                var inventory=await ObserveAsync(token).ConfigureAwait(false);
                _=await _reader.ReserveFreshCatalogHeaderAsync(inventory,generation,token).ConfigureAwait(false);
                // Reservation changes native state. Never relabel the previous
                // inventory or final Root fence to admit signing or later IO.
                var final=await ObserveAsync(token).ConfigureAwait(false);
                var result=await _reader.SealFreshCatalogHeaderAsync(final,generation,token).ConfigureAwait(false);
                lock(_gate) {Check(token);return result;}
            }
            finally {_serial.Release();}
        }
    }

    private async Task<LocalCurrentWorkspaceInventory> ObserveAsync(CancellationToken token)
    {
        Check(token);var capture=await _reader.CaptureAsync(token).ConfigureAwait(false);
        return await _reader.ObserveWorkspaceInventorySessionAsync(capture,token).ConfigureAwait(false);
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping,this);
        if(_failure is not null || _dependencies.Failure is not null)
            throw new InvalidOperationException("Current header children have not confirmed retirement; retain borrowed owners.",_failure??_dependencies.Failure);
    }
    private void Check(CancellationToken token)
    {lock(_gate) if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Current header preparation retired before admission.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is{IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        var errors=new List<Exception>();try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error) {errors.Add(error);}
        Task child;try {child=_dependencies.DisposeAsync().AsTask();}catch(Exception error) {errors.Add(error);child=Task.CompletedTask;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            try {await child.ConfigureAwait(false);}catch(Exception error) {errors.Add(error);}
            if(!_dependencies.ExitConfirmed) errors.Add(new InvalidOperationException("The actual dependency owner has not joined."));
            if(errors.Count!=0) throw new AggregateException("Current header retirement is unconfirmed; retain borrowed owners.",errors);
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        finally {_serial.Release();}
    }
}
