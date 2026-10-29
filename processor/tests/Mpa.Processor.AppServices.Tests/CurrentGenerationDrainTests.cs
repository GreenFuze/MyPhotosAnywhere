using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentGenerationDrainFinishesOriginalWorkThenNewDiscoveryAndRechecksRealRootDespiteMatchingLocalHints(bool importDuringUpload)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;var imported=false;
        if(importDuringUpload) fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,payload,hash,token)=>
        {
            var actual=await create(folder,file,mime,payload,hash,token).ConfigureAwait(false);
            if(!imported) {imported=true;await AddCurrentSnapshotFactAsync(fixture,"discovered-after-first-current-freeze").ConfigureAwait(false);}
            return actual;
        };
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        await using var runtime=await NewCurrentGenerationDrainAsync(fixture,canonical,pool).ConfigureAwait(false);
        var head=await runtime.RunUntilCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(importDuringUpload?2L:1L,head.Sequence);Assert.AreEqual(importDuringUpload?2L:1L,head.Assets);Assert.AreEqual(importDuringUpload?2:1,fixture.Http.Puts);
        Assert.IsFalse(File.Exists(owned.Path));Assert.AreEqual("retired",(await ReadCompletionCaptureAsync(fixture.Data,owned.Generation).ConfigureAwait(false)).Phase);
        var first=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentCatalogGenerationSelectionRepository(database,fixture.Data.Budget).Read(owned.Generation,CancellationToken.None)!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L,first.Sequence);Assert.AreEqual(1L,first.Assets);
        var inputs=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var reader=new CatalogGenerationInputReader(database,fixture.Data.Budget);Assert.AreEqual(0,new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Active().Count);
            return (Current:reader.Current(CancellationToken.None),Published:reader.Published(CancellationToken.None));
        },CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(inputs.Current,inputs.Published);
        var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);var before=canonical.Selection;Assert.IsNotNull(before);
        // A physically different provider version of the identical signed Root
        // invalidates retained current evidence while local scheduling hints match.
        var root=fixture.Data.Cloud["captured-3"];fixture.Data.Cloud[root.FileId]=root with {DriveVersion=root.DriveVersion+1};
        var again=await runtime.RunUntilCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(head,again);Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));
        Assert.AreEqual(before.DriveVersion+1,canonical.Selection!.DriveVersion);Assert.AreEqual(before.RootSha256,canonical.Selection.RootSha256);Assert.AreNotEqual(before.FencedAttempt,canonical.Selection.FencedAttempt);
        var page=await canonical.ReadPageAsync(null,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);Assert.AreEqual(head.Assets,(long)page.Page.Assets.Count);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));Assert.IsFalse(fixture.Http.BodyFiles.Contains("discovered-after-first-current-freeze"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        await runtime.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(runtime.ExitConfirmed);Assert.IsNull(runtime.Failure);
        Assert.IsFalse(canonical.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentGenerationDrainCancelOrExitJoinsPhysicalRootAndResumesRetainedWorkBeforeAdmittingReady(bool exit)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);var plan=await PrepareCurrentPublicationRootForTestAsync(owned).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        await using var runtime=await NewCurrentGenerationDrainAsync(fixture,canonical,pool).ConfigureAwait(false);
        using var caller=new CancellationTokenSource();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOnExit=new TestCallbackReleaseOwner(release);fixture.Http.AfterPut=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var running=runtime.RunUntilCurrentAsync(caller.Token);var stopping=Task.CompletedTask;
        try
        {
            _=await Task.WhenAny(entered.Task,running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(running.IsCompleted) {await running.ConfigureAwait(false);Assert.IsTrue(entered.Task.IsCompleted);}
            if(exit) stopping=runtime.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(runtime.ExitConfirmed);
            Assert.IsFalse(canonical.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);Assert.IsTrue(File.Exists(owned.Path));Assert.IsNull(canonical.Selection);
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(exit,runtime.ExitConfirmed);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.AreEqual(plan,await CurrentPublicationPlanAsync(fixture,owned.Generation).ConfigureAwait(false));fixture.Http.AfterPut=null;
        await using var resumed=await NewCurrentGenerationDrainAsync(fixture,canonical,pool).ConfigureAwait(false);
        var result=await resumed.RunUntilCurrentAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(owned.Generation,result.GenerationId);Assert.AreEqual(1,fixture.Http.Puts);
        await AssertCurrentPublicationCompletedAsync(fixture,canonical,result,owned.Path).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentGenerationDrainDoesNotClaimCurrentReadyOfflineAndPreservesItsActualHistoricalPhotoReader()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        await using var runtime=await NewCurrentGenerationDrainAsync(fixture,canonical,pool).ConfigureAwait(false);
        var head=await runtime.RunUntilCurrentAsync(CancellationToken.None).ConfigureAwait(false);var selected=canonical.Selection;Assert.IsNotNull(selected);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",selected.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>runtime.RunUntilCurrentAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.AreEqual(selected,canonical.Selection);
        var page=await canonical.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);Assert.AreEqual(head.Assets,(long)page.Page.Assets.Count);Assert.IsNull(runtime.Failure);
    }

    [TestMethod]
    public async Task CurrentGenerationDrainHistoricalSelectedCleanupCannotBecomeCurrentReadyWithoutActualNewAdmission()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"CREATE TRIGGER reject_drain_head BEFORE INSERT ON processor_meta WHEN NEW.key='catalog_published_head_v1' BEGIN SELECT RAISE(ABORT,'actual drain completion rollback'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        try
        {
            await using var publication=await NewCurrentPublicationOwnerAsync(fixture,canonical,pool).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<SqliteException>(()=>publication.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_drain_head");return true;},CancellationToken.None).ConfigureAwait(false);}
        Assert.IsTrue(File.Exists(owned.Path));var selected=canonical.Selection;Assert.IsNotNull(selected);Assert.AreEqual(1,fixture.Http.Puts);
        Assert.IsNotNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentCatalogGenerationSelectionRepository(database,fixture.Data.Budget).Read(owned.Generation,CancellationToken.None),CancellationToken.None).ConfigureAwait(false));
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",selected.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        await using var runtime=await NewCurrentGenerationDrainAsync(fixture,canonical,pool).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>runtime.RunUntilCurrentAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.AreEqual(selected,canonical.Selection);
        Assert.IsFalse(File.Exists(owned.Path));Assert.AreEqual("retired",(await ReadCompletionCaptureAsync(fixture.Data,owned.Generation).ConfigureAwait(false)).Phase);
        var page=await canonical.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);Assert.AreEqual(1,page.Page.Assets.Count);Assert.IsNull(runtime.Failure);
    }

    [TestMethod]
    public async Task CurrentGenerationDrainRejectsForeignConcreteOwnersBeforeNativeOrProviderWork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var other=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        Assert.ThrowsExactly<InvalidOperationException>(()=>new CurrentCatalogGenerationRuntime(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader,canonical,other));
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    private static async Task<CurrentCatalogGenerationRuntime> NewCurrentGenerationDrainAsync(CurrentReaderFixture fixture,CurrentCanonicalReadRuntime canonical,LocalCurrentStagePool pool)
    {
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader,canonical,pool);
    }
}
