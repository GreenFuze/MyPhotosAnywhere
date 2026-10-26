using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>One joined directory owner for active and pending private indexes.
/// Opening another candidate leaves historical readers alive. No active pointer,
/// Root authority, current publication or original-write permission is implied.</summary>
public sealed partial class LocalCurrentStagePool(ProcessorStateCoordinator state,StorageBudgetManager budget) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state=state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly Dictionary<Guid,ILocalCurrentIndexOwner> _stages=[];
    private CurrentCatalogStageArea? _area;
    private string? _directory;
    private Guid _installation;
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}

    public Task<LocalCurrentSqliteStage> OpenAsync(LocalCurrentControlCapture capture,Guid id,CancellationToken cancellationToken)=>OperationAsync(async token=>
    {
        ArgumentNullException.ThrowIfNull(capture);SweepClosed();
        if(_stages.ContainsKey(id)) throw new InvalidOperationException("Close the same pending index before reopening its capture; historical readers of other indexes remain owned.");
        var prepared=await _state.ExecuteAsync(database=>
        {
            var journal=new CurrentCatalogStageJournal(database,_budget);var claim=journal.Begin(capture.Attempt,id,token);
            return new Prepared(claim,journal.ReadAll(),database.StateDirectory,database.InstallationId);
        },token).ConfigureAwait(false);
        await RefreshAreaAsync(prepared,token).ConfigureAwait(false);
        var stage=await Task.Run(()=>LocalCurrentSqliteStage.OpenBorrowed(_state,_budget,capture,prepared.Claim,_area!,token),CancellationToken.None).ConfigureAwait(false);
        _stages.Add(id,stage);
        try {Check(token);return stage;}
        catch {await stage.DisposeAsync().ConfigureAwait(false);throw;}
    },cancellationToken);

    /// <summary>Retire only a caller-closed inactive candidate. Unknown or corrupt
    /// active receipts still block retirement through the journal's decoder.</summary>
    public Task RetireAsync(Guid id,CancellationToken cancellationToken)=>OperationAsync(async token=>
    {
        SweepClosed();if(_stages.ContainsKey(id)) throw new InvalidOperationException("Join the index before requesting its retirement.");
        var prepared=await _state.ExecuteAsync(database=>
        {
            var journal=new CurrentCatalogStageJournal(database,_budget);var claims=journal.ReadAll();
            var claim=claims.SingleOrDefault(value=>value.Id==id) ?? throw new InvalidOperationException("Retirement requires its durable claim.");
            return new Prepared(claim,claims,database.StateDirectory,database.InstallationId);
        },token).ConfigureAwait(false);
        await RefreshAreaAsync(prepared,token).ConfigureAwait(false);
        if(prepared.Claim.Phase=="preparing")
            await Task.Run(()=>_area!.RecoverPreparing(prepared.Claim,_budget,token),CancellationToken.None).ConfigureAwait(false);
        var retiring=await _state.ExecuteAsync(database=>
        {token.ThrowIfCancellationRequested();return new CurrentCatalogStageJournal(database,_budget).BeginRetirement(prepared.Claim);},token).ConfigureAwait(false);
        var receipt=await Task.Run(()=>_area!.RemoveRetiring(retiring,token),CancellationToken.None).ConfigureAwait(false);
        var remaining=await _state.ExecuteAsync(database=>
        {
            token.ThrowIfCancellationRequested();var journal=new CurrentCatalogStageJournal(database,_budget);journal.CompleteRetirement(retiring,receipt);return journal.ReadAll();
        },token).ConfigureAwait(false);
        await Task.Run(()=>_area!.SyncClaims(remaining),CancellationToken.None).ConfigureAwait(false);return true;
    },cancellationToken);

    private async Task RefreshAreaAsync(Prepared prepared,CancellationToken token)
    {
        if(_area is null)
        {
            var area=await Task.Run(()=>new CurrentCatalogStageArea(prepared.Directory,prepared.Installation,prepared.Claims,_budget),CancellationToken.None).ConfigureAwait(false);
            _area=area;_directory=prepared.Directory;_installation=prepared.Installation;
        }
        else
        {
            if(_directory!=prepared.Directory || _installation!=prepared.Installation) throw new InvalidOperationException("A shared stage owner cannot change installation or state directory.");
            await Task.Run(()=>_area.SyncClaims(prepared.Claims),CancellationToken.None).ConfigureAwait(false);
        }
        Check(token);
    }
    private void SweepClosed()
    {foreach(var pair in _stages.Where(pair=>pair.Value.ExitConfirmed).ToArray()) _stages.Remove(pair.Key);}
    private async Task<T> OperationAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try {Check(linked.Token);return await action(linked.Token).ConfigureAwait(false);}
            finally {_serial.Release();}
        }
    }
    private void Check(CancellationToken token)
    {lock(_gate) if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("The shared stage owner retired before admission.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal ??=StopAsync());}}
    private async Task StopAsync()
    {
        var failures=new List<Exception>();try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach(var stage in _stages.Values)
                try {await stage.DisposeAsync().ConfigureAwait(false);}catch(Exception error){failures.Add(error);}
            if(_stages.Values.Any(stage=>!stage.ExitConfirmed)) throw new AggregateException("Shared indexes have not joined; retain the area and borrowed state.",failures);
            if(failures.Count!=0) throw new AggregateException("Shared staging cleanup needs attention; retain its owner for retry.",failures);
            if(_area is not null) await Task.Run(_area.Dispose,CancellationToken.None).ConfigureAwait(false);
            _stages.Clear();_stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        finally {_serial.Release();}
    }
    private sealed record Prepared(CurrentCatalogStageClaim Claim,IReadOnlyList<CurrentCatalogStageClaim> Claims,string Directory,Guid Installation);
}
