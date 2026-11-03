using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CanonicalViewerCaptureUsesActualHistoricalIdentitiesOfflineAndOutlivesTheJoinedSource()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);using var directory=new CanonicalViewerTestDirectory();
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        try
        {
            var selection=(await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false)).Selection;
            var page=await runtime.ReadPageAsync(selection.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);var asset=page.Page.Assets.Single();
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
            var calls=(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts);
            using var frozen=await runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,asset.Id,new(asset.AccountId),CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L,frozen.Count);Assert.AreEqual(0L,frozen.SelectedPosition);Assert.AreEqual(asset.Id,frozen.ReadWindow(0).Single().AssetId);
            await runtime.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(runtime.ExitConfirmed);
            Assert.AreEqual(asset.Id,frozen.ReadWindow(0).Single().AssetId);Assert.AreEqual(calls,(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts));
            frozen.Dispose();Assert.IsTrue(frozen.ExitConfirmed);Assert.IsFalse(File.Exists(directory.IndexPath));
        }
        finally {await runtime.DisposeAsync().ConfigureAwait(false);}
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CanonicalViewerCaptureCallerCancellationAndExitJoinTheActualBlockedNativeCapture(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);using var directory=new CanonicalViewerTestDirectory();using var space=new CurrentStageBlockedSpace();
        var budget=new StorageBudgetManager(space);await using var reader=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,budget);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,budget);await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,budget,reader,pool);
        var selection=(await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false)).Selection;
        var page=await runtime.ReadPageAsync(selection.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);var asset=page.Page.Assets.Single();
        var reserved=budget.Status().ReservedBytes;var calls=(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts);using var canceled=new CancellationTokenSource();
        Task<CatalogViewerSessionIndex>? capture=null;Task? stopping=null;
        try
        {
            space.Armed=true;capture=runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,asset.Id,null,canceled.Token);
            await space.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=runtime.DisposeAsync().AsTask();else canceled.Cancel();
            Assert.IsFalse(capture.IsCompleted);Assert.IsFalse(runtime.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);Assert.IsFalse(reader.ExitConfirmed);
            if(stopping is not null) Assert.IsFalse(stopping.IsCompleted);
        }
        finally
        {
            space.Release.Set();
            if(capture is not null) await Assert.ThrowsAsync<OperationCanceledException>(()=>capture).ConfigureAwait(false);
            if(stopping is not null) await stopping.ConfigureAwait(false);
        }
        Assert.IsFalse(File.Exists(directory.IndexPath));Assert.IsFalse(File.Exists(directory.IndexPath+"-journal"));Assert.AreEqual(reserved,budget.Status().ReservedBytes);
        Assert.AreEqual(selection,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(calls,(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts));
        if(exit) Assert.IsTrue(runtime.ExitConfirmed);
        else {using var retry=await runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,asset.Id,null,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(asset.Id,retry.ReadWindow(0).Single().AssetId);}
    }

    [TestMethod]
    public async Task CanonicalViewerCaptureLateCancellationDisposesACompletedPrivateOwnerBeforeRejectingAdmission()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);using var directory=new CanonicalViewerTestDirectory();var space=new CanonicalViewerCallbackSpace();
        var budget=new StorageBudgetManager(space);await using var reader=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,budget);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,budget);await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,budget,reader,pool);
        var selection=(await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false)).Selection;
        var page=await runtime.ReadPageAsync(selection.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);var asset=page.Page.Assets.Single();
        var reserved=budget.Status().ReservedBytes;using var canceled=new CancellationTokenSource();var reads=0;var sawPrivateFile=false;
        space.Observed=()=>{if(++reads==4) {sawPrivateFile=File.Exists(directory.IndexPath);canceled.Cancel();}};
        await Assert.ThrowsAsync<OperationCanceledException>(()=>runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,asset.Id,null,canceled.Token)).ConfigureAwait(false);
        space.Observed=null;Assert.AreEqual(4,reads);Assert.IsTrue(sawPrivateFile);
        Assert.IsFalse(File.Exists(directory.IndexPath));Assert.IsFalse(File.Exists(directory.IndexPath+"-journal"));Assert.AreEqual(reserved,budget.Status().ReservedBytes);
        using var retry=await runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,asset.Id,null,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(asset.Id,retry.ReadWindow(0).Single().AssetId);
    }

    [TestMethod]
    public async Task CanonicalViewerCaptureRequiresTheExactStageAndSelectedAccountWithoutChangingProviderOrDiscoveryState()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);using var directory=new CanonicalViewerTestDirectory();
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);await using var runtime=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var selection=(await SelectCanonicalAccountFixtureAsync(fixture,runtime).ConfigureAwait(false)).Selection;
        var page=await runtime.ReadPageAsync(selection.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);var asset=page.Page.Assets.Single();
        var calls=(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts);
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>runtime.CaptureViewerIndexAsync(Guid.NewGuid(),directory.IndexPath,asset.Id,null,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.CaptureViewerIndexAsync(Guid.Empty,directory.IndexPath,asset.Id,null,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,Guid.Empty,null,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogSessionSelectionUnavailableException>(()=>runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,asset.Id,new("another-account"),CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogSessionSelectionUnavailableException>(()=>runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,Guid.NewGuid(),null,CancellationToken.None)).ConfigureAwait(false);
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>runtime.CaptureViewerIndexAsync(selection.StageId,directory.IndexPath,asset.Id,null,canceled.Token)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(directory.IndexPath));Assert.AreEqual(calls,(fixture.Http.MetadataReads,fixture.Http.BodyReads,fixture.Http.Puts));Assert.AreEqual(selection,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    private sealed class CanonicalViewerCallbackSpace : IStorageSpaceReader
    {
        internal Action? Observed {get;set;}
        public StorageSpaceSnapshot Read() {Observed?.Invoke();return new(100*StorageBudgetManager.Gibibyte,20*StorageBudgetManager.Gibibyte,0);}
    }
    private sealed class CanonicalViewerTestDirectory : IDisposable
    {
        private readonly string _path=Directory.CreateTempSubdirectory("mpa-canonical-viewer-").FullName;
        internal string IndexPath=>Path.Combine(_path,"viewer.sqlite");
        public void Dispose()=>Directory.Delete(_path);
    }
}
