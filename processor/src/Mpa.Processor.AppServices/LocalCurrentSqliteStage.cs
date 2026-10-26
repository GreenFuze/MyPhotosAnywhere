using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Joined private canonical preparation. The main state owner admits only
/// bounded claims/checkpoints; native file writes and whole-file hashing run outside
/// that queue. A sealed result is historical preparation, never an active catalog or
/// permission to write originals. The final Root fence and atomic admission follow.</summary>
public sealed class LocalCurrentSqliteStage : ILocalCurrentCatalogStage,ILocalCurrentIndexOwner
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly LocalCurrentControlCapture _capture;
    private readonly CurrentCatalogStageArea _area;
    private readonly CurrentCatalogStageIndex _index;
    private readonly bool _ownsArea;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private CurrentCatalogStageClaim _claim;
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}

    private LocalCurrentSqliteStage(ProcessorStateCoordinator state,StorageBudgetManager budget,LocalCurrentControlCapture capture,
        CurrentCatalogStageClaim claim,CurrentCatalogStageArea area,CurrentCatalogStageIndex index,bool ownsArea=true)
    {_state=state;_budget=budget;_capture=capture;_claim=claim;_area=area;_index=index;_ownsArea=ownsArea;}

    internal static LocalCurrentSqliteStage OpenBorrowed(ProcessorStateCoordinator state,StorageBudgetManager budget,LocalCurrentControlCapture capture,
        CurrentCatalogStageClaim claim,CurrentCatalogStageArea area,CancellationToken token)
    {
        var index=area.Open(claim,budget,token);
        try {token.ThrowIfCancellationRequested();return new(state,budget,capture,claim,area,index,ownsArea:false);}
        catch {index.Dispose();throw;}
    }

    public static async Task<LocalCurrentSqliteStage> CreateAsync(ProcessorStateCoordinator state,StorageBudgetManager budget,
        LocalCurrentControlCapture capture,Guid id,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(budget);ArgumentNullException.ThrowIfNull(capture);
        var prepared=await state.ExecuteAsync(database=>
        {
            cancellationToken.ThrowIfCancellationRequested();var journal=new CurrentCatalogStageJournal(database,budget);
            var claim=journal.Begin(capture.Attempt,id,cancellationToken);
            return (Claim:claim,Claims:journal.ReadAll(),database.StateDirectory,database.InstallationId);
        },cancellationToken).ConfigureAwait(false);
        // No timeout/WaitAsync wrapper may abandon a live SQLite/file owner.
        return await Task.Run(()=>
        {
            cancellationToken.ThrowIfCancellationRequested();var area=new CurrentCatalogStageArea(prepared.StateDirectory,prepared.InstallationId,prepared.Claims,budget);
            CurrentCatalogStageIndex? index=null;
            try
            {
                index=area.Open(prepared.Claim,budget,cancellationToken);cancellationToken.ThrowIfCancellationRequested();
                return new LocalCurrentSqliteStage(state,budget,capture,prepared.Claim,area,index);
            }
            catch {index?.Dispose();area.Dispose();throw;}
        },CancellationToken.None).ConfigureAwait(false);
    }

    public Task AddAsync(CatalogFactsLeaf leaf,CancellationToken cancellationToken)=>OperationAsync(async token=>
    {
        ArgumentNullException.ThrowIfNull(leaf);await AssertCapturedAsync(token).ConfigureAwait(false);
        await Task.Run(()=>_index.Add(leaf,token),CancellationToken.None).ConfigureAwait(false);
        await AssertCapturedAsync(token).ConfigureAwait(false);return true;
    },cancellationToken);

    public Task<CurrentCatalogStageDescriptor> SealAsync(LocalCurrentControlGraph graph,CancellationToken cancellationToken)=>OperationAsync(async token=>
    {
        ArgumentNullException.ThrowIfNull(graph);
        if(!ReferenceEquals(graph.Stage,this) || !ReferenceEquals(graph.Policies.Capture,_capture)) throw new CurrentControlObservationConflictException();
        await AssertCapturedAsync(token).ConfigureAwait(false);
        var descriptor=await Task.Run(()=>_index.Seal(graph.Assets,token),CancellationToken.None).ConfigureAwait(false);
        _claim=await _state.ExecuteAsync(database=>new CurrentCatalogStageJournal(database,_budget).RecordSealed(_claim,_capture.Attempt,descriptor,token),token).ConfigureAwait(false);
        return descriptor;
    },cancellationToken);

    /// <summary>Read the sealed historical projection. This does not refresh authority.</summary>
    public Task<CurrentCatalogStagePage> ReadPageAsync(CatalogCursor? after,int limit,CancellationToken cancellationToken)=>
        OperationAsync(token=>Task.Run(()=>_index.ReadPage(after,limit,token),CancellationToken.None),cancellationToken);

    /// <summary>Keep the actual sealed native file lease joined throughout the
    /// remaining inventory and final Root observation. Stage disposal cancels and
    /// joins that work rather than releasing its file while a scan still borrows it.</summary>
    internal Task<T> UseSealedCurrentGraphAsync<T>(ProcessorStateCoordinator state,LocalCurrentControlGraph graph,
        Func<CancellationToken,Task<T>> action,CancellationToken cancellationToken)=>OperationAsync(async token=>
    {
        ArgumentNullException.ThrowIfNull(action);
        if(!ReferenceEquals(state,_state) || !ReferenceEquals(graph.Stage,this) || !ReferenceEquals(graph.Policies.Capture,_capture) ||
            _claim.Phase!="sealed" || _claim.Descriptor is null || _claim.Descriptor.Assets!=graph.Assets)
            throw new CurrentControlObservationConflictException();
        await AssertCapturedAsync(token).ConfigureAwait(false);
        return await action(token).ConfigureAwait(false);
    },cancellationToken);

    internal Task<T> UseSealedGraphAsync<T>(ProcessorStateCoordinator state,LocalCurrentGraphFence fence,
        Func<CurrentCatalogStageClaim,CancellationToken,Task<T>> action,CancellationToken cancellationToken)=>OperationAsync(async token=>
    {
        if(!ReferenceEquals(state,_state) || !ReferenceEquals(fence.Graph.Stage,this) || !ReferenceEquals(fence.Graph.Policies.Capture,_capture) ||
            !ReferenceEquals(fence.Current.Capture,_capture) || _claim.Phase!="sealed" || _claim.Descriptor is null || _claim.Descriptor.Assets!=fence.Graph.Assets)
            throw new CurrentControlObservationConflictException();
        // The serialized stage owner keeps its verified immutable file lease alive
        // through the caller's entire main-state transaction.
        return await action(_claim,token).ConfigureAwait(false);
    },cancellationToken);

    private Task<bool> AssertCapturedAsync(CancellationToken token)=>_state.ExecuteAsync(database=>
    {new CurrentCatalogStageJournal(database,_budget).AssertForCapture(_claim,_capture.Attempt,token);return true;},token);

    private async Task<T> OperationAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken caller)
    {
        CancellationTokenSource linked;
        lock(_gate) {ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                Check(linked.Token);var result=await action(linked.Token).ConfigureAwait(false);
                lock(_gate) {Check(linked.Token);return result;}
            }
            finally {_serial.Release();}
        }
    }
    private void Check(CancellationToken token)
    {lock(_gate) if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Current staging retired before admission.",token);}
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            _stopping=true;
            if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;
            return new(_disposal ??=StopAsync());
        }
    }
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){cancellationFailure=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if(cancellationFailure is not null) throw new AggregateException("Staging cancellation needs attention; retain borrowed owners.",cancellationFailure);
            await Task.Run(()=>{_index.Dispose();if(_ownsArea) _area.Dispose();},CancellationToken.None).ConfigureAwait(false);
            _stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        finally {_serial.Release();}
    }
}
