using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed class CurrentCanonicalPositionChangedException() : InvalidOperationException(
    "The shared photo catalog changed. Restore the visible photo before continuing.");
public sealed record CurrentCanonicalReadPage(CurrentCatalogActivation Selection,CurrentCatalogStagePage Page);
public sealed record CurrentCanonicalReadAsset(CurrentCatalogActivation Selection,DriveCatalogAsset? Asset);

/// <summary>Owns opened canonical handles; borrows the actual reader, shared two-
/// index pool and serialized state. Historical queries use a separate lane from
/// network refresh. Join this owner before its borrowed owners. No publication or
/// original-media permission can be obtained from historical content.</summary>
public sealed class CurrentCanonicalReadRuntime(ProcessorStateCoordinator state,StorageBudgetManager budget,
    LocalCurrentControlReader reader,LocalCurrentStagePool pool) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state=state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly LocalCurrentControlReader _reader=reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly LocalCurrentStagePool _pool=pool ?? throw new ArgumentNullException(nameof(pool));
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly TaskCompletionSource _queriesDrained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LocalHistoricalCatalog? _active,_opening,_retiring;
    private LocalCurrentSqliteStage? _candidate;
    private bool _stopping,_exitConfirmed;
    private int _queries;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure;}}
    public CurrentCatalogActivation? Selection {get {lock(_gate) return _active?.Selection;}}
    internal bool BorrowsPublicationOwners(ProcessorStateCoordinator state,StorageBudgetManager budget,
        LocalCurrentControlReader reader,LocalCurrentStagePool pool)=>
        ReferenceEquals(state,_state) && ReferenceEquals(budget,_budget) && ReferenceEquals(reader,_reader) && ReferenceEquals(pool,_pool);


    public Task<CurrentCatalogActivation?> InitializeHistoricalAsync(CancellationToken caller)=>OperationAsync(async token=>
    {
        await RecoverActiveAsync(token).ConfigureAwait(false);
        await RetireInactiveAsync(token).ConfigureAwait(false);
        Check(token);return Selection;
    },caller);

    public Task<CurrentCatalogActivation> RefreshAsync(CancellationToken caller)=>OperationAsync(async token=>
    {
        await RecoverActiveAsync(token).ConfigureAwait(false);
        await RetireInactiveAsync(token).ConfigureAwait(false);
        var capture=await _reader.CaptureAsync(token).ConfigureAwait(false);
        var policies=await _reader.ReadSelectedPoliciesAsync(capture,token).ConfigureAwait(false);
        _candidate=await _pool.OpenAsync(capture,Guid.NewGuid(),token).ConfigureAwait(false);
        Check(token);
        var graph=await _reader.ReadSelectedGraphAsync(policies,_candidate,token).ConfigureAwait(false);
        _=await _candidate.SealAsync(graph,token).ConfigureAwait(false);
        var observed=await _reader.ObserveSelectedInventoryAsync(graph,token).ConfigureAwait(false);
        var activation=await _reader.ActivateReconciledAsync(observed,_candidate,token).ConfigureAwait(false);
        // The old historical reader stays live until the acknowledged candidate
        // has closed and its exact read-only replacement has actually reopened.
        await CloseCandidateAsync().ConfigureAwait(false);
        await RecoverActiveAsync(token).ConfigureAwait(false);
        Check(token);
        if(Selection!=activation) throw new CurrentControlObservationConflictException();
        await RetireInactiveAsync(token).ConfigureAwait(false);
        Check(token);return activation;
    },caller);

    public Task<CurrentCanonicalReadPage?> ReadPageAsync(Guid? expectedStageId,CatalogCursor? after,int limit,CancellationToken caller)
    {
        ValidatePage(expectedStageId,limit);
        if(expectedStageId is null && after is not null) throw new ArgumentException("A continuation requires its exact catalog context.");
        return ReadCatalogAsync(expectedStageId,async (active,token)=>
            new CurrentCanonicalReadPage(active.Selection,await active.ReadPageAsync(after,limit,token).ConfigureAwait(false)),caller);
    }

    /// <summary>Previous entries are returned in normal timeline order. Preserve
    /// this selection context and use the first returned entry to continue back.</summary>
    public Task<CurrentCanonicalReadPage?> ReadPreviousPageAsync(Guid expectedStageId,CatalogCursor before,int limit,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(before);ValidatePage(expectedStageId,limit);
        if(before.Id==Guid.Empty || before.EffectiveUtc is < -62135596800000L or >253402300799999L)
            throw new ArgumentException("A valid previous-page identity and date are required.");
        return ReadCatalogAsync(expectedStageId,async (active,token)=>
            new CurrentCanonicalReadPage(active.Selection,await active.ReadPreviousPageAsync(before,limit,token).ConfigureAwait(false)),caller);
    }

    /// <summary>Inclusive UTC milliseconds; the caller converts its own local-day
    /// boundary. No new current-authority or publication claim is made by seeking.</summary>
    public Task<CurrentCanonicalReadPage?> ReadAtOrBeforeAsync(Guid? expectedStageId,long utcMilliseconds,int limit,CancellationToken caller)
    {
        ValidatePage(expectedStageId,limit);
        if(utcMilliseconds is < -62135596800000L or >253402300799999L) throw new ArgumentException("A supported UTC date is required.");
        return ReadCatalogAsync(expectedStageId,async (active,token)=>
            new CurrentCanonicalReadPage(active.Selection,await active.ReadAtOrBeforeAsync(utcMilliseconds,limit,token).ConfigureAwait(false)),caller);
    }

    /// <summary>Returns the exact historical selection even when the identity is
    /// absent. A missing photo is never replaced by a neighboring entry.</summary>
    public Task<CurrentCanonicalReadAsset?> FindAssetAsync(Guid expectedStageId,Guid assetId,CancellationToken caller)
    {
        if(expectedStageId==Guid.Empty || assetId==Guid.Empty) throw new ArgumentException("Exact catalog and photo identities are required.");
        return ReadCatalogAsync(expectedStageId,async (active,token)=>
            new CurrentCanonicalReadAsset(active.Selection,await active.FindAssetAsync(assetId,token).ConfigureAwait(false)),caller);
    }

    private static void ValidatePage(Guid? expectedStageId,int limit)
    {
        if(limit is <1 or >90 || expectedStageId==Guid.Empty) throw new ArgumentException("A bounded page and valid catalog context are required.");
    }

    private async Task<T?> ReadCatalogAsync<T>(Guid? expectedStageId,Func<LocalHistoricalCatalog,CancellationToken,Task<T>> query,CancellationToken caller) where T:class
    {
        LocalHistoricalCatalog? active;CancellationTokenSource linked;
        lock(_gate)
        {
            AssertRunning();caller.ThrowIfCancellationRequested();active=_active;
            if(expectedStageId is not null && active?.Selection.StageId!=expectedStageId)
                throw new CurrentCanonicalPositionChangedException();
            if(active is null) return null;
            var admitted=checked(_queries+1);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);_queries=admitted;
        }
        using(linked)
        {
            try
            {
                var result=await query(active,linked.Token).ConfigureAwait(false);
                Check(linked.Token);return result;
            }
            finally {lock(_gate) {_queries--;if(_stopping && _queries==0) _queriesDrained.TrySetResult();}}
        }
    }

    private async Task RecoverActiveAsync(CancellationToken token)
    {
        await CloseCandidateAsync().ConfigureAwait(false);
        await CloseOpeningAsync().ConfigureAwait(false);
        await CloseRetiringAsync().ConfigureAwait(false);
        var selected=await _state.ExecuteAsync(database=>new CurrentCatalogActivationRepository(database,_budget).Read(),token).ConfigureAwait(false);
        CurrentCatalogActivation? previous;lock(_gate) previous=_active?.Selection;
        if(selected==previous) return;
        if(selected is null || selected.StageId==previous?.StageId)
            throw new InvalidDataException("The active canonical receipt disappeared or changed its retained identity.");
        _opening=await _pool.OpenHistoricalAsync(token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Acknowledged canonical content lost its historical owner.");
        if(_opening.Selection!=selected) throw new CurrentControlObservationConflictException();
        lock(_gate) {Check(token);_retiring=_active;_active=_opening;_opening=null;}
        await CloseRetiringAsync().ConfigureAwait(false);
    }
    private async Task RetireInactiveAsync(CancellationToken token)
    {
        var retained=await _state.ExecuteAsync(database=>
        {
            var selection=new CurrentCatalogActivationRepository(database,_budget).Read();
            return (Selection:selection,Claims:new CurrentCatalogStageJournal(database,_budget).ReadAll());
        },token).ConfigureAwait(false);
        if(retained.Selection!=Selection) throw new CurrentControlObservationConflictException();
        foreach(var claim in retained.Claims)
        {
            Check(token);if(claim.Id==retained.Selection?.StageId) continue;
            await _pool.RetireAsync(claim.Id,token).ConfigureAwait(false);
        }
    }
    private async Task CloseCandidateAsync()
    {
        if(_candidate is null) return;
        try {await _candidate.DisposeAsync().ConfigureAwait(false);}catch(Exception error) {Retain(error);throw;}
        if(!_candidate.ExitConfirmed) throw Retain(new InvalidOperationException("The canonical candidate has not physically joined."));
        _candidate=null;
    }
    private async Task CloseOpeningAsync()
    {
        if(_opening is null) return;
        try {await _opening.DisposeAsync().ConfigureAwait(false);}catch(Exception error) {Retain(error);throw;}
        if(!_opening.ExitConfirmed) throw Retain(new InvalidOperationException("The opening canonical reader has not physically joined."));
        _opening=null;
    }
    private async Task CloseRetiringAsync()
    {
        if(_retiring is null) return;
        try {await _retiring.DisposeAsync().ConfigureAwait(false);}catch(Exception error) {Retain(error);throw;}
        if(!_retiring.ExitConfirmed) throw Retain(new InvalidOperationException("The displaced canonical reader has not physically joined."));
        _retiring=null;
    }
    private Exception Retain(Exception error)
    {lock(_gate) {_failure=_failure is null ? error : ReferenceEquals(_failure,error) ? _failure : new AggregateException(_failure,error);return _failure;}}
    private async Task<T> OperationAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try {Check(linked.Token);return await action(linked.Token).ConfigureAwait(false);}
            catch(Exception error)
            {
                var failures=new List<Exception> {error};
                try {await CloseCandidateAsync().ConfigureAwait(false);}catch(Exception cleanup){failures.Add(cleanup);}
                try {await CloseOpeningAsync().ConfigureAwait(false);}catch(Exception cleanup){failures.Add(cleanup);}
                if(failures.Count>1) throw new AggregateException("Canonical refresh and joined cleanup failed; retain borrowed owners.",failures);
                throw;
            }
            finally {_serial.Release();}
        }
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping,this);
        if(_failure is not null) throw new InvalidOperationException("Canonical retirement is unconfirmed; retain its actual owners.",_failure);
    }
    private void Check(CancellationToken token)
    {lock(_gate) {token.ThrowIfCancellationRequested();AssertRunning();}}
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            _stopping=true;if(_queries==0) _queriesDrained.TrySetResult();
            if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;
            return new(_disposal ??=StopAsync());
        }
    }
    private async Task StopAsync()
    {
        var failures=new List<Exception>();
        try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await _queriesDrained.Task.ConfigureAwait(false);
            try {await CloseCandidateAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
            try {await CloseOpeningAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
            try {await CloseRetiringAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
            LocalHistoricalCatalog? active;lock(_gate) active=_active;
            if(active is not null)
            {
                try {await active.DisposeAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
                if(active.ExitConfirmed) lock(_gate) _active=null;
                else failures.Add(new InvalidOperationException("The active canonical reader has not physically joined."));
            }
            if(failures.Count!=0) throw Retain(new AggregateException("Canonical runtime retirement failed; borrowed owners remain retained.",failures));
            _stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        finally {_serial.Release();}
    }
}
