using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentBasisPreparesTheRealOriginalSnapshotAndInternallyDerivesStableGenesisInputsAcrossRetry()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await AddCurrentSnapshotFactAsync(fixture,"basis-original").ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        CurrentPreparedCatalogBasis first;
        await using(var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false))
        {first=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);Assert.IsNull(owner.Failure);}
        Assert.AreEqual("publication-planned",first.Publication.Capture.Phase);Assert.AreEqual(1L,first.Publication.Basis.Snapshot.Assets);
        Assert.IsNull(first.Publication.Basis.Parent);Assert.AreEqual(0L,first.Publication.Basis.ParentSequence);
        Assert.AreEqual(first.Sources.EnvelopeSha256,first.Publication.Basis.SourcePolicySha256);Assert.IsNull(first.Publication.Plan);
        var path=await CurrentSnapshotPathAsync(fixture,first.Publication.Capture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        await AddCurrentSnapshotFactAsync(fixture,"basis-later").ConfigureAwait(false);
        await using var resumed=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);var actual=await resumed.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Publication,actual.Publication);Assert.AreEqual(first.Sources.EnvelopeSha256,actual.Sources.EnvelopeSha256);
        Assert.AreEqual(first.Sources.Sequence,actual.Sources.Sequence);CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.IsFalse(fixture.Http.BodyFiles.Contains("basis-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        await resumed.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(resumed.ExitConfirmed);
    }

    [TestMethod]
    public async Task CurrentBasisDerivesTheAcknowledgedActualNativeTipWithoutAcceptingACallerSuppliedParent()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var head=await completion.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var puts=fixture.Http.Puts;
        await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);var prepared=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(head.Commit,prepared.Publication.Basis.Parent);Assert.AreEqual(head.Sequence,prepared.Publication.Basis.ParentSequence);
        Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.AreNotEqual(owned.Capture.Scope.GenerationId,prepared.Publication.Capture.Scope.GenerationId);
        Assert.AreEqual(before.Signing,(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Signing);Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.IsFalse(File.Exists(owned.SnapshotPath));Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentBasisKeepsARetiredUnselectedNativeHeaderAsParentEvenWhenTheActualRootSelectsNoCatalog()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var initial=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var pending=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget)
            .CaptureSigningInventory(initial.Attempt).Roots.Single(value=>value.Disposition==CurrentRootSigningDisposition.Pending).Intent.Id,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
            sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
        },CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.WithdrawTransitionAsync(pending,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var old=await fixture.Reader.SupersedeMaterializedGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(old.SelectedAnchor);Assert.AreEqual(1L,old.Sequence);
        await using(var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
        {Assert.AreEqual(old,await retirement.FinishAsync(old.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));}
        var sourcePolicy=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        var fence=await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);
        var transition=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget).Prepare(
            Guid.NewGuid(),fence.Current.Attempt,new(capture.Head.Publishers,sourcePolicy,null)),CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(transition.Id,CancellationToken.None).ConfigureAwait(false);
        var next=await PrepareReconciledGraphAsync(fixture,owned.Pool).ConfigureAwait(false);
        await using(next.Stage)
        {
            var actual=await fixture.Reader.ObserveSelectedInventoryAsync(next.Graph,CancellationToken.None).ConfigureAwait(false);
            _=await fixture.Reader.ActivateReconciledAsync(actual,next.Stage,CancellationToken.None).ConfigureAwait(false);
        }
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.IsNull(await LineageHeadAsync(fixture).ConfigureAwait(false));
        await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);var result=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(old.OriginalCommit,result.Publication.Basis.Parent);Assert.AreEqual(1L,result.Publication.Basis.ParentSequence);
        Assert.AreEqual(sourcePolicy.Reference.Sha256,result.Publication.Basis.SourcePolicySha256);Assert.IsNull(await LineageHeadAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(before.Signing,(await CurrentBirthStateAsync(fixture).ConfigureAwait(false)).Signing);Assert.IsFalse(File.Exists(owned.SnapshotPath));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentBasisRollsBackItsPairedRecordAndPhaseWhenInsertionOrHumanStateChanges(bool humanDrift)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await AddCurrentSnapshotFactAsync(fixture,"basis-original").ConfigureAwait(false);
        CatalogGenerationCapture frozen;await using(var snapshot=await NewCurrentSnapshotOwnerAsync(fixture).ConfigureAwait(false))
        {frozen=await snapshot.PrepareAsync(CancellationToken.None).ConfigureAwait(false);}
        var path=await CurrentSnapshotPathAsync(fixture,frozen).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,humanDrift
                ? "CREATE TRIGGER reject_current_basis AFTER UPDATE OF phase ON catalog_generation_capture WHEN NEW.phase='publication-planned' BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END"
                : "CREATE TRIGGER reject_current_basis BEFORE INSERT ON catalog_generation_publication BEGIN SELECT RAISE(ABORT,'current basis rollback'); END");return true;
        },CancellationToken.None).ConfigureAwait(false);
        await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);
        try
        {
            if(humanDrift) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<SqliteException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(frozen,await CurrentSnapshotClaimAsync(fixture).ConfigureAwait(false));
            Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(frozen.Scope.GenerationId),CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally
        {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_current_basis");return true;},CancellationToken.None).ConfigureAwait(false);}
        var recovered=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(frozen.Scope,recovered.Publication.Capture.Scope);Assert.AreEqual(frozen.SnapshotId,recovered.Publication.Capture.SnapshotId);
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task CurrentBasisRejectsAnEmptyIdentityAndPreCanceledWorkBeforeAnyProviderIo()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
        Assert.ThrowsExactly<ArgumentException>(()=>fixture.Reader.PrepareCurrentPublicationBasisAsync(Guid.Empty,CancellationToken.None));
        await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>owner.PrepareAsync(canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentBasisActualRevocationPreservesTheOriginalPlanAndPrivateFile()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);var first=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var path=await CurrentSnapshotPathAsync(fixture,first.Publication.Capture).ConfigureAwait(false);var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        _=await RevokeForOriginalAuthorityTestAsync(fixture).ConfigureAwait(false);var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var puts=fixture.Http.Puts;
        await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Captures,after.Captures);
        Assert.AreEqual(first.Publication,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Publication(first.Publication.Capture.Scope.GenerationId),CancellationToken.None).ConfigureAwait(false));
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(path).ConfigureAwait(false));Assert.AreEqual(puts,fixture.Http.Puts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentBasisExitJoinsItsSnapshotChildAndActualReaderBeforeBorrowersRelease(bool stopReader)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var running=owner.PrepareAsync(CancellationToken.None);Task? stopping=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);stopping=stopReader?fixture.Reader.DisposeAsync().AsTask():owner.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);Assert.IsFalse(fixture.Reader.ExitConfirmed);
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);Assert.IsNotNull(stopping);await stopping.ConfigureAwait(false);
        if(stopReader) Assert.IsTrue(fixture.Reader.ExitConfirmed);else Assert.IsTrue(owner.ExitConfirmed);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task CurrentBasisDoesNotRestartOrReinterpretAnAdvancedSignedCapture()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);_=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var reads=fixture.Http.BodyReads;
        await using var owner=await NewCurrentBasisOwnerAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(reads,fixture.Http.BodyReads);
    }

    private static async Task<CurrentCatalogBasisPreparationService> NewCurrentBasisOwnerAsync(CurrentReaderFixture fixture)
    {
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Budget,fixture.Reader);
    }
}
