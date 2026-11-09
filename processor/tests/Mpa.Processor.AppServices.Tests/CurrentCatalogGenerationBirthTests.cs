using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentBirthAdmitsOneNewCaptureWithoutSigningUploadingOrCompletingLegacyObservation()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Creates,fixture.Data.Allocations);
        var capture=await fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("capturing",capture.Phase);Assert.IsNull(capture.Snapshot);
        Assert.AreNotEqual(Guid.Empty,capture.Scope.GenerationId);Assert.AreNotEqual(Guid.Empty,capture.SnapshotId);Assert.AreNotEqual(Guid.Empty,capture.CommitIntentId);
        var actual=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var local=new LibraryAuthorityRepository(database,fixture.Data.Budget).CaptureLocalPublicationAuthority();
            Assert.AreEqual(new WorkspacePublicationScope(local.LibraryId,local.ActorId,local.ActorEpoch,capture.Scope.GenerationId,
                local.AccountId,local.AccountRevision,local.FolderId,local.ConfigurationRevision,local.AuthoritySha256),capture.Scope);
            return new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Read(capture.Scope.GenerationId);
        },CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(capture,actual);var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);
        Assert.AreEqual(before.Inventory,after.Inventory);Assert.AreEqual(before.Legacy,after.Legacy);Assert.AreEqual(before.Head,after.Head);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Creates,fixture.Data.Allocations));
        await Assert.ThrowsExactlyAsync<CatalogGenerationInProgressException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(after,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentBirthRejectsForeignReaderAndSupersededInventoryWithoutAllocatingNewIdentities(bool foreign)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        if(!foreign) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>(foreign?other:fixture.Reader)
            .BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentBirthRollsBackFailedInsertAndHumanMutationInsideTheBirthTransaction(bool changeHuman)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,changeHuman
                ? "CREATE TRIGGER reject_current_birth AFTER INSERT ON catalog_generation_capture BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END"
                : "CREATE TRIGGER reject_current_birth BEFORE INSERT ON catalog_generation_capture BEGIN SELECT RAISE(ABORT,'birth rollback fixture'); END");return true;
        },CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        try
        {
            if(changeHuman) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        finally
        {
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            {CurrentStageSql(database,"DROP TRIGGER reject_current_birth");return true;},CancellationToken.None).ConfigureAwait(false);
        }
        var capture=await fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("capturing",capture.Phase);
    }

    [TestMethod]
    public async Task CurrentBirthRejectsActualRevocationAndPreCanceledAdmissionWithoutChangingHistory()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        _=await RevokeForOriginalAuthorityTestAsync(fixture).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentBirthLeavesUnfinishedRootAndOriginalGenerationWorkForTheirExactRecoveryOwners(bool pendingRoot)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        if(pendingRoot) _=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        else _=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        if(pendingRoot) await Assert.ThrowsExactlyAsync<CurrentControlTransitionInProgressException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<CatalogGenerationInProgressException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task CurrentBirthChecksTheActualNativeTipAfterSelectedCompletionAndPrivateSnapshotRetirement()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var head=await completion.FinishAsync(owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(owned.SnapshotPath));var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var puts=fixture.Http.Puts;
        var capture=await fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(owned.Capture.Scope.GenerationId,capture.Scope.GenerationId);Assert.AreEqual("capturing",capture.Phase);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Head,after.Head);
        Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentBirthDoesNotTreatAnUnwitnessedImmutableJsonBodyAsProofOfSafeNativeHistory()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var bytes=System.Text.Encoding.UTF8.GetBytes("{\"unwitnessed\":true}");
        fixture.Data.Cloud.Add("unwitnessed-body",new("permission-A","unwitnessed-body","metadata",8,bytes));
        InstallCurrentInventoryTransport(fixture);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>fixture.Reader.BeginNewCurrentGenerationAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);
        CollectionAssert.AreEqual(bytes,fixture.Data.Cloud["unwitnessed-body"].Bytes.ToArray());
    }

    private static Task<(string Signing,string Publications,string Inventory,string Legacy,string Head,string Captures,long Human)> CurrentBirthStateAsync(CurrentReaderFixture fixture)=>
        fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var authority=new LibraryAuthorityRepository(database,fixture.Data.Budget);var pin=authority.Controller()!;
            var account=new DriveAccountRepository(database).Accounts().Single(value=>value.PermissionId==pin.AccountId);
            var signing=new ProtocolSigningJournal(database,fixture.Data.Budget).Inventory(pin.LibraryId).Sha256;
            var publications=new WorkspacePublicationRepository(database,fixture.Data.Budget).Inventory(new(pin.LibraryId,pin.AccountId,pin.FolderId,account.Revision,null)).Sha256;
            var observations=new WorkspaceObservationRepository(database,fixture.Data.Budget);var captures=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget);
            return(signing,publications,JsonSerializer.Serialize(observations.ReadCurrentInventory()),JsonSerializer.Serialize(observations.LastCheckpoint()),
                JsonSerializer.Serialize(captures.PublishedHead()),JsonSerializer.Serialize(captures.Active()),new LibrarySourceRepository(database).Snapshot().Revision);
        },CancellationToken.None);
}
