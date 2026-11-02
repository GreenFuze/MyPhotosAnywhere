using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Engine-owned folder review and explicit import queue. Browser requests
/// own selection only; captured reviews/Start requests survive browser closure.
/// Shares the existing current-library lane and borrowed inventory owner.</summary>
public sealed class TakeoutDesktopImportRuntime(ProcessorStateCoordinator state,StorageBudgetManager budget,
    TakeoutInventoryService inventory,CurrentLibraryCatalogRuntime current,Func<CancellationToken,Task<string?>>? picker,Action? discoveryReady=null,TakeoutImportMetadataRunner? metadata=null)
    : IProcessorWorkControl,IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state=state??throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget=budget??throw new ArgumentNullException(nameof(budget));
    private readonly TakeoutInventoryService _inventory=inventory??throw new ArgumentNullException(nameof(inventory));
    private readonly CurrentLibraryCatalogRuntime _current=current??throw new ArgumentNullException(nameof(current));
    private readonly Func<CancellationToken,Task<string?>>? _picker=picker;
    private readonly Action? _discoveryReady=discoveryReady;
    private readonly TakeoutImportMetadataRunner? _metadata=metadata;
    private readonly object _gate=new();
    private readonly CancellationTokenSource _stop=new();
    private CancellationTokenSource? _runStop;
    private Task? _run,_pick,_pause,_disposal;
    private TakeoutAssociationCatalog? _catalog;
    private TakeoutOriginalReader? _source;
    private ProcessorWorkState _status=ProcessorWorkState.Paused;
    private bool _stopping,_exitConfirmed,_draining;
    private long _requests;
    private Exception? _failure;
    public bool CanChooseFolder=>_picker is not null;
    public ProcessorWorkState State {get{lock(_gate)return _status;}}
    public bool ExitConfirmed {get{lock(_gate)return _exitConfirmed;}}
    public Exception? Failure {get{lock(_gate)return _failure;}}
    public event Action<Exception>? Failed;

    public Task<IReadOnlyList<TakeoutImportActivity>> ListAsync(CancellationToken token)=>_state.ExecuteAsync(database=>
    {var repository=new TakeoutImportInventoryRepository(database);return(IReadOnlyList<TakeoutImportActivity>)repository.Identities().Select(repository.Activity).ToArray();},token);
    public Task<TakeoutImportActivity?> ChooseFolderAsync(Guid sourceId,CancellationToken caller)
    {
        if(sourceId==Guid.Empty)throw new ArgumentException("Choose an existing Drive source.",nameof(sourceId));
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping,this);
            if(_picker is null)throw new InvalidOperationException("Choose the input on the desktop computer.");
            if(_pick is{IsCompleted:false})throw new ProcessorWorkStateConflictException();
            caller.ThrowIfCancellationRequested();var operation=Task.Run(()=>ChooseOwnedAsync(sourceId,caller),CancellationToken.None);_pick=operation;return operation;
        }
    }
    private async Task<TakeoutImportActivity?> ChooseOwnedAsync(Guid sourceId,CancellationToken caller)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);
        var target=await _state.ExecuteAsync(database=>
        {
            Check(caller,linked.Token);var snapshot=new LibrarySourceRepository(database).Snapshot();
            var source=snapshot.Sources.SingleOrDefault(value=>value.Id==sourceId&&value.Enabled&&value.AccountConnected)??throw new StaleLibraryConfigurationException();
            var account=new DriveAccountRepository(database).Accounts().Single(value=>value.PermissionId==source.AccountId&&value.Connected);
            var library=new LibraryAuthorityRepository(database,_budget).Controller()??throw new InvalidOperationException("Finish library setup before importing photos.");
            return new TakeoutImportTarget(library.LibraryId,source.AccountId,account.Revision,source.FolderId,snapshot.Revision);
        },linked.Token).ConfigureAwait(false);
        var path=await _picker!(linked.Token).ConfigureAwait(false);Check(caller,linked.Token);if(path is null)return null;
        using var selected=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),path);var binding=selected.Binding;var importId=Guid.NewGuid();
        var result=await _state.ExecuteAsync(database=>
        {
            lock(_gate)
            {
                Check(caller,linked.Token);var snapshot=new LibrarySourceRepository(database).Snapshot();
                if(snapshot.Revision!=target.ConfigurationRevision||!snapshot.Sources.Any(value=>value.Id==sourceId&&value.AccountId==target.AccountId&&value.FolderId==target.FolderId&&value.Enabled&&value.AccountConnected)||
                    !new DriveAccountRepository(database).Accounts().Any(value=>value.PermissionId==target.AccountId&&value.Revision==target.AccountRevision&&value.Connected))throw new StaleLibraryConfigurationException();
                var repository=new TakeoutImportInventoryRepository(database);if(repository.Identities().Count==64)throw new InvalidOperationException("Retained import capacity reached; review the existing imports first.");
                repository.Ensure(importId,target,[binding]);Wake();return repository.Activity(importId);
            }
        },linked.Token).ConfigureAwait(false);
        // Admission above is durable. Caller/browser cancellation after that point
        // must not abandon the engine's review, even if this reply is lost.
        return result;
    }
    public async Task<TakeoutImportActivity> StartAsync(Guid importId,CancellationToken caller)
    {
        return await _state.ExecuteAsync(database=>
        {
            lock(_gate){Check(caller,caller);var repository=new TakeoutImportInventoryRepository(database);repository.RequestOriginals(importId);Wake();return repository.Activity(importId);}
        },caller).ConfigureAwait(false);
    }
    public Task<TakeoutImportActivity> RetryAsync(Guid importId,CancellationToken caller)=>_state.ExecuteAsync(database=>
    {
        lock(_gate){Check(caller,caller);var repository=new TakeoutImportInventoryRepository(database);_=repository.Activity(importId);repository.ClearIssue(importId);Wake();return repository.Activity(importId);}
    },caller);
    private void Wake()
    {
        _requests=checked(_requests+1);if(_stopping||_status is ProcessorWorkState.Paused or ProcessorWorkState.Pausing||_draining)return;
        if(_failure is not null)throw new InvalidOperationException("Import ownership needs attention before further work.",_failure);
        _runStop?.Dispose();_runStop=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);var token=_runStop.Token;
        var previous=_run;_draining=true;_status=ProcessorWorkState.Running;
        _run=Task.Run(async()=>{if(previous is not null)await previous.ConfigureAwait(false);await DrainAsync(token).ConfigureAwait(false);},CancellationToken.None);
    }
    private async Task DrainAsync(CancellationToken token)
    {
        try
        {
            while(true)
            {
                long version;lock(_gate){Check(token,token);version=_requests;}
                var imports=await ListAsync(token).ConfigureAwait(false);
                foreach(var item in imports.Where(value=>value.State is "reviewing" or "copying" or "originals-verified"))
                {
                    Check(token,token);
                    try
                    {
                        if(item.State!="originals-verified")
                        {
                            var completed=await _inventory.RunAsync(item.Id,token).ConfigureAwait(false);
                            var activity=await _state.ExecuteAsync(database=>new TakeoutImportInventoryRepository(database).Activity(item.Id),token).ConfigureAwait(false);
                            if(!activity.Requested)continue;
                            await CopyAsync(completed,token).ConfigureAwait(false);
                        }
                        var scheduled=await _state.ExecuteAsync(database=>new SourceDiscoveryRepository(database).EnsureImportedOriginalsScheduled(item.Id),token).ConfigureAwait(false);
                        if(scheduled.Created)_discoveryReady?.Invoke();
                        if(_metadata is not null)
                        {
                            var completed=await _state.ExecuteAsync(database=>
                            {
                                var repository=new TakeoutImportInventoryRepository(database);
                                return repository.Activity(item.Id).Metadata is{Finished:true}?null:repository.ReadCompleted(item.Id)??throw new InvalidDataException("The reviewed metadata input disappeared.");
                            },token).ConfigureAwait(false);
                            if(completed is not null)await _metadata.RunAsync(completed,token).ConfigureAwait(false);
                        }
                    }
                    catch(Exception error)when(error is HttpRequestException or StaleLibraryConfigurationException or StaleDriveConnectionException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or TakeoutMetadataCapacityException)
                    {
                        var issue=error is TakeoutMetadataCapacityException?"metadata-capacity":error is HttpRequestException?"drive-unavailable":error is FileNotFoundException or DirectoryNotFoundException?"input-unavailable":"permission-changed";
                        await _state.ExecuteAsync(database=>{new TakeoutImportInventoryRepository(database).RecordIssue(item.Id,issue);return true;},CancellationToken.None).ConfigureAwait(false);
                    }
                }
                lock(_gate){if(_requests==version){_draining=false;if(_status==ProcessorWorkState.Running)_status=ProcessorWorkState.Ready;return;}}
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested||_stopping||_status==ProcessorWorkState.Pausing){lock(_gate)_draining=false;}
        catch(Exception error)
        {
            lock(_gate){_failure=error;_draining=false;_status=ProcessorWorkState.Faulted;}
            Failed?.Invoke(error);throw;
        }
    }
    private async Task CopyAsync(TakeoutCompletedInventory completed,CancellationToken token)
    {
        try
        {
            var approval=await _current.ApproveTakeoutImportAsync(completed,token).ConfigureAwait(false);
            _catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,_budget,token).ConfigureAwait(false);
            string? after=null;
            while(true)
            {
                var page=_catalog.ReadPage(TakeoutEntryKind.Media,after,token:token);
                foreach(var entry in page.Entries)
                {
                    Check(token,token);var id=TakeoutOriginalJobRepository.Identity(completed.ImportId,entry.Key);
                    if(await _state.ExecuteAsync(database=>new TakeoutOriginalJobRepository(database).IsVerified(id),token).ConfigureAwait(false))continue;
                    _source=await TakeoutOriginalReader.CaptureAsync(completed,_catalog,entry,token).ConfigureAwait(false);
                    var job=await _state.ExecuteAsync(database=>new TakeoutOriginalJobRepository(database).Ensure(_source.Capture,approval.Id),token).ConfigureAwait(false);
                    await _current.UploadTakeoutOriginalAsync(job,completed,_catalog,_source,token).ConfigureAwait(false);
                    await _source.DisposeAsync().ConfigureAwait(false);_source=null;
                }
                if(!page.HasMore)break;after=page.Entries[^1].Key;
            }
        }
        finally {await RetireInputsAsync().ConfigureAwait(false);}
    }
    private async Task RetireInputsAsync()
    {if(_source is{}source){await source.DisposeAsync().ConfigureAwait(false);_source=null;}if(_catalog is{}catalog){catalog.Dispose();_catalog=null;}}
    private void Check(CancellationToken caller,CancellationToken token)
    {lock(_gate){caller.ThrowIfCancellationRequested();token.ThrowIfCancellationRequested();if(_stopping)throw new OperationCanceledException("Import owner closed admission.",token);}}
    public Task PauseAsync()
    {
        lock(_gate)
        {
            if(_status==ProcessorWorkState.Paused)return Task.CompletedTask;if(_status==ProcessorWorkState.Pausing)return _pause!;
            if(_stopping||_status is not(ProcessorWorkState.Ready or ProcessorWorkState.Running))throw new ProcessorWorkStateConflictException();
            _status=ProcessorWorkState.Pausing;var cancellation=_runStop?.CancelAsync()??Task.CompletedTask;return _pause=Task.Run(async()=>
            {
                var run=_run;await cancellation.ConfigureAwait(false);if(run is not null)await run.ConfigureAwait(false);
                lock(_gate){if(_failure is not null)throw new InvalidOperationException("Import retirement needs attention.",_failure);if(!_stopping)_status=ProcessorWorkState.Paused;}
            },CancellationToken.None);
        }
    }
    public Task ResumeAsync()
    {lock(_gate){ObjectDisposedException.ThrowIf(_stopping,this);if(_status!=ProcessorWorkState.Paused||_source is not null||_catalog is not null)throw new ProcessorWorkStateConflictException();_status=ProcessorWorkState.Ready;Wake();return Task.CompletedTask;}}
    public ValueTask DisposeAsync()
    {lock(_gate){_stopping=true;_status=ProcessorWorkState.Stopping;if(_disposal is{IsFaulted:true}&&!_exitConfirmed)_disposal=null;return new(_disposal??=StopAsync());}}
    public Task CloseAdmissionAsync()
    {lock(_gate){if(_exitConfirmed)return Task.CompletedTask;_stopping=true;_status=ProcessorWorkState.Stopping;return _stop.CancelAsync();}}
    private async Task StopAsync()
    {
        var failures=new List<Exception>();try{await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
        Task? run,pick,pause;lock(_gate){run=_run;pick=_pick;pause=_pause;}
        foreach(var task in new[]{run,pick,pause}.OfType<Task>())
        {
            try{await task.ConfigureAwait(false);}
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){ }
            catch(Exception error){if(!ReferenceEquals(task,pick))failures.Add(error);}
        }
        await RetireInputsAsync().ConfigureAwait(false);_runStop?.Dispose();_stop.Dispose();lock(_gate){_exitConfirmed=true;_status=ProcessorWorkState.Stopped;}
        if(failures.Count!=0)throw new AggregateException("Import work retired after a recorded failure.",failures);
    }
}
