using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentPublicationOwnerSelectsActualFrozenContentRetiresOnlyItsPrivateSnapshotAndCanPublishTheNextGeneration(bool next)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;var human=(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Human;
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        await using var owner=await NewCurrentPublicationOwnerAsync(fixture,canonical,pool).ConfigureAwait(false);
        var first=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Generation,first.GenerationId);Assert.AreEqual(1L,first.Sequence);Assert.AreEqual(1L,first.Assets);
        Assert.AreEqual(creates+5,fixture.Data.Creates);Assert.AreEqual(allocations+5,fixture.Data.Allocations);Assert.AreEqual(1,fixture.Http.Puts);
        await AssertCurrentPublicationCompletedAsync(fixture,canonical,first,owned.Path).ConfigureAwait(false);
        var firstSelection=canonical.Selection;Assert.IsNotNull(firstSelection);var last=first;
        if(next)
        {
            await AddCurrentSnapshotFactAsync(fixture,"second-current-publication-photo").ConfigureAwait(false);
            CurrentPreparedCatalogBasis basis;await using(var preparation=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false))
                basis=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            var path=await CurrentSnapshotPathAsync(fixture,basis.Publication.Capture).ConfigureAwait(false);
            InstallPreparedGraphCreates(fixture,basis.Publication.Capture.Scope);
            last=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(first.GenerationId,last.GenerationId);Assert.AreEqual(2L,last.Sequence);Assert.AreEqual(2L,last.Assets);Assert.AreEqual(2,fixture.Http.Puts);
            await AssertCurrentPublicationCompletedAsync(fixture,canonical,last,path).ConfigureAwait(false);
            Assert.AreNotEqual(firstSelection.StageId,canonical.Selection!.StageId);
            var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentCatalogGenerationSelectionRepository(database,fixture.Data.Budget).Read(first.GenerationId,CancellationToken.None)!,CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(first.Commit,retained.Activation.Catalog);Assert.AreEqual(firstSelection.StageId,retained.Activation.StageId);
        }
        Assert.AreEqual(human,(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Human);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));Assert.IsFalse(fixture.Http.BodyFiles.Contains("second-current-publication-photo"));
        var account=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new DriveAccountRepository(database).Accounts().Single(value=>value.PermissionId=="permission-A"),CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",account.Revision,CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        Assert.AreEqual(first,await owner.ResumeSelectedAsync(first.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(last,await owner.ResumeSelectedAsync(last.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));
        await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);Assert.IsNull(owner.Failure);
        Assert.IsFalse(canonical.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);
        var offline=await canonical.ReadPageAsync(null,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(offline);Assert.AreEqual(last.Assets,(long)offline.Page.Assets.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentPublicationOwnerLostRootReplyResumesItsExactPlanHeaderAndFirstSignatureWithoutAnotherPut(bool restart)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        var owner=await NewCurrentPublicationOwnerAsync(fixture,canonical,pool).ConfigureAwait(false);
        try
        {
            fixture.Http.LosePutAcknowledgement=true;
            await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1,fixture.Http.Puts);Assert.IsNull(canonical.Selection);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
            CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
            var plan=await CurrentPublicationPlanAsync(fixture,owned.Generation).ConfigureAwait(false);
            var signing=(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Signing;var signed=await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false);
            var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
            if(restart) {await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);owner=await NewCurrentPublicationOwnerAsync(fixture,canonical,pool).ConfigureAwait(false);}
            var result=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(plan,await CurrentPublicationPlanAsync(fixture,owned.Generation).ConfigureAwait(false));
            Assert.AreEqual(signing,(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Signing);
            CollectionAssert.AreEqual(signed.Envelope!.Value.ToArray(),(await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Envelope!.Value.ToArray());
            Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));
            await AssertCurrentPublicationCompletedAsync(fixture,canonical,result,owned.Path).ConfigureAwait(false);
        }
        finally {await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);}
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentPublicationOwnerCancelOrExitJoinsAnActualUncooperativeRootPutWithoutLateSelectionOrSnapshotDeletion(bool exit)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        var plan=await PrepareCurrentPublicationRootForTestAsync(owned).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        await using var owner=await NewCurrentPublicationOwnerAsync(fixture,canonical,pool).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOnExit=new TestCallbackReleaseOwner(release);using var caller=new CancellationTokenSource();
        fixture.Http.AfterPut=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var running=owner.PublishAsync(caller.Token);var stopping=Task.CompletedTask;
        try
        {
            _=await Task.WhenAny(entered.Task,running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(running.IsCompleted) {await running.ConfigureAwait(false);Assert.IsTrue(entered.Task.IsCompleted);}
            if(exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);
            Assert.IsFalse(canonical.ExitConfirmed);Assert.IsFalse(pool.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);Assert.IsFalse(fixture.Http.Disposed);
            Assert.IsTrue(File.Exists(owned.Path));Assert.IsNull(canonical.Selection);Assert.AreEqual(1,fixture.Http.Puts);
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(exit,owner.ExitConfirmed);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));fixture.Http.AfterPut=null;
        Assert.AreEqual(plan,await CurrentPublicationPlanAsync(fixture,owned.Generation).ConfigureAwait(false));
        await using var resumed=await NewCurrentPublicationOwnerAsync(fixture,canonical,pool).ConfigureAwait(false);
        var result=await resumed.PublishAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(1,fixture.Http.Puts);
        await AssertCurrentPublicationCompletedAsync(fixture,canonical,result,owned.Path).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentPublicationOwnerCanonicalTransactionFailureRetainsTheWrittenRootAndFrozenWorkForExactRetry(bool selectionReceipt)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        await using var owner=await NewCurrentPublicationOwnerAsync(fixture,canonical,pool).ConfigureAwait(false);
        var condition=selectionReceipt?"NEW.key GLOB 'current_catalog_generation_selection_v1:*'":"NEW.key='current_catalog_active_v1'";
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"CREATE TRIGGER reject_publication_owner BEFORE INSERT ON processor_meta WHEN "+condition+" BEGIN SELECT RAISE(ABORT,'actual current publication activation rollback'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        CurrentControlTransitionPlan plan;string signing;
        try
        {
            await Assert.ThrowsExactlyAsync<SqliteException>(()=>owner.PublishAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1,fixture.Http.Puts);Assert.IsNull(canonical.Selection);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
            Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentCatalogGenerationSelectionRepository(database,fixture.Data.Budget).Read(owned.Generation,CancellationToken.None),CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
            Assert.AreEqual("commit-sealed",(await ReadCompletionCaptureAsync(fixture.Data,owned.Generation).ConfigureAwait(false)).Phase);
            plan=await CurrentPublicationPlanAsync(fixture,owned.Generation).ConfigureAwait(false);signing=(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Signing;
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_publication_owner");return true;},CancellationToken.None).ConfigureAwait(false);}
        var writes=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        var result=await owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(plan,await CurrentPublicationPlanAsync(fixture,owned.Generation).ConfigureAwait(false));Assert.AreEqual(signing,(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Signing);
        Assert.AreEqual(writes,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));
        await AssertCurrentPublicationCompletedAsync(fixture,canonical,result,owned.Path).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentPublicationOwnerConstructorRejectsForeignBudgetPoolAndCanonicalBeforeAnyProviderWork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        var foreignBudget=new StorageBudgetManager(new ProcessorStateStorageReader(input.StateDirectory));
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var other=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        await using var canonical=new CurrentCanonicalReadRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Budget,fixture.Reader,pool);
        CurrentCatalogPublicationService New(StorageBudgetManager budget,LocalCurrentStagePool selected)=>new(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,budget,fixture.Reader,canonical,selected);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Creates,fixture.Data.Allocations);
        Assert.ThrowsExactly<InvalidOperationException>(()=>New(foreignBudget,pool));Assert.ThrowsExactly<InvalidOperationException>(()=>New(fixture.Data.Budget,other));
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Creates,fixture.Data.Allocations));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);Assert.AreEqual(0L,foreignBudget.Status().ReservedBytes);
    }

    private static async Task<CurrentCatalogPublicationService> NewCurrentPublicationOwnerAsync(CurrentReaderFixture fixture,CurrentCanonicalReadRuntime canonical,LocalCurrentStagePool pool)
    {
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader,canonical,pool);
    }
    private static Task<CurrentControlTransitionPlan> CurrentPublicationPlanAsync(CurrentReaderFixture fixture,Guid generation)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        new CurrentControlTransitionJournal(database,fixture.Data.Budget).ReadAll().Single(value=>value.GenerationId==generation),CancellationToken.None);
    private static async Task<CurrentControlTransitionPlan> PrepareCurrentPublicationRootForTestAsync(CurrentDependencyFixture owned)
    {
        var fixture=owned.Fixture;WorkspacePublicationRecord header;
        await using(var preparation=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false)) header=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var p0=await fixture.Reader.InspectCatalogHeaderCloudAsync(inventory,owned.Generation,CancellationToken.None).ConfigureAwait(false);
        await using(var allocation=new CurrentCatalogHeaderAllocationService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget,fixture.Reader))
            _=await allocation.AllocateAsync(p0,CancellationToken.None).ConfigureAwait(false);
        var p1=await fixture.Reader.InspectCatalogHeaderCloudAsync(await FreshLineageInventoryAsync(fixture).ConfigureAwait(false),owned.Generation,CancellationToken.None).ConfigureAwait(false);
        await using(var upload=new CurrentCatalogHeaderUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader)) _=await upload.UploadAsync(p1,CancellationToken.None).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        inventory=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        return await fixture.Reader.PrepareCurrentCatalogTransitionAsync(Guid.NewGuid(),inventory,new(capture.Head.Publishers,capture.Head.Sources,header.Id),CancellationToken.None).ConfigureAwait(false);
    }
    private static async Task AssertCurrentPublicationCompletedAsync(CurrentReaderFixture fixture,CurrentCanonicalReadRuntime canonical,PublishedCatalogHead result,string path)
    {
        Assert.IsFalse(File.Exists(path));Assert.AreEqual("retired",(await ReadCompletionCaptureAsync(fixture.Data,result.GenerationId).ConfigureAwait(false)).Phase);
        var selected=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentCatalogGenerationSelectionRepository(database,fixture.Data.Budget).Read(result.GenerationId,CancellationToken.None)!,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(selected);Assert.AreEqual(result.Commit,selected.Activation.Catalog);Assert.AreEqual(selected.Activation.StageId,canonical.Selection!.StageId);
        var page=await canonical.ReadPageAsync(selected.Activation.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);
        Assert.AreEqual(result.Assets,(long)page.Page.Assets.Count);Assert.AreEqual(result,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead(),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));
    }
}
