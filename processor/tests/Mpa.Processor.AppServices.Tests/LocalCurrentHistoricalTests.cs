using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentHistoricalReopenKeepsOldContentOfflineWithoutRetargetingItToANewCloudRoot()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await AddCatalogHeadAsync(fixture.Data,"historical-old",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        CurrentCatalogActivation selected;
        await using(var first=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
        {
            var prepared=await PrepareActivationAsync(fixture,first).ConfigureAwait(false);await using var stage=prepared.Stage;
            selected=await fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None).ConfigureAwait(false);
        }
        var path=await StagePathAsync(fixture,selected.StageId).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        // A newly signed cloud Root and disconnected account are deliberately not
        // captured as authority merely to reopen old, independently verified pages.
        await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var metadata=fixture.Http.MetadataReads;
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var historical=await pool.OpenHistoricalAsync(CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(historical);await using var owner=historical;
        Assert.AreEqual(selected,historical.Selection);Assert.AreEqual(1,(await historical.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(metadata,fixture.Http.MetadataReads);Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>historical.ReadPageAsync(null,60,canceled.Token)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>pool.OpenHistoricalAsync(CancellationToken.None)).ConfigureAwait(false);
        await pool.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(historical.ExitConfirmed);Assert.IsTrue(pool.ExitConfirmed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>historical.ReadPageAsync(null,60,CancellationToken.None)).ConfigureAwait(false);
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));Assert.AreEqual(selected,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentHistoricalOwnerSharesThePoolUntilANewActivationAndItsActualClose()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await AddCatalogHeadAsync(fixture.Data,"historical-parent",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        CurrentCatalogActivation previous;
        await using(var first=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
        {
            var prepared=await PrepareActivationAsync(fixture,first).ConfigureAwait(false);await using var stage=prepared.Stage;
            previous=await fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None).ConfigureAwait(false);
        }
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var historical=await pool.OpenHistoricalAsync(CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(historical);await using var owner=historical;
        var next=await AddCatalogHeadAsync(fixture.Data,"historical-successor",head.Reference,includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,next.Reference).ConfigureAwait(false);
        var candidate=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var pending=candidate.Stage;
        var active=await fixture.Reader.ActivateAsync(candidate.Fence,pending,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(previous,historical.Selection);Assert.AreEqual(1,(await historical.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>pool.RetireAsync(previous.StageId,CancellationToken.None)).ConfigureAwait(false);
        await historical.DisposeAsync().ConfigureAwait(false);await pool.RetireAsync(previous.StageId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(active.StageId,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);
        Assert.AreEqual(1,(await pending.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentHistoricalCorruptionFailsClosedWithoutDeletingClaimOrFile(bool nativeFile)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);CurrentCatalogActivation selected;
        await using(var first=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
        {
            var prepared=await PrepareActivationAsync(fixture,first).ConfigureAwait(false);await using var stage=prepared.Stage;
            selected=await fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None).ConfigureAwait(false);
        }
        var path=await StagePathAsync(fixture,selected.StageId).ConfigureAwait(false);
        if(nativeFile)
        {var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);bytes[^1]^=1;await File.WriteAllBytesAsync(path,bytes).ConfigureAwait(false);}
        else await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"UPDATE processor_meta SET value='{}' WHERE key='current_catalog_active_v1'");return true;},CancellationToken.None).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>pool.OpenHistoricalAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(path));Assert.AreEqual("sealed",(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Phase);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentHistoricalEmptyOrCancelledOpenDoesNotAllocateAnIndexOrCaptureCloudAuthority()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.IsNull(await pool.OpenHistoricalAsync(CancellationToken.None).ConfigureAwait(false));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>pool.OpenHistoricalAsync(canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);Assert.AreEqual(0,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.MetadataReads);
        var area=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>Path.Combine(database.StateDirectory,"current-catalog-stages"),CancellationToken.None).ConfigureAwait(false);Assert.IsFalse(Directory.Exists(area));
    }
}
