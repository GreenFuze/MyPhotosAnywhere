using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentStagePoolKeepsHistoricalPagesWhilePreparingAndRetiringAnotherIndex()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selected=await AddCatalogHeadAsync(fixture.Data,"pool-selected",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,selected.Reference).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);var firstId=Guid.NewGuid();var secondId=Guid.NewGuid();
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        await using var first=await pool.OpenAsync(capture,firstId,CancellationToken.None).ConfigureAwait(false);
        var firstGraph=await fixture.Reader.ReadSelectedGraphAsync(policies,first,CancellationToken.None).ConfigureAwait(false);await first.SealAsync(firstGraph,CancellationToken.None).ConfigureAwait(false);
        var original=(await first.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Single();
        var codec=new DriveCatalogFacts(selected.Plan.Scope.LibraryId);var prefix=original.Id.ToString("N")[..2];var originalBytes=codec.Encode(prefix,[original]);
        var nextCapture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var nextPolicies=await fixture.Reader.ReadSelectedPoliciesAsync(nextCapture,CancellationToken.None).ConfigureAwait(false);
        await using var second=await pool.OpenAsync(nextCapture,secondId,CancellationToken.None).ConfigureAwait(false);
        var nextGraph=await fixture.Reader.ReadSelectedGraphAsync(nextPolicies,second,CancellationToken.None).ConfigureAwait(false);await second.SealAsync(nextGraph,CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(originalBytes,codec.Encode(prefix,[(await first.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Single()]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>pool.RetireAsync(firstId,CancellationToken.None)).ConfigureAwait(false);
        await first.DisposeAsync().ConfigureAwait(false);await pool.RetireAsync(firstId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(await StagePathAsync(fixture,firstId).ConfigureAwait(false)));Assert.AreEqual(secondId,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);
        CollectionAssert.AreEqual(originalBytes,codec.Encode(prefix,[(await second.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Single()]));
        await fixture.Reader.FenceSelectedGraphAsync(nextGraph,CancellationToken.None).ConfigureAwait(false);
        await pool.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(second.ExitConfirmed);Assert.IsTrue(pool.ExitConfirmed);
    }

    [TestMethod]
    public async Task LocalCurrentStagePoolLowStorageCancellationAndCapacityNeverReplaceExistingClaims()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,budget);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>pool.OpenAsync(capture,Guid.NewGuid(),canceled.Token)).ConfigureAwait(false);Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
        var firstId=Guid.NewGuid();await using var first=await pool.OpenAsync(capture,firstId,CancellationToken.None).ConfigureAwait(false);
        var firstClaim=(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single();space.Available=1;
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>pool.OpenAsync(capture,Guid.NewGuid(),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(firstClaim,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single());space.Available=20*StorageBudgetManager.Gibibyte;
        await using var second=await pool.OpenAsync(capture,Guid.NewGuid(),CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogPreparationCapacityException>(()=>pool.OpenAsync(capture,Guid.NewGuid(),CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>pool.OpenAsync(capture,firstId,CancellationToken.None)).ConfigureAwait(false);
        await pool.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(first.ExitConfirmed);Assert.IsTrue(second.ExitConfirmed);Assert.AreEqual(2,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>pool.OpenAsync(capture,Guid.NewGuid(),CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentStagePoolExitJoinsBlockedNativeWorkBeforeReleasingTheSharedArea()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selected=await AddCatalogHeadAsync(fixture.Data,"pool-joining",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,selected.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        CatalogFactsLeaf? leaf=null;await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage {Hook=value=>{leaf=value;return Task.CompletedTask;}},CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(leaf);
        using var space=new CurrentStageBlockedSpace();await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,new(space));var id=Guid.NewGuid();
        await using var stage=await pool.OpenAsync(capture,id,CancellationToken.None).ConfigureAwait(false);space.Armed=true;var writing=stage.AddAsync(leaf,CancellationToken.None);
        try
        {
            await space.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var stopping=pool.DisposeAsync().AsTask();Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(pool.ExitConfirmed);space.Release.Set();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>writing).ConfigureAwait(false);await stopping.ConfigureAwait(false);Assert.IsTrue(pool.ExitConfirmed);Assert.IsTrue(stage.ExitConfirmed);
        }
        finally {space.Release.Set();}
        var next=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var recovered=await LocalCurrentSqliteStage.CreateAsync(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,next,id,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(id,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);
    }
}
