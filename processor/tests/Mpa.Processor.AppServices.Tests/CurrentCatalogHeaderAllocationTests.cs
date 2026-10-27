using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentHeaderAllocationRetainsOnlyTheExactOriginalIdAndRequiresNewInventoryBeforeAnotherOperation()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var cloud=fixture.Data.Cloud.Count;var creates=fixture.Data.Creates;var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("known-original-header");};
        await using var owner=HeaderAllocator(owned);
        var row=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Id,row.Id);Assert.AreEqual(owned.Pending.Capture.Scope,row.Scope);
        Assert.AreEqual("known-original-header",row.FileId);Assert.IsNull(row.VerifiedVersion);
        CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),row.Payload.ToArray());
        Assert.AreEqual(1,calls);Assert.AreEqual(cloud,fixture.Data.Cloud.Count);Assert.AreEqual(creates,fixture.Data.Creates);
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.AssertCatalogHeaderCloudAdmissionAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        var next=await fixture.Reader.InspectCatalogHeaderCloudAsync(await FreshLineageInventoryAsync(fixture).ConfigureAwait(false),
            owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var again=await owner.AllocateAsync(next,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(row.FileId,again.FileId);Assert.AreEqual(row.Sha256,again.Sha256);Assert.AreEqual(1,calls);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderAllocationCancellationOrExitJoinsAndCheckpointsTheActualLateReturnedId(bool exit)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=async _=>
        {calls++;entered.TrySetResult();await release.Task.ConfigureAwait(false);return "known-late-header";};
        var cloud=fixture.Data.Cloud.Count;var creates=fixture.Data.Creates;var disposals=fixture.Data.Setup.Accounts.Readers.DisposalAttempts;
        await using var owner=HeaderAllocator(owned);using var caller=new CancellationTokenSource();
        var allocating=owner.AllocateAsync(admission,caller.Token);
        var stopping=Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(allocating.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);
            Assert.AreEqual(disposals,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally
        {
            release.TrySetResult();
            // Even a failed entry assertion must join the provider before fixture
            // disposal. The original entry/exit deadlines and assertions remain.
            try {await allocating.ConfigureAwait(false);} catch(OperationCanceledException) { }
        }
        await Assert.ThrowsAsync<OperationCanceledException>(()=>allocating).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var row=await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false);
        Assert.IsNotNull(row);Assert.AreEqual(owned.Original.Id,row.Id);Assert.AreEqual("known-late-header",row.FileId);
        Assert.IsNull(row.VerifiedVersion);CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),row.Payload.ToArray());
        Assert.AreEqual(1,calls);Assert.AreEqual(cloud,fixture.Data.Cloud.Count);Assert.AreEqual(creates,fixture.Data.Creates);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        if(exit) Assert.IsTrue(owner.ExitConfirmed);
    }

    [TestMethod]
    public async Task CurrentHeaderAllocationLowStorageKeepsItsKnownReceiptAndRetriesWithoutAnotherProviderAllocation()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>
        {calls++;space.Available=1;return Task.FromResult("known-header-after-storage");};
        await using var owner=HeaderAllocator(owned,budget);
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.FileId);Assert.AreEqual(1,calls);
        Assert.IsNotNull(owner.Failure);Assert.AreEqual(0L,budget.Status().ReservedBytes);
        space.Available=20*StorageBudgetManager.Gibibyte;
        var recovered=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Id,recovered.Id);Assert.AreEqual("known-header-after-storage",recovered.FileId);
        Assert.AreEqual(1,calls);Assert.IsNull(owner.Failure);Assert.IsNull(recovered.VerifiedVersion);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderAllocationRejectsForeignReaderOrNewNonceBeforeProviderIo(bool stale)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("must-not-allocate");};
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        if(stale) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=new CurrentCatalogHeaderAllocationService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,
            fixture.Data.Budget,stale ? fixture.Reader : other);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,calls);Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.FileId);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderAllocationRefusesLatestAuthoredSourcesAndGlobalPendingRootWork(bool pendingRoot)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        _=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);
        var sources=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
        if(pendingRoot)
        {
            // An active publisher cannot silently deselect its real catalog.
            // Prepare actual global revocation work, which legally targets no catalog.
            var revoked=await SignHeaderAllocationRevocationAsync(fixture).ConfigureAwait(false);
            var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
            _=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget)
                .Prepare(Guid.NewGuid(),fence.Attempt,new(revoked,sources,null)),CancellationToken.None).ConfigureAwait(false);
        }
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("must-not-allocate");};
        if(pendingRoot)
            await Assert.ThrowsExactlyAsync<CurrentControlTransitionInProgressException>(()=>fixture.Reader.InspectCatalogHeaderCloudAsync(
                inventory,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        else
            await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.InspectCatalogHeaderCloudAsync(
                inventory,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,calls);Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.FileId);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    private static async Task<LibraryPolicyReference> SignHeaderAllocationRevocationAsync(CurrentReaderFixture fixture)
    {
        await SelectNewerTransitionPolicyAsync(fixture,false).ConfigureAwait(false);
        var intent=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var authority=new LibraryAuthorityRepository(database,fixture.Data.Budget);var selected=authority.CurrentPublisherPolicy()!;
            Assert.IsNull(selected.Publisher);
            var journal=new ProtocolSigningJournal(database,fixture.Data.Budget);
            var record=journal.Inventory(fixture.Data.Setup.Keys.PublicIdentity.LibraryId).Intents.Single(value=>
                value.Kind=="library-publisher-authorization" && value.Sequence==selected.Sequence);
            return journal.Read(record.Id)!;
        },CancellationToken.None).ConfigureAwait(false);
        var bytes=intent.Envelope!.Value.ToArray();var file="header-global-revocation-"+intent.Id.ToString("N");
        fixture.Data.Cloud[file]=new("permission-A",file,"metadata",8,bytes);fixture.Data.MetadataMimeTypes[file]="application/json";
        return new(new("permission-A",file,"metadata",bytes.Length,intent.EnvelopeSha256!),intent.Sequence);
    }

    private static CurrentCatalogHeaderAllocationService HeaderAllocator(HeaderRecoveryFixture owned,StorageBudgetManager? budget=null)=>
        new(owned.Fixture.Data.Setup.Accounts.State,owned.Fixture.Data.Setup.Accounts.Service,budget ?? owned.Fixture.Data.Budget,owned.Fixture.Reader);

    private static async Task<LocalCurrentCatalogHeaderCloudAdmission> PrepareHeaderAllocationAdmissionAsync(HeaderRecoveryFixture owned)
    {
        var fixture=owned.Fixture;var generation=owned.Pending.Capture.Scope.GenerationId;
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        _=await fixture.Reader.RecoverCatalogHeaderPendingAsync(fence,generation,CancellationToken.None).ConfigureAwait(false);
        return await fixture.Reader.InspectCatalogHeaderCloudAsync(await FreshLineageInventoryAsync(fixture).ConfigureAwait(false),generation,CancellationToken.None).ConfigureAwait(false);
    }
}
