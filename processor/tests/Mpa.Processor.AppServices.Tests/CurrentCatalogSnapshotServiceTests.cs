using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentSnapshotCopiesRealFactsThenResumesTheSameFileAfterLiveDiscoveryAndOwnerRecreation()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await AddCurrentSnapshotFactAsync(fixture,"snapshot-original").ConfigureAwait(false);
        var calls=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        CatalogGenerationCapture first;
        await using(var owner=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false))
        {first=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);Assert.IsNull(owner.Failure);}
        Assert.AreEqual("frozen",first.Phase);Assert.AreEqual(1L,first.Snapshot!.Assets);
        var path=await CurrentSnapshotPathAsync(fixture,first).ConfigureAwait(false);var original=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        await AddCurrentSnapshotFactAsync(fixture,"snapshot-later").ConfigureAwait(false);
        await using var resumed=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(first,await resumed.PrepareAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("snapshot-original",await ReadCurrentSnapshotFileAsync(fixture,first).ConfigureAwait(false));
        Assert.AreEqual(2,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new DiscoveredCatalogRepository(database).ReadPage().Assets.Count,CancellationToken.None).ConfigureAwait(false));
        CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);
        Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
        Assert.AreEqual(calls,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.IsFalse(fixture.Http.BodyFiles.Contains("snapshot-original"));
        await resumed.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(resumed.ExitConfirmed);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentSnapshotRecoversItsActualClosedFileAfterAtomicSealFailureWithoutRecapturingIt(bool humanDrift)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await AddCurrentSnapshotFactAsync(fixture,"snapshot-original").ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,humanDrift
                ? "CREATE TRIGGER refuse_current_snapshot AFTER UPDATE OF snapshot ON catalog_generation_capture WHEN NEW.phase='frozen' BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END"
                : "CREATE TRIGGER refuse_current_snapshot BEFORE UPDATE OF snapshot ON catalog_generation_capture WHEN NEW.phase='frozen' BEGIN SELECT RAISE(ABORT,'current snapshot seal rollback'); END");return true;
        },CancellationToken.None).ConfigureAwait(false);
        await using var owner=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false);
        try
        {
            if(humanDrift) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<SqliteException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            var claim=await CurrentSnapshotClaimAsync(fixture).ConfigureAwait(false);Assert.AreEqual("capturing",claim.Phase);Assert.IsNull(claim.Snapshot);
            var path=await CurrentSnapshotPathAsync(fixture,claim).ConfigureAwait(false);Assert.IsTrue(File.Exists(path));
            var original=await File.ReadAllBytesAsync(path).ConfigureAwait(false);Assert.IsNull(owner.Failure);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER refuse_current_snapshot");return true;},CancellationToken.None).ConfigureAwait(false);
            await AddCurrentSnapshotFactAsync(fixture,"snapshot-later").ConfigureAwait(false);
            var recovered=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(claim.Scope,recovered.Scope);Assert.AreEqual(claim.SnapshotId,recovered.SnapshotId);Assert.AreEqual(claim.CommitIntentId,recovered.CommitIntentId);
            Assert.AreEqual(1L,recovered.Snapshot!.Assets);Assert.AreEqual("snapshot-original",await ReadCurrentSnapshotFileAsync(fixture,recovered).ConfigureAwait(false));
            CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(path).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);
        }
        finally
        {
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER IF EXISTS refuse_current_snapshot");return true;},CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CurrentSnapshotRefusesAnExistingPartialFileAndKeepsEveryOriginalIdentityAndByte()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var claim=await fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {using var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget);return true;},CancellationToken.None).ConfigureAwait(false);
        var path=await CurrentSnapshotPathAsync(fixture,claim).ConfigureAwait(false);var partial=new byte[]{1,2,3,4};await File.WriteAllBytesAsync(path,partial).ConfigureAwait(false);
        await using var owner=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(claim,await CurrentSnapshotClaimAsync(fixture).ConfigureAwait(false));CollectionAssert.AreEqual(partial,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        Assert.IsNull(owner.Failure);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentSnapshotPublishedRevocationCannotResumeOrReplaceItsFrozenWork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await AddCurrentSnapshotFactAsync(fixture,"snapshot-original").ConfigureAwait(false);
        await using var owner=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false);
        var claim=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);var path=await CurrentSnapshotPathAsync(fixture,claim).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);_=await RevokeForOriginalAuthorityTestAsync(fixture).ConfigureAwait(false);
        var puts=fixture.Http.Puts;
        await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(claim,await CurrentSnapshotClaimAsync(fixture).ConfigureAwait(false));CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        Assert.AreEqual(puts,fixture.Http.Puts);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentSnapshotExitJoinsAnUncooperativeActualRootReadBeforeItsBorrowedOwnersCanRelease(bool stopReader)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await using var owner=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        Task? stopping=null;var running=owner.PrepareAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            stopping=stopReader ? fixture.Reader.DisposeAsync().AsTask() : owner.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);
            Assert.AreEqual(0,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Active().Count,CancellationToken.None).ConfigureAwait(false));
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);Assert.IsNotNull(stopping);await stopping.ConfigureAwait(false);
        if(stopReader) Assert.IsTrue(fixture.Reader.ExitConfirmed);else Assert.IsTrue(owner.ExitConfirmed);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task CurrentSnapshotDoesNotReinterpretSignedPendingWorkAsAFreshPrivateCapture()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        _=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await using var owner=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Captures,after.Captures);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentSnapshotConstructorRejectsAnotherBudgetOwnerBeforeAnyWorkStarts()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        var otherBudget=new StorageBudgetManager(new ProcessorStateStorageReader(input.StateDirectory));
        Assert.ThrowsExactly<InvalidOperationException>(()=>new CurrentCatalogSnapshotService(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,otherBudget,fixture.Reader));
        Assert.AreEqual(0,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,otherBudget.Status().ReservedBytes);
    }

    private static async Task<CurrentCatalogSnapshotService> NewCurrentSnapshotOwnerAsync(CurrentReaderFixture fixture)
    {
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Budget,fixture.Reader);
    }
    private static Task<bool> AddCurrentSnapshotFactAsync(CurrentReaderFixture fixture,string id)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
    {
        var source=new LibrarySourceRepository(database).Snapshot().Sources.Single();
        new DiscoveredCatalogRepository(database).ApplyBatch(source.Id,source.Revision,1,[new(id,1,id+".jpg","image/jpeg",12,8,null,null,null,0)]);return true;
    },CancellationToken.None);
    private static Task<CatalogGenerationCapture> CurrentSnapshotClaimAsync(CurrentReaderFixture fixture)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Active().Single(),CancellationToken.None);
    private static Task<string> CurrentSnapshotPathAsync(CurrentReaderFixture fixture,CatalogGenerationCapture claim)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        Path.Combine(database.StateDirectory,"catalog-generation-snapshots","snapshot-"+claim.SnapshotId.ToString("N")+".sqlite"),CancellationToken.None);
    private static Task<string> ReadCurrentSnapshotFileAsync(CurrentReaderFixture fixture,CatalogGenerationCapture claim)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
    {
        using var store=new CatalogGenerationSnapshotStore(database.StateDirectory,database,fixture.Data.Budget);using var frozen=store.Open(claim,CancellationToken.None);
        Assert.AreEqual(1L,frozen.Count);return frozen.ReadPage("").Assets.Single().FileId;
    },CancellationToken.None);
}
