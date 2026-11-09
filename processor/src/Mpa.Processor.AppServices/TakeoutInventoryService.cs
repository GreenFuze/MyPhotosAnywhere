using System.Runtime.ExceptionServices;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>One joined desktop import-inventory owner. The native job already
/// captures every selected part and destination before this owner opens inputs.
/// Its completion is local inventory evidence, never an original backup receipt.</summary>
public sealed class TakeoutInventoryService : IProcessorWorkControl,IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly object _gate=new();
    private ProcessorWorkState _status=ProcessorWorkState.Ready;
    private Task<TakeoutCompletedInventory>? _run;
    private CancellationTokenSource? _runStop;
    private Task? _pause;
    private Task? _disposal;
    private Guid _runId;
    private Attempt? _retained;
    private Exception? _failure;
    private bool _stopping,_exitConfirmed;
    public ProcessorWorkState State {get{lock(_gate)return _status;}}
    public Exception? Failure {get{lock(_gate)return _failure;}}
    public bool ExitConfirmed {get{lock(_gate)return _exitConfirmed;}}
    public event Action<Exception>? Failed;
    public TakeoutInventoryService(ProcessorStateCoordinator state,StorageBudgetManager budget)
    {_state=state??throw new ArgumentNullException(nameof(state));_budget=budget??throw new ArgumentNullException(nameof(budget));}
    /// <summary>Desktop composition-root startup, before listeners or import
    /// admission. Recover only this fixed in-process stage under the real native
    /// state/index leases, then expose a paused owner for grouped activation.</summary>
    public static async Task<TakeoutInventoryService> CreatePausedAfterExclusiveRestartAsync(ProcessorStateCoordinator state,StorageBudgetManager budget,CancellationToken token)
    {
        var owner=new TakeoutInventoryService(state,budget);owner._status=ProcessorWorkState.Paused;
        try {await state.ExecuteAsync(database=>new TakeoutImportInventoryRepository(database).RecoverInterruptedAfterExclusiveRestart(),token).ConfigureAwait(false);return owner;}
        catch {await owner.DisposeAsync().ConfigureAwait(false);throw;}
    }

    public Task<TakeoutCompletedInventory> RunAsync(Guid importId,CancellationToken caller)
    {
        if(importId==Guid.Empty)throw new ArgumentException("A previously captured import identity is required.",nameof(importId));
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping,this);
            if(_status!=ProcessorWorkState.Ready||_run is{IsCompleted:false})throw new ProcessorWorkStateConflictException();
            caller.ThrowIfCancellationRequested();_runStop?.Dispose();_runStop=CancellationTokenSource.CreateLinkedTokenSource(caller);var token=_runStop.Token;
            var id=Guid.NewGuid();_runId=id;_status=ProcessorWorkState.Running;
            return _run=Task.Run(()=>ExecuteAsync(importId,id,caller,token),CancellationToken.None);
        }
    }
    private async Task<TakeoutCompletedInventory> ExecuteAsync(Guid importId,Guid id,CancellationToken caller,CancellationToken token)
    {
        var owned=new Attempt();TakeoutCompletedInventory? result=null;Exception? failure=null;
        try
        {
            var acquisition=await _state.ExecuteAsync(database=>
            {
                lock(_gate)
                {
                    Check(id,caller,token);var repository=new TakeoutImportInventoryRepository(database);var completed=repository.ReadCompleted(importId);
                    if(completed is not null)return new Acquisition(null,completed);
                    return new Acquisition(repository.Claim(importId)??throw new ProcessorWorkStateConflictException(),null);
                }
            },token).ConfigureAwait(false);
            owned.Work=acquisition.Work;
            if(acquisition.Completed is { } completed)
            {
                owned.Catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,_budget,token).ConfigureAwait(false);
                lock(_gate){Check(id,caller,token);result=completed;}
            }
            else
            {
                var work=owned.Work!;
                owned.Catalog=await _state.ExecuteAsync(database=>TakeoutAssociationCatalog.Open(database,work,_budget,token),token).ConfigureAwait(false);
                foreach(var part in work.Parts)
                {
                    lock(_gate)Check(id,caller,token);
                    owned.Input=part.Kind=="folder"?TakeoutSelectedInput.OpenFolder(part.PartId,part.Location):
                        await TakeoutSelectedInput.OpenZipAsync(part.PartId,part.Location,part.Bytes!.Value,part.Sha256!,token).ConfigureAwait(false);
                    // Enumeration is synchronous native IO, on this owned background
                    // operation, never the browser/UI thread or the SQLite queue.
                    owned.Catalog.AddPart(owned.Input,token);owned.ReleaseInput();
                }
                await owned.Catalog.SealAsync(token).ConfigureAwait(false);
                result=await _state.ExecuteAsync(database=>
                {
                    lock(_gate)
                    {
                        Check(id,caller,token);var repository=new TakeoutImportInventoryRepository(database);repository.Complete(work,owned.Catalog);
                        owned.Completed=true;return repository.ReadCompleted(importId)??throw new InvalidDataException("The exact committed inventory receipt disappeared.");
                    }
                },token).ConfigureAwait(false);
            }
        }
        catch(Exception error){failure=error;}
        try {await RetireAsync(owned).ConfigureAwait(false);}
        catch(Exception cleanup)
        {
            failure=failure is null?cleanup:new AggregateException(failure,cleanup);
            lock(_gate)_retained=owned;
        }
        var notify=false;
        lock(_gate)
        {
            var canceled=failure is OperationCanceledException&&(caller.IsCancellationRequested||token.IsCancellationRequested||_status is ProcessorWorkState.Pausing or ProcessorWorkState.Stopping);
            var unavailable=failure is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException &&
                owned.Input is null&&owned.Catalog is null&&(owned.Work is null||owned.Returned);
            if(failure is not null&&!canceled&&!unavailable){_failure=failure;_status=ProcessorWorkState.Faulted;notify=true;}
            else if(_status==ProcessorWorkState.Running)_status=ProcessorWorkState.Ready;
        }
        if(notify)
        {
            try {Failed?.Invoke(failure!);}
            catch(Exception notification){failure=new AggregateException(failure!,notification);lock(_gate)_failure=failure;}
        }
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
        return result??throw new InvalidOperationException("Inventory owner retired without a result.");
    }
    private async Task RetireAsync(Attempt owned)
    {
        owned.ReleaseInput();owned.Catalog?.Dispose();owned.Catalog=null;
        if(owned.Work is{ } work&&!owned.Completed&&!owned.Returned)
        {
            // Every actual reader/index owner has retired. Cancellation must not
            // abandon the exact pending native attempt during this bookkeeping.
            await _state.ExecuteAsync(database=>{new DurableJobRepository(database).ReturnJoinedRead(work.Lease);return true;},CancellationToken.None).ConfigureAwait(false);
            owned.Returned=true;
        }
    }
    private void Check(Guid id,CancellationToken caller,CancellationToken token)
    {
        caller.ThrowIfCancellationRequested();token.ThrowIfCancellationRequested();
        if(_stopping||_status!=ProcessorWorkState.Running||_runId!=id)throw new OperationCanceledException("Import inventory owner closed admission before completion.",token);
    }
    public Task PauseAsync()
    {
        lock(_gate)
        {
            if(_status==ProcessorWorkState.Paused)return Task.CompletedTask;
            if(_status==ProcessorWorkState.Pausing)return _pause!;
            if(_stopping||_status is not(ProcessorWorkState.Ready or ProcessorWorkState.Running))throw new ProcessorWorkStateConflictException();
            _status=ProcessorWorkState.Pausing;return _pause=Task.Run(PauseOwnedAsync,CancellationToken.None);
        }
    }
    private async Task PauseOwnedAsync()
    {
        CancellationTokenSource? stop;Task? run;lock(_gate){stop=_runStop;run=_run;}
        try
        {
            if(stop is not null)await stop.CancelAsync().ConfigureAwait(false);
            if(run is not null)try{await run.ConfigureAwait(false);}catch(OperationCanceledException)when(stop?.IsCancellationRequested==true){ }
            lock(_gate){if(_failure is not null)ExceptionDispatchInfo.Capture(_failure).Throw();if(!_stopping)_status=ProcessorWorkState.Paused;}
        }
        catch(Exception error){lock(_gate){_failure=error;_status=ProcessorWorkState.Faulted;}throw;}
    }
    public Task ResumeAsync()
    {
        lock(_gate)
        {
            if(_stopping||_status!=ProcessorWorkState.Paused||_run is{IsCompleted:false}||_retained is not null)throw new ProcessorWorkStateConflictException();
            _status=ProcessorWorkState.Ready;return Task.CompletedTask;
        }
    }
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            _stopping=true;if(_exitConfirmed)return ValueTask.CompletedTask;_status=ProcessorWorkState.Stopping;
            if(_disposal is{IsFaulted:true})_disposal=null;return new(_disposal??=Task.Run(StopOwnedAsync,CancellationToken.None));
        }
    }
    private async Task StopOwnedAsync()
    {
        CancellationTokenSource? stop;Task? run;Task? pause;lock(_gate){stop=_runStop;run=_run;pause=_pause;}
        try
        {
            if(stop is not null)await stop.CancelAsync().ConfigureAwait(false);
            if(pause is not null)try{await pause.ConfigureAwait(false);}catch(Exception error){lock(_gate)_failure??=error;}
            if(run is not null)try{await run.ConfigureAwait(false);}catch(OperationCanceledException)when(stop?.IsCancellationRequested==true){ }catch(Exception error){lock(_gate)_failure??=error;}
            Attempt? retained;lock(_gate)retained=_retained;
            if(retained is not null){await RetireAsync(retained).ConfigureAwait(false);lock(_gate)_retained=null;}
            stop?.Dispose();lock(_gate){_runStop=null;_status=ProcessorWorkState.Stopped;_exitConfirmed=true;}
        }
        catch(Exception error){lock(_gate){_failure=error;_status=ProcessorWorkState.Faulted;}throw;}
    }
    private sealed record Acquisition(TakeoutImportInventoryWork? Work,TakeoutCompletedInventory? Completed);
    private sealed class Attempt
    {
        internal TakeoutImportInventoryWork? Work {get;set;}
        internal TakeoutAssociationCatalog? Catalog {get;set;}
        internal TakeoutSelectedInput? Input {get;set;}
        internal bool Completed {get;set;}
        internal bool Returned {get;set;}
        internal void ReleaseInput(){Input?.Dispose();Input=null;}
    }
}
