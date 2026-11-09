using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentCanonicalRuntimeKeepsHistoricalQueriesIndependentOfAnEnteredRefreshAndJoinsExit()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        // The full inventory requires an actual retained Root plan. The helper's
        // publication snapshot descriptor is synthetic; this test owns only real
        // canonical files and never claims outgoing snapshot completion/retirement.
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var selected=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        Task<CurrentCatalogActivation>? refresh=null;Task? stopping=null;Task? cancellation=null;
        try
        {
            refresh=runtime.RefreshAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var page=await runtime.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsNotNull(page);Assert.AreEqual(selected,page.Selection);Assert.AreEqual(1,page.Page.Assets.Count);
            stopping=runtime.DisposeAsync().AsTask();Assert.IsFalse(stopping.IsCompleted);
            Assert.IsFalse(fixture.Reader.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);
            cancellation=Assert.ThrowsAsync<OperationCanceledException>(()=>refresh);
        }
        finally
        {
            release.TrySetResult();
            if(refresh is not null) await ObserveCanonicalRuntimeRefreshAsync(refresh,stopping is not null).ConfigureAwait(false);
            if(stopping is not null) await stopping.ConfigureAwait(false);
        }
        if(cancellation is not null) await cancellation.ConfigureAwait(false);
        Assert.IsTrue(runtime.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);
        Assert.AreEqual(selected,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentCanonicalRuntimeReopensAcknowledgedContentOfflineWithoutReadingANewerRemoteRoot()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        // The full inventory requires an actual retained Root plan. The helper's
        // publication snapshot descriptor is synthetic; this test owns only real
        // canonical files and never claims outgoing snapshot completion/retirement.
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        CurrentCatalogActivation selected;
        await using(var first=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool))
            selected=await first.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var newer=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(newer.Id,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        var bodies=fixture.Http.BodyReads;var metadata=fixture.Http.MetadataReads;var puts=fixture.Http.Puts;
        await using var restarted=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        Assert.AreEqual(selected,await restarted.InitializeHistoricalAsync(CancellationToken.None).ConfigureAwait(false));
        var page=await restarted.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(page);Assert.AreEqual(selected,page.Selection);Assert.AreEqual(1,page.Page.Assets.Count);
        Assert.AreEqual(bodies,fixture.Http.BodyReads);Assert.AreEqual(metadata,fixture.Http.MetadataReads);Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.AreEqual(selected,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentCanonicalRuntimeRejectsOldPageContextAfterARealNewActivationAndRetiresOnlyTheClosedIndex()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        // The full inventory requires an actual retained Root plan. The helper's
        // publication snapshot descriptor is synthetic; this test owns only real
        // canonical files and never claims outgoing snapshot completion/retirement.
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var first=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var next=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(next.Id,CancellationToken.None).ConfigureAwait(false);
        var second=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(first.StageId,second.StageId);Assert.AreEqual(head.Reference,second.Catalog);
        Assert.AreEqual(next.Sequence,second.RootSequence);Assert.IsTrue(second.RootSequence>first.RootSequence);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.ReadPageAsync(first.StageId,null,60,CancellationToken.None)).ConfigureAwait(false);
        var page=await runtime.ReadPageAsync(second.StageId,null,60,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(page);Assert.AreEqual(second,page.Selection);Assert.AreEqual(1,page.Page.Assets.Count);
        var claims=await StageClaimsAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(second.StageId,claims.Single().Id);Assert.AreEqual(second,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentCanonicalRuntimeRetainsVisibleContentOnGraphFailureAndRecoversItsExactInactiveClaim()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        // The full inventory requires an actual retained Root plan. The helper's
        // publication snapshot descriptor is synthetic; this test owns only real
        // canonical files and never claims outgoing snapshot completion/retirement.
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var selected=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var failure=new InvalidOperationException("Synthetic immutable transport failure.");
        fixture.Http.BeforeFileBody=file=>file==head.Reference.FileId ? Task.FromException(failure) : Task.CompletedTask;
        var observed=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>runtime.RefreshAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(failure,observed);Assert.AreEqual(selected,runtime.Selection);
        var retained=await runtime.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(retained);Assert.AreEqual(1,retained.Page.Assets.Count);
        var before=await StageClaimsAsync(fixture).ConfigureAwait(false);Assert.AreEqual(2,before.Count);
        var inactive=before.Single(value=>value.Id!=selected.StageId);
        fixture.Http.BeforeFileBody=null;
        var recovered=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var after=await StageClaimsAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(recovered.StageId,after.Single().Id);Assert.AreNotEqual(inactive.Id,recovered.StageId);
        Assert.AreEqual(head.Reference,recovered.Catalog);Assert.AreEqual(recovered,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    private static async Task ObserveCanonicalRuntimeRefreshAsync(Task operation,bool stopping)
    {
        try {await operation.ConfigureAwait(false);}
        catch(OperationCanceledException) when(stopping) { /* The actual owner requested and joined this cancellation. */ }
    }
}
