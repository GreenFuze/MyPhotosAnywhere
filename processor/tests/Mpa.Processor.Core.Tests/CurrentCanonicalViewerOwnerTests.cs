using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CurrentCatalogStageTests
{
    [TestMethod]
    public async Task CanonicalViewerSessionsUseTheActualCanonicalSourceAndBindReplayToStageAccountAndBrowser()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);
        var assets=Enumerable.Range(0,40).Select(i=>AccountAsset(fixture,i%2==0?"account-A":"account-B","owner-"+i,i)).ToArray();foreach(var leaf in fixture.Leaves(assets)) source.Add(leaf);source.Seal(assets.Length);
        await using var state=new ProcessorStateCoordinator(fixture.Database);var capture=new CanonicalViewerSourceFixture(state,fixture.Budget,source,fixture.Claim.Id);
        await using var sessions=new CatalogViewerSessions(state,fixture.Root.Path,fixture.Database.InstallationId,fixture.Budget,canonical:capture);
        var browser=Guid.NewGuid();var other=Guid.NewGuid();var request=new CatalogViewerRequest(Guid.NewGuid(),assets[0].Id,"account-A",fixture.Claim.Id);
        var opened=await sessions.OpenAsync(browser,request,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(20L,opened.Count);Assert.AreEqual(fixture.Claim.Id,opened.StageId);Assert.AreEqual("account-A",opened.AccountId);
        Assert.AreEqual(assets[0].Id,opened.Entries.Single(e=>e.Position==opened.Position).AssetId);Assert.AreEqual(1,capture.Captures);
        Assert.AreEqual(opened.Count,(await sessions.OpenAsync(browser,request,CancellationToken.None).ConfigureAwait(false)).Count);Assert.AreEqual(1,capture.Captures);
        await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(()=>sessions.OpenAsync(browser,request with {StageId=Guid.NewGuid()},CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(()=>sessions.OpenAsync(browser,request with {AccountId=null},CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(()=>sessions.ReadAsync(other,request.Id,opened.Position,CancellationToken.None)).ConfigureAwait(false);
        await sessions.CloseAsync(other,request.Id,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(20L,(await sessions.ReadAsync(browser,request.Id,0,CancellationToken.None).ConfigureAwait(false)).Count);
        await sessions.OpenAsync(browser,request with {Id=Guid.NewGuid()},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogViewerCapacityException>(()=>sessions.OpenAsync(browser,request with {Id=Guid.NewGuid()},CancellationToken.None)).ConfigureAwait(false);
        await sessions.CloseBrowserAsync(browser,CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(()=>sessions.ReadAsync(browser,request.Id,0,CancellationToken.None)).ConfigureAwait(false);
        await sessions.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(sessions.ExitConfirmed);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root.Path,"viewer-sessions")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CanonicalViewerSessionsJoinLateCompletedNativeCapturesOnCallerCancellationAndExit(bool exit)
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);
        var asset=fixture.Asset("owner-late",1);foreach(var leaf in fixture.Leaves([asset])) source.Add(leaf);source.Seal(1);
        await using var state=new ProcessorStateCoordinator(fixture.Database);var capture=new CanonicalViewerSourceFixture(state,fixture.Budget,source,fixture.Claim.Id) {Hold=true};
        await using var sessions=new CatalogViewerSessions(state,fixture.Root.Path,fixture.Database.InstallationId,fixture.Budget,canonical:capture);
        using var canceled=new CancellationTokenSource();var opening=sessions.OpenAsync(Guid.NewGuid(),new(Guid.NewGuid(),asset.Id,null,fixture.Claim.Id),canceled.Token);Task? stopping=null;
        try
        {
            await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsNotNull(capture.Last);Assert.IsFalse(capture.Last.ExitConfirmed);
            if(exit) stopping=sessions.DisposeAsync().AsTask();else canceled.Cancel();
            Assert.IsFalse(opening.IsCompleted);Assert.IsFalse(sessions.ExitConfirmed);if(stopping is not null) Assert.IsFalse(stopping.IsCompleted);
        }
        finally {capture.Release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>opening).ConfigureAwait(false);if(stopping is not null) await stopping.ConfigureAwait(false);
        Assert.IsNotNull(capture.Last);Assert.IsTrue(capture.Last.ExitConfirmed);
        await sessions.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(sessions.ExitConfirmed);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root.Path,"viewer-sessions")));
    }

    [TestMethod]
    public async Task CanonicalViewerSessionsRejectMissingOrForeignSourceOwnershipWithoutFallingBackToDiscovery()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);source.Seal(0);
        await using var state=new ProcessorStateCoordinator(fixture.Database);var capture=new CanonicalViewerSourceFixture(state,fixture.Budget,source,fixture.Claim.Id);
        var foreign=new StorageBudgetManager(new ViewerCaptureSpace());
        Assert.ThrowsExactly<ArgumentException>(()=>new CatalogViewerSessions(state,fixture.Root.Path,fixture.Database.InstallationId,foreign,canonical:capture));
        await using var sessions=new CatalogViewerSessions(state,fixture.Root.Path,fixture.Database.InstallationId,fixture.Budget);
        await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(()=>sessions.OpenAsync(Guid.NewGuid(),new(Guid.NewGuid(),Guid.NewGuid(),null,fixture.Claim.Id),CancellationToken.None)).ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(()=>sessions.OpenAsync(Guid.NewGuid(),new(Guid.NewGuid(),Guid.NewGuid(),null,Guid.Empty),CancellationToken.None));
        Assert.AreEqual(0,capture.Captures);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root.Path,"viewer-sessions")));
    }

    [TestMethod]
    public async Task CanonicalViewerSessionsRetainTheExactFailedCaptureOwnerUntilJoinedExit()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);
        var asset=fixture.Asset("owner-retained",1);foreach(var leaf in fixture.Leaves([asset])) source.Add(leaf);source.Seal(1);
        await using var state=new ProcessorStateCoordinator(fixture.Database);var capture=new CanonicalViewerSourceFixture(state,fixture.Budget,source,fixture.Claim.Id) {RetainFailure=true};
        await using var sessions=new CatalogViewerSessions(state,fixture.Root.Path,fixture.Database.InstallationId,fixture.Budget,canonical:capture);
        var failure=await Assert.ThrowsExactlyAsync<CatalogSessionCaptureException>(()=>sessions.OpenAsync(Guid.NewGuid(),new(Guid.NewGuid(),asset.Id,null,fixture.Claim.Id),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(capture.Last,failure.RetainedOwner);Assert.IsFalse(failure.RetainedOwner.ExitConfirmed);Assert.IsTrue(fixture.Budget.Status().ReservedBytes>0);
        await sessions.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(failure.RetainedOwner.ExitConfirmed);Assert.IsTrue(sessions.ExitConfirmed);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    private sealed class CanonicalViewerSourceFixture(ProcessorStateCoordinator state,StorageBudgetManager storage,CurrentCatalogStageIndex source,Guid stage) : ICanonicalCatalogViewerSource
    {
        internal int Captures {get;private set;}
        internal bool Hold {get;init;}
        internal bool RetainFailure {get;init;}
        internal CatalogViewerSessionIndex? Last {get;private set;}
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Borrows(ProcessorStateCoordinator candidate,StorageBudgetManager budget)=>ReferenceEquals(candidate,state) && ReferenceEquals(budget,storage);
        public async Task<CatalogViewerSessionIndex> CaptureAsync(CatalogViewerRequest request,string ownedIndexPath,CancellationToken cancellationToken)
        {
            if(request.StageId!=stage) throw new CatalogViewerUnavailableException();Captures++;
            var index=await Task.Run(()=>source.CaptureViewerIndex(ownedIndexPath,request.SelectedId,request.AccountId is null?null:new(request.AccountId),cancellationToken),CancellationToken.None).ConfigureAwait(false);
            Last=index;if(Hold) {Entered.TrySetResult();await Release.Task.ConfigureAwait(false);} // Model a real completed native owner returning after cancellation.
            if(RetainFailure) throw new CatalogSessionCaptureException(new IOException("Synthetic retained-owner transfer."),index);
            return index;
        }
    }
}
