using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

internal interface ILocalCurrentIndexOwner : IAsyncDisposable
{
    bool ExitConfirmed {get;}
}

/// <summary>Read-only historical content under the pool's native area lease. It has
/// no capture, graph-staging, activation, publication, account or original-write
/// members. Retire before the pool and its borrowed state owner.</summary>
public sealed class LocalHistoricalCatalog : ILocalCurrentIndexOwner
{
    private readonly CurrentCatalogStageIndex _index;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    public CurrentCatalogActivation Selection {get;}
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}

    internal LocalHistoricalCatalog(CurrentCatalogActivation selection,CurrentCatalogStageIndex index)
    {Selection=selection ?? throw new ArgumentNullException(nameof(selection));_index=index ?? throw new ArgumentNullException(nameof(index));}

    public Task<CurrentCatalogStagePage> ReadPageAsync(CatalogCursor? after,int limit,CancellationToken caller)=>
        ReadAsync(token=>_index.ReadPage(after,limit,token),caller);
    public Task<CurrentCatalogStagePage> ReadPreviousPageAsync(CatalogCursor before,int limit,CancellationToken caller)=>
        ReadAsync(token=>_index.ReadPreviousPage(before,limit,token),caller);
    public Task<CurrentCatalogStagePage> ReadAtOrBeforeAsync(long utcMilliseconds,int limit,CancellationToken caller)=>
        ReadAsync(token=>_index.ReadAtOrBefore(utcMilliseconds,limit,token),caller);
    public Task<DriveCatalogAsset?> FindAssetAsync(Guid assetId,CancellationToken caller)=>
        ReadAsync(token=>_index.FindAsset(assetId,token),caller);

    public Task<CurrentCatalogStagePage> ReadAccountPageAsync(CurrentCatalogAccountFilter account,CatalogCursor? after,int limit,CancellationToken caller)=>
        ReadAsync(token=>_index.ReadAccountPage(account,after,limit,token),caller);
    public Task<CurrentCatalogStagePage> ReadAccountPreviousPageAsync(CurrentCatalogAccountFilter account,CatalogCursor before,int limit,CancellationToken caller)=>
        ReadAsync(token=>_index.ReadAccountPreviousPage(account,before,limit,token),caller);
    public Task<CurrentCatalogStagePage> ReadAccountAtOrBeforeAsync(CurrentCatalogAccountFilter account,long utcMilliseconds,int limit,CancellationToken caller)=>
        ReadAsync(token=>_index.ReadAccountAtOrBefore(account,utcMilliseconds,limit,token),caller);
    public Task<DriveCatalogAsset?> FindAccountAssetAsync(CurrentCatalogAccountFilter account,Guid assetId,CancellationToken caller)=>
        ReadAsync(token=>_index.FindAccountAsset(account,assetId,token),caller);

    private async Task<T> ReadAsync<T>(Func<CancellationToken,T> query,CancellationToken caller)
    {
        CancellationTokenSource linked;
        lock(_gate) {ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                Check(linked.Token);
                var result=await Task.Run(()=>query(linked.Token),CancellationToken.None).ConfigureAwait(false);
                Check(linked.Token);return result;
            }
            finally {_serial.Release();}
        }
    }
    private void Check(CancellationToken token)
    {lock(_gate) if(_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Historical catalog retired before read completion.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal ??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){cancellationFailure=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if(cancellationFailure is not null) throw new AggregateException("Historical read cancellation needs attention; retain the native owner.",cancellationFailure);
            await Task.Run(_index.Dispose,CancellationToken.None).ConfigureAwait(false);
            _stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        finally {_serial.Release();}
    }
}
