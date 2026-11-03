using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Owned result admission differs from an ordinary data query: if a
/// physical capture finishes as cancellation/retirement wins, dispose it before
/// rejecting the result. Unconfirmed cleanup carries its exact retained owner.</summary>
internal static class RejectedCanonicalViewerCapture
{
    internal static void Retire(CatalogViewerSessionIndex index,Exception admissionFailure)
    {
        try
        {
            index.Dispose();
            if(!index.ExitConfirmed) throw new InvalidOperationException("The rejected canonical viewer index has not physically closed.");
        }
        catch(Exception cleanup) {throw new CatalogSessionCaptureException(new AggregateException(admissionFailure,cleanup),index);}
    }
}

public sealed partial class LocalHistoricalCatalog
{
    /// <summary>Hold this exact sealed native owner for the complete streamed
    /// capture. The generated viewer owns only its private ordered identities.</summary>
    public async Task<CatalogViewerSessionIndex> CaptureViewerIndexAsync(string ownedIndexPath,Guid selectedId,CurrentCatalogAccountFilter? account,CancellationToken caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedIndexPath);
        if(selectedId==Guid.Empty) throw new ArgumentException("An exact selected photo identity is required.",nameof(selectedId));
        CancellationTokenSource linked;
        lock(_gate) {ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            CatalogViewerSessionIndex? generated=null;
            try
            {
                Check(linked.Token);
                generated=await Task.Run(()=>_index.CaptureViewerIndex(ownedIndexPath,selectedId,account,linked.Token),CancellationToken.None).ConfigureAwait(false);
                Check(linked.Token);return generated;
            }
            catch(Exception error) {if(generated is not null) RejectedCanonicalViewerCapture.Retire(generated,error);throw;}
            finally {_serial.Release();}
        }
    }
}

public sealed partial class CurrentCanonicalReadRuntime
{
    /// <summary>Exact-stage resource query; include it in joined query retirement
    /// and retain/dispose a completed private owner before any late rejection.</summary>
    public async Task<CatalogViewerSessionIndex> CaptureViewerIndexAsync(Guid expectedStageId,string ownedIndexPath,Guid selectedId,CurrentCatalogAccountFilter? account,CancellationToken caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedIndexPath);
        if(expectedStageId==Guid.Empty || selectedId==Guid.Empty) throw new ArgumentException("Exact catalog and photo identities are required.");
        LocalHistoricalCatalog active;CancellationTokenSource linked;
        lock(_gate)
        {
            AssertRunning();caller.ThrowIfCancellationRequested();
            active=_active ?? throw new CurrentCanonicalPositionChangedException();
            if(active.Selection.StageId!=expectedStageId) throw new CurrentCanonicalPositionChangedException();
            var admitted=checked(_queries+1);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);_queries=admitted;
        }
        using(linked)
        {
            CatalogViewerSessionIndex? generated=null;
            try
            {
                generated=await active.CaptureViewerIndexAsync(ownedIndexPath,selectedId,account,linked.Token).ConfigureAwait(false);
                Check(linked.Token);return generated;
            }
            catch(Exception error) {if(generated is not null) RejectedCanonicalViewerCapture.Retire(generated,error);throw;}
            finally {lock(_gate) {_queries--;if(_stopping && _queries==0) _queriesDrained.TrySetResult();}}
        }
    }
}
