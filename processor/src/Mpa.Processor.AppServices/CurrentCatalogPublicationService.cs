using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>One joined outgoing current generation: original header preparation,
/// actual metadata allocation/upload, fixed Root publication, canonical admission
/// and acknowledged private-snapshot retirement. Owns its four semantic children;
/// borrows the existing reader/canonical runtime/shared two-index pool. No original
/// image write is authorized. An existing Root plan resumes before header work.</summary>
public sealed class CurrentCatalogPublicationService : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly LocalCurrentControlReader _reader;
    private readonly CurrentCanonicalReadRuntime _canonical;
    private readonly CurrentCatalogHeaderPreparationService _header;
    private readonly CurrentCatalogHeaderAllocationService _allocation;
    private readonly CurrentCatalogHeaderUploadService _upload;
    private readonly CurrentCatalogGenerationCompletionService _completion;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure??_header.Failure??_allocation.Failure??_upload.Failure??_completion.Failure;}}

    public CurrentCatalogPublicationService(ProcessorStateCoordinator state,string directory,Guid installationId,
        ProcessorAccountService accounts,StorageBudgetManager budget,LocalCurrentControlReader reader,
        CurrentCanonicalReadRuntime canonical,LocalCurrentStagePool pool)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(accounts);ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(reader);ArgumentNullException.ThrowIfNull(canonical);ArgumentNullException.ThrowIfNull(pool);
        if(!reader.BorrowsDependencyAllocationOwners(state,accounts,budget) || !pool.BorrowsPublicationOwners(state,budget) ||
            !canonical.BorrowsPublicationOwners(state,budget,reader,pool))
            throw new InvalidOperationException("Current publication must borrow the exact reader, state, account, budget and canonical pool owners.");
        _state=state;_budget=budget;_reader=reader;_canonical=canonical;
        _header=new(state,directory,installationId,accounts,budget,reader);
        _allocation=new(state,accounts,budget,reader);_upload=new(accounts,reader);_completion=new(state,budget);
    }

    public async Task<PublishedCatalogHead> PublishAsync(CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock(_gate) AssertRunning();var token=linked.Token;Check(token);
                var progress=await ReadProgressAsync(token).ConfigureAwait(false);
                if(progress.Capture is {} complete && (progress.Selected || complete.Phase is "complete" or "snapshot-retiring"))
                    return await FinishAsync(complete.Scope.GenerationId,token).ConfigureAwait(false);
                WorkspacePublicationRecord? header=progress.Header;
                var plan=progress.Transition;
                if(plan is null)
                {
                    header=await _header.PrepareAsync(token).ConfigureAwait(false);Check(token);
                    progress=await ReadProgressAsync(token).ConfigureAwait(false);
                    if(progress.Capture is not {} original || original.Scope!=header.Scope || header.Id!=original.CommitIntentId)
                        throw new CatalogGenerationCaptureConflictException();
                    if(progress.Selected) return await FinishAsync(original.Scope.GenerationId,token).ConfigureAwait(false);
                    plan=progress.Transition;
                    if(plan is null)
                    {
                    var p0=await ObserveHeaderAsync(header.Scope.GenerationId,token).ConfigureAwait(false);
                    var assigned=await _allocation.AllocateAsync(p0,token).ConfigureAwait(false);
                    if(assigned.Id!=header.Id || assigned.Scope!=header.Scope) throw new WorkspacePublicationConflictException();
                    // A real assignment spends P0. The uploader gets its own actual
                    // current scan, including an exact already-assigned retry.
                    var p1=await ObserveHeaderAsync(header.Scope.GenerationId,token).ConfigureAwait(false);
                    header=await _upload.UploadAsync(p1,token).ConfigureAwait(false);
                    if(header.FileId is null || header.VerifiedVersion is null) throw new WorkspacePublicationConflictException();
                    var capture=await _reader.CaptureAsync(token).ConfigureAwait(false);
                    var inventory=await _reader.ObserveWorkspaceInventorySessionAsync(capture,token).ConfigureAwait(false);
                    var prepared=await _reader.PrepareGenerationRootAsync(inventory,header.Scope.GenerationId,token).ConfigureAwait(false);
                    if(prepared.Selected) return await FinishAsync(header.Scope.GenerationId,token).ConfigureAwait(false);
                    plan=prepared.Transition??throw new CatalogGenerationCaptureConflictException();
                    }
                }
                if(header is null || plan.GenerationId!=header.Scope.GenerationId || plan.Target.CatalogPublicationId!=header.Id ||
                    plan.Catalog is null || header.FileId!=plan.Catalog.FileId || header.Sha256!=plan.Catalog.Sha256 ||
                    header.VerifiedVersion is null) throw new CatalogGenerationCaptureConflictException();
                _=await _reader.PublishTransitionAsync(plan.Id,token).ConfigureAwait(false);
                // Transport acknowledgement is never canonical admission. Perform
                // actual selected traversal, staging, Root-last inventory and the
                // atomic pointer/selection/Root-terminal transaction before cleanup.
                var selected=await _canonical.RefreshAsync(token).ConfigureAwait(false);
                if(selected.Catalog!=plan.Catalog) throw new CurrentControlObservationConflictException();
                return await FinishAsync(header.Scope.GenerationId,token).ConfigureAwait(false);
            }
            finally {_serial.Release();}
        }
    }

    // A known already-selected generation can finish its private local cleanup
    // offline, including a lost acknowledgement after retirement. Historical
    // returned progress supplies no new publication or remote availability grant.
    public async Task<PublishedCatalogHead> ResumeSelectedAsync(Guid generation,CancellationToken caller)
    {
        if(generation==Guid.Empty) throw new ArgumentException("An original selected generation is required.",nameof(generation));
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try {lock(_gate) AssertRunning();return await FinishAsync(generation,linked.Token).ConfigureAwait(false);}
            finally {_serial.Release();}
        }
    }

    private async Task<PublishedCatalogHead> FinishAsync(Guid generation,CancellationToken token)
    {var result=await _completion.FinishAsync(generation,token).ConfigureAwait(false);lock(_gate) {Check(token);return result;}}
    private async Task<LocalCurrentCatalogHeaderCloudAdmission> ObserveHeaderAsync(Guid generation,CancellationToken token)
    {
        Check(token);var capture=await _reader.CaptureAsync(token).ConfigureAwait(false);
        var inventory=await _reader.ObserveWorkspaceInventorySessionAsync(capture,token).ConfigureAwait(false);
        return await _reader.InspectCatalogHeaderCloudAsync(inventory,generation,token).ConfigureAwait(false);
    }
    private Task<Progress> ReadProgressAsync(CancellationToken token)=>_state.ExecuteAsync(database=>
    {
        Check(token);var capture=new CatalogGenerationCaptureRepository(database,_budget).Active().SingleOrDefault();
        if(capture is null) return new Progress(null,null,null,false);
        var selected=new CurrentCatalogGenerationSelectionRepository(database,_budget).Read(capture.Scope.GenerationId,token) is not null;
        if(selected || capture.Phase is "complete" or "snapshot-retiring") return new Progress(capture,null,null,selected);
        var header=new WorkspacePublicationRepository(database,_budget).Read(capture.CommitIntentId);
        var plans=new CurrentControlTransitionJournal(database,_budget).ReadAll(token).Where(value=>value.GenerationId==capture.Scope.GenerationId).ToArray();
        if(plans.Length>1) throw new InvalidDataException("The original generation has multiple Root transitions; retain all work for explicit recovery.");
        var plan=plans.SingleOrDefault();
        if(plan is not null && (header is null || header.Id!=plan.Target.CatalogPublicationId || header.VerifiedVersion is null))
            throw new CatalogGenerationCaptureConflictException();
        return new Progress(capture,header,plan,false);
    },token);
    private sealed record Progress(CatalogGenerationCapture? Capture,WorkspacePublicationRecord? Header,CurrentControlTransitionPlan? Transition,bool Selected);
    private void AssertRunning()
    {ObjectDisposedException.ThrowIf(_stopping,this);if(_failure is not null) throw new InvalidOperationException("Current publication retirement is unconfirmed; retain borrowed owners.",_failure);}
    private void Check(CancellationToken token)
    {lock(_gate) if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Current publication retired before result admission.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is{IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        var errors=new List<Exception>();try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error) {errors.Add(error);}
        var stops=new List<Task>();
        Start(_header.DisposeAsync);Start(_allocation.DisposeAsync);Start(_upload.DisposeAsync);Start(_completion.DisposeAsync);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach(var task in stops) try {await task.ConfigureAwait(false);}catch(Exception error) {errors.Add(error);}
            if(!_header.ExitConfirmed || !_allocation.ExitConfirmed || !_upload.ExitConfirmed || !_completion.ExitConfirmed)
                errors.Add(new InvalidOperationException("Actual publication children have not all joined."));
            if(errors.Count!=0) throw new AggregateException("Current publication retirement needs attention; retain borrowed owners.",errors);
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        finally {_serial.Release();}
        void Start(Func<ValueTask> stop) {try {stops.Add(stop().AsTask());}catch(Exception error) {errors.Add(error);}}
    }
}
