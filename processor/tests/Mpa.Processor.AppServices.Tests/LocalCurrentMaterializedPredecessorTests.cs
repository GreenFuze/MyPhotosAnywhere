using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentMaterializedPredecessorKeepsOriginalCapturedWorkSeparateFromActualSelectedAuthorityWithoutCleanupOrNetwork()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var before=await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var puts=fixture.Http.Puts;
        var ready=await fixture.Reader.InspectMaterializedPredecessorAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Capture.Scope,ready.Original.OriginalScope);Assert.AreEqual(owned.Capture.SnapshotId,ready.Original.SnapshotId);
        Assert.AreEqual(owned.Capture.CommitIntentId,ready.Original.CommitIntentId);Assert.AreEqual(1L,ready.Original.Sequence);
        Assert.IsNull(ready.Original.SelectedAnchor);Assert.AreEqual(1,ready.Original.WithdrawnRootPlans);
        Assert.IsTrue(ready.Original.HumanConfigurationRevision>owned.Capture.Scope.ConfigurationRevision);
        await fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
        CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(owned.SnapshotPath).ConfigureAwait(false));
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead(),CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await owned.SelectionAsync().ConfigureAwait(false));Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(owned.SnapshotPath));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentMaterializedPredecessorRejectsAnObsoleteButStillPendingRootAndAcceptsOnlyItsOriginalWithdrawal()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:false).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlTransitionInProgressException>(()=>fixture.Reader.InspectMaterializedPredecessorAsync(
            inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.IsTrue(File.Exists(owned.SnapshotPath));
        var plan=await MaterializedOriginalRootPlanAsync(owned).ConfigureAwait(false);
        _=await fixture.Reader.WithdrawTransitionAsync(plan.Id,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        inventory=await fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var ready=await fixture.Reader.InspectMaterializedPredecessorAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1,ready.Original.WithdrawnRootPlans);Assert.AreEqual(owned.Capture.Scope,ready.Original.OriginalScope);
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("revision")]
    [DataRow("allocation")]
    [DataRow("unversioned")]
    public async Task CurrentMaterializedPredecessorChangedHumanBindingsOrPendingAllocationInvalidatesTheExactInspection(string mutation)
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var id=Guid.NewGuid();var pending=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new WorkspacePublicationRepository(database,fixture.Data.Budget).Prepare(id,owned.Capture.Scope with {GenerationId=Guid.NewGuid()},
                "facts-index","application/json","{}"u8),CancellationToken.None).ConfigureAwait(false);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var ready=await fixture.Reader.InspectMaterializedPredecessorAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            // This is the original already-captured pending metadata identity.
            // Recording its synthetic allocation never uploads obsolete facts.
            if(mutation=="allocation") new WorkspacePublicationRepository(database,fixture.Data.Budget).AssignFileId(id,pending.Sha256,"newly-allocated-pending-metadata");
            else if(mutation=="unversioned") CurrentStageSql(database,"UPDATE source_binding SET enabled=1");
            else
            {
                var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
                sources.Update(source.Id,true,source.IncludeDescendants,source.Revision,snapshot.Revision);
            }
            return true;
        },CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;
        if(mutation=="allocation") await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None)).ConfigureAwait(false);
        else if(mutation=="unversioned") await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.IsTrue(File.Exists(owned.SnapshotPath));
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
        Assert.IsNotNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new WorkspacePublicationRepository(database,fixture.Data.Budget).Read(id),CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentMaterializedPredecessorCannotTreatAnActuallySelectedGenerationAsAnUnselectedParent()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var selection=await owned.SelectionAsync().ConfigureAwait(false);Assert.IsNotNull(selection);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:false).ConfigureAwait(false);var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Reader.InspectMaterializedPredecessorAsync(
            inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(selection,await owned.SelectionAsync().ConfigureAwait(false));
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
        Assert.IsTrue(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentMaterializedPredecessorCannotBorrowAnotherReaderOrSurviveItsOriginalReadersExit()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var ready=await fixture.Reader.InspectMaterializedPredecessorAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.InspectMaterializedPredecessorAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None)).ConfigureAwait(false);
        await fixture.Reader.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(fixture.Reader.ExitConfirmed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.IsTrue(File.Exists(owned.SnapshotPath));
    }

    [TestMethod]
    public async Task CurrentMaterializedPredecessorAcceptsTheExistingMaximumUnicodeSourceNameWithoutUploadingItsFacts()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);
        var latest=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sources=new LibrarySourceRepository(database);var old=sources.Snapshot();
            return sources.Add(Guid.NewGuid(),owned.Capture.Scope.AccountId,owned.Capture.Scope.AccountRevision,
                "source-with-long-name",new string('\u05e9',1024),true,old.Revision);
        },CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;var puts=fixture.Http.Puts;
        var ready=await fixture.Reader.InspectMaterializedPredecessorAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(latest.Revision,ready.Original.HumanConfigurationRevision);
        await fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(puts,fixture.Http.Puts);Assert.IsTrue(File.Exists(owned.SnapshotPath));
        Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentMaterializedPredecessorPreservesAnExistingLongConfigurationCounterWithoutNarrowingItToAJsonSafeInteger()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            // Seed an otherwise valid existing64-bit counter; use the normal
            // optimistic update/canonical outbox to perform its next increment.
            CurrentStageSql(database,"UPDATE library_configuration SET revision=9007199254740992 WHERE id=1");return true;
        },CancellationToken.None).ConfigureAwait(false);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:true).ConfigureAwait(false);var reads=fixture.Http.BodyReads;
        var ready=await fixture.Reader.InspectMaterializedPredecessorAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(9007199254740993L,ready.Original.HumanConfigurationRevision);
        await fixture.Reader.AssertMaterializedPredecessorUnchangedAsync(ready,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.IsTrue(File.Exists(owned.SnapshotPath));
        Assert.AreEqual(owned.Capture.Scope,ready.Original.OriginalScope);Assert.AreEqual(owned.Capture,await ReadCompletionCaptureAsync(fixture.Data,owned.Capture.Scope.GenerationId).ConfigureAwait(false));
    }

    private static async Task<LocalCurrentWorkspaceInventory> MaterializedInspectionInventoryAsync(CurrentCompletionFixture owned,bool withdraw)
    {
        var fixture=owned.Fixture;await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
            sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
        },CancellationToken.None).ConfigureAwait(false);
        if(withdraw)
        {
            var plan=await MaterializedOriginalRootPlanAsync(owned).ConfigureAwait(false);
            _=await fixture.Reader.WithdrawTransitionAsync(plan.Id,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
        }
        var captured=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        return await fixture.Reader.ObserveWorkspaceInventoryAsync(captured,CancellationToken.None).ConfigureAwait(false);
    }
    private static Task<CurrentControlTransitionPlan> MaterializedOriginalRootPlanAsync(CurrentCompletionFixture owned)=>
        owned.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,owned.Fixture.Data.Budget).ReadAll()
            .Single(plan=>plan.GenerationId==owned.Capture.Scope.GenerationId),CancellationToken.None);
}
