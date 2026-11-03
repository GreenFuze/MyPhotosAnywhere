using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentCanonicalNavigationReturnsExactContextAndExplicitlyMissingIdentitiesWithoutCloudReads()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var selection=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var first=await runtime.ReadPageAsync(selection.StageId,null,60,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(first);var asset=first.Page.Assets.Single();
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
        var previous=await runtime.ReadPreviousPageAsync(selection.StageId,new(asset.EffectiveUtc,asset.Id),60,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(previous);Assert.AreEqual(selection,previous.Selection);Assert.AreEqual(0,previous.Page.Assets.Count);Assert.IsFalse(previous.Page.HasMore);
        var dated=await runtime.ReadAtOrBeforeAsync(selection.StageId,0,60,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(dated);Assert.AreEqual(selection,dated.Selection);Assert.AreEqual(asset.Id,dated.Page.Assets.Single().Id);
        var found=await runtime.FindAssetAsync(selection.StageId,asset.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(found);Assert.AreEqual(selection,found.Selection);Assert.AreEqual(asset.Id,found.Asset!.Id);
        var absent=await runtime.FindAssetAsync(selection.StageId,Guid.NewGuid(),CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(absent);Assert.AreEqual(selection,absent.Selection);Assert.IsNull(absent.Asset);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));
    }

    [TestMethod]
    public async Task CurrentCanonicalNavigationRejectsEveryOldQueryContextAfterActualReplacement()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var previous=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var page=await runtime.ReadPageAsync(previous.StageId,null,60,CancellationToken.None).ConfigureAwait(false);
        var asset=page!.Page.Assets.Single();
        var newer=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(newer.Id,CancellationToken.None).ConfigureAwait(false);
        var active=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreNotEqual(previous.StageId,active.StageId);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.ReadPreviousPageAsync(previous.StageId,
            new(asset.EffectiveUtc,asset.Id),60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.ReadAtOrBeforeAsync(previous.StageId,0,60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.FindAssetAsync(previous.StageId,asset.Id,CancellationToken.None)).ConfigureAwait(false);
        var actual=await runtime.FindAssetAsync(active.StageId,asset.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(actual);Assert.AreEqual(active,actual.Selection);Assert.AreEqual(asset.Id,actual.Asset!.Id);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));
    }

    [TestMethod]
    public async Task CurrentCanonicalNavigationKeepsEveryReadIndependentOfBlockedRefreshAndJoinsItsActualExit()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var root=await PrepareTransitionAsync(fixture,head.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var selected=await runtime.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        var page=await runtime.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);var asset=page!.Page.Assets.Single();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        Task<CurrentCatalogActivation>? refresh=null;Task? stopping=null;Task? canceled=null;
        try
        {
            refresh=runtime.RefreshAsync(CancellationToken.None);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsNotNull(await runtime.ReadPreviousPageAsync(selected.StageId,new(asset.EffectiveUtc,asset.Id),60,CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            Assert.IsNotNull(await runtime.ReadAtOrBeforeAsync(selected.StageId,0,60,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            Assert.IsNotNull(await runtime.FindAssetAsync(selected.StageId,asset.Id,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            stopping=runtime.DisposeAsync().AsTask();Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(pool.ExitConfirmed);
            canceled=Assert.ThrowsAsync<OperationCanceledException>(()=>refresh);
        }
        finally
        {
            release.TrySetResult();
            if(refresh is not null) await ObserveCanonicalRuntimeRefreshAsync(refresh,stopping is not null).ConfigureAwait(false);
            if(stopping is not null) await stopping.ConfigureAwait(false);
        }
        if(canceled is not null) await canceled.ConfigureAwait(false);
        Assert.IsTrue(runtime.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);
    }

    [TestMethod]
    public async Task CurrentCanonicalNavigationValidatesBeforeAdmissionAndNeverRetargetsAnAbsentCatalog()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        Assert.IsNull(await runtime.ReadAtOrBeforeAsync(null,0,60,CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.ReadPreviousPageAsync(Guid.NewGuid(),new(null,Guid.NewGuid()),60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.FindAssetAsync(Guid.NewGuid(),Guid.NewGuid(),CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.ReadAtOrBeforeAsync(null,long.MinValue,60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.ReadAtOrBeforeAsync(null,0,91,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.ReadPreviousPageAsync(Guid.NewGuid(),new(null,Guid.Empty),60,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.FindAssetAsync(Guid.Empty,Guid.NewGuid(),CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.FindAssetAsync(Guid.NewGuid(),Guid.Empty,CancellationToken.None)).ConfigureAwait(false);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>runtime.ReadAtOrBeforeAsync(null,0,60,canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.MetadataReads);Assert.AreEqual(0,fixture.Http.Puts);
    }
}
