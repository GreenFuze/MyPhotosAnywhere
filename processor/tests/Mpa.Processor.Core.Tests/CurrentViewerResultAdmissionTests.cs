using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CurrentCatalogStageTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CanonicalViewerAdmissionChecksTheOriginalCallerBeforeItsLinkedCancellationCanRun(bool filtered)
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);
        var asset=fixture.Asset("result-admission",1);foreach(var leaf in fixture.Leaves([asset])) source.Add(leaf);source.Seal(1);
        await using var state=new ProcessorStateCoordinator(fixture.Database);var capture=new ViewerAdmissionSource(state,fixture.Budget,source);
        await using var sessions=new CatalogViewerSessions(state,fixture.Root.Path,fixture.Database.InstallationId,fixture.Budget,canonical:capture);
        using var caller=new CancellationTokenSource();var browser=Guid.NewGuid();var request=new CatalogViewerRequest(Guid.NewGuid(),asset.Id,filtered?"account-A":null,fixture.Claim.Id);
        var opening=sessions.OpenAsync(browser,request,caller.Token);await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        using var releaseCallback=new ManualResetEventSlim();var callbackEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration=caller.Token.Register(()=>{callbackEntered.TrySetResult();releaseCallback.Wait();});
        var canceling=Task.Run(()=>caller.Cancel());
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsTrue(caller.IsCancellationRequested);Assert.IsFalse(capture.AdmittedToken.IsCancellationRequested);Assert.IsFalse(canceling.IsCompleted);
            capture.Release.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(()=>opening.WaitAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            Assert.IsFalse(canceling.IsCompleted);Assert.IsNotNull(capture.Index);Assert.IsTrue(capture.Index.ExitConfirmed);
            await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(()=>sessions.ReadAsync(browser,request.Id,0,CancellationToken.None)).ConfigureAwait(false);
        }
        finally {releaseCallback.Set();capture.Release.TrySetResult();await canceling.ConfigureAwait(false);}
        await sessions.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(sessions.ExitConfirmed);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    private sealed class ViewerAdmissionSource(ProcessorStateCoordinator state,StorageBudgetManager budget,CurrentCatalogStageIndex source) : ICanonicalCatalogViewerSource
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CatalogViewerSessionIndex? Index {get;private set;}
        internal CancellationToken AdmittedToken {get;private set;}
        public bool Borrows(ProcessorStateCoordinator candidate,StorageBudgetManager storage)=>ReferenceEquals(candidate,state) && ReferenceEquals(storage,budget);
        public async Task<CatalogViewerSessionIndex> CaptureAsync(CatalogViewerRequest request,string ownedIndexPath,CancellationToken cancellationToken)
        {
            AdmittedToken=cancellationToken;
            var index=await Task.Run(()=>source.CaptureViewerIndex(ownedIndexPath,request.SelectedId,request.AccountId is null?null:new(request.AccountId),cancellationToken),CancellationToken.None).ConfigureAwait(false);
            Index=index;Entered.TrySetResult();await Release.Task.ConfigureAwait(false);return index;
        }
    }
}
