using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentCanonicalAccountQueriesUseTheActualSelectedIndexAndReopenOfflineWithoutProviderReads()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        CurrentCatalogActivation selected;Guid assetId;CurrentCatalogAccountFilter account;
        await using(var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool))
        {
            var context=await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false);selected=context.Selection;
            var all=await runtime.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(all);
            var asset=all.Page.Assets.Single();assetId=asset.Id;account=new(asset.AccountId);
            var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
            var first=await runtime.ReadAccountPageAsync(selected.StageId,account,null,60,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(first);Assert.AreEqual(selected,first.Selection);Assert.AreEqual(assetId,first.Page.Assets.Single().Id);
            var absent=await runtime.ReadAccountPageAsync(selected.StageId,new("another-account"),null,60,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(absent);Assert.AreEqual(0,absent.Page.Assets.Count);Assert.IsFalse(absent.Page.HasMore);
            var before=await runtime.ReadAccountPreviousPageAsync(selected.StageId,account,new(asset.EffectiveUtc,asset.Id),60,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(before);Assert.AreEqual(0,before.Page.Assets.Count);
            var dated=await runtime.ReadAccountAtOrBeforeAsync(selected.StageId,account,253402300799999L,60,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(dated);Assert.AreEqual(assetId,dated.Page.Assets.Single().Id);
            var found=await runtime.FindAccountAssetAsync(selected.StageId,account,assetId,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(found);Assert.IsNotNull(found.Asset);Assert.AreEqual(assetId,found.Asset.Id);
            var wrong=await runtime.FindAccountAssetAsync(selected.StageId,new("another-account"),assetId,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(wrong);Assert.IsNull(wrong.Asset);
            Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));
        }
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        var offlineCalls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
        await using var reopened=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        Assert.AreEqual(selected,await reopened.InitializeHistoricalAsync(CancellationToken.None).ConfigureAwait(false));
        var historical=await reopened.ReadAccountPageAsync(selected.StageId,account,null,60,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(historical);Assert.AreEqual(assetId,historical.Page.Assets.Single().Id);
        var exact=await reopened.FindAccountAssetAsync(selected.StageId,account,assetId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(exact);Assert.IsNotNull(exact.Asset);Assert.AreEqual(assetId,exact.Asset.Id);
        Assert.AreEqual(offlineCalls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));
    }

    [TestMethod]
    public async Task CurrentCanonicalAccountQueriesRemainAvailableDuringBlockedRefreshAndExitJoinsItsPhysicalRead()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var selected=(await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false)).Selection;
        var page=await runtime.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);
        var asset=page.Page.Assets.Single();var account=new CurrentCatalogAccountFilter(asset.AccountId);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        Task<CurrentCatalogActivation>? refresh=null;Task? stopping=null;Task? canceled=null;
        try
        {
            refresh=runtime.RefreshAsync(CancellationToken.None);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsNotNull(await runtime.ReadAccountPageAsync(selected.StageId,account,null,60,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            Assert.IsNotNull(await runtime.ReadAccountPreviousPageAsync(selected.StageId,account,new(asset.EffectiveUtc,asset.Id),60,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            Assert.IsNotNull(await runtime.ReadAccountAtOrBeforeAsync(selected.StageId,account,253402300799999L,60,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            Assert.IsNotNull(await runtime.FindAccountAssetAsync(selected.StageId,account,asset.Id,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            stopping=runtime.DisposeAsync().AsTask();Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(pool.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);
            canceled=Assert.ThrowsAsync<OperationCanceledException>(()=>refresh);
        }
        finally
        {
            release.TrySetResult();
            if(refresh is not null) await ObserveCanonicalRuntimeRefreshAsync(refresh,stopping is not null).ConfigureAwait(false);
            if(stopping is not null) await stopping.ConfigureAwait(false);
        }
        if(canceled is not null) await canceled.ConfigureAwait(false);
        Assert.IsTrue(runtime.ExitConfirmed);Assert.AreEqual(selected,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentCanonicalAccountQueriesRejectOldStagesAndInvalidPositionsWithoutSubstitutingAnotherSelection()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var context=await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false);var selected=context.Selection;
        var page=await runtime.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);
        var asset=page.Page.Assets.Single();var account=new CurrentCatalogAccountFilter(asset.AccountId);
        var newer=await PrepareTransitionAsync(fixture,context.IntentId).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(newer.Id,CancellationToken.None).ConfigureAwait(false);
        var current=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreNotEqual(selected.StageId,current.StageId);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.ReadAccountPageAsync(selected.StageId,account,null,60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.ReadAccountPreviousPageAsync(selected.StageId,account,new(asset.EffectiveUtc,asset.Id),60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.ReadAccountAtOrBeforeAsync(selected.StageId,account,0,60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.FindAccountAssetAsync(selected.StageId,account,asset.Id,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.ReadAccountPageAsync(null,account,new(asset.EffectiveUtc,asset.Id),60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.ReadAccountAtOrBeforeAsync(current.StageId,account,long.MinValue,60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.ReadAccountPreviousPageAsync(current.StageId,account,new(null,Guid.Empty),60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.ReadAccountPageAsync(current.StageId,account,null,91,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.FindAccountAssetAsync(Guid.Empty,account,asset.Id,CancellationToken.None)).ConfigureAwait(false);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>runtime.ReadAccountPageAsync(current.StageId,account,null,60,canceled.Token)).ConfigureAwait(false);
        var actual=await runtime.FindAccountAssetAsync(current.StageId,account,asset.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(actual);Assert.IsNotNull(actual.Asset);Assert.AreEqual(asset.Id,actual.Asset.Id);Assert.AreEqual(current,actual.Selection);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));
    }

    private static async Task<(Guid IntentId,CurrentCatalogActivation Selection)> SelectCanonicalAccountFixtureAsync(CurrentReaderFixture fixture,CurrentCanonicalReadRuntime runtime)
    {
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        return (head.Intent.Id,await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false));
    }
}
