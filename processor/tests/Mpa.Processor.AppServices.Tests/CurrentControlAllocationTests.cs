using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentControlAllocationsSerializeReuseTheProviderIdAndCreateNoFile()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=ControlAllocator(fixture);
        var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>owner.PrepareAsync(CancellationToken.None))).ConfigureAwait(false);
        Assert.IsTrue(results.All(slot=>slot==results[0]));Assert.AreEqual("captured-3",results[0].FileId);
        Assert.AreEqual(results[0].FileId,results[0].ConnectionDescriptor().CurrentFileId);
        Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(2,fixture.Cloud.Count);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        await using var reopened=ControlAllocator(fixture);
        Assert.AreEqual(results[0],await reopened.PrepareAsync(CancellationToken.None).ConfigureAwait(false));Assert.AreEqual(3,fixture.Allocations);
    }

    [TestMethod]
    public async Task CurrentControlAllocationRetriesItsKnownReceiptAfterLowStorageWithoutAnotherProviderCall()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);
        await using var owner=ControlAllocator(fixture,budget);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate=_=>
        { fixture.Allocations++;space.Available=1;return Task.FromResult("known-current-id"); };
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);Assert.IsNotNull(owner.Failure);
        var pending=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pending);Assert.IsNull(pending.FileId);
        space.Available=20*StorageBudgetManager.Gibibyte;
        var recovered=await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.Id,recovered.Id);Assert.AreEqual("known-current-id",recovered.FileId);
        Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(0L,budget.Status().ReservedBytes);Assert.IsNull(owner.Failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentControlAllocationCancellationOrExitJoinsAndRetainsTheLateProviderId(bool exit)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=ControlAllocator(fixture);using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate=async _=>
        { fixture.Allocations++;entered.TrySetResult();await release.Task.ConfigureAwait(false);return "known-late-current-id"; };
        var preparing=owner.PrepareAsync(caller.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stopping=Task.CompletedTask;
        if (exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try
        {
            Assert.IsFalse(preparing.IsCompleted);if (exit) Assert.IsFalse(stopping.IsCompleted);
            Assert.AreEqual(0,fixture.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(()=>preparing).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var retained=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("known-late-current-id",retained!.FileId);Assert.AreEqual(1L,retained.AllocationAccountRevision);
        Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);
        if (exit) Assert.IsTrue(owner.ExitConfirmed);
        else Assert.AreEqual(retained,await owner.PrepareAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentControlAllocationRefusesChangedSourcesOrDisconnectedAccountsBeforeProviderIo(bool changedSources)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=ControlAllocator(fixture);
        if (changedSources) await fixture.Setup.AddSourceAsync("changed-human-source").ConfigureAwait(false);
        else await fixture.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);
    }

    private static CurrentControlAllocationService ControlAllocator(InitialPublicationFixture fixture,StorageBudgetManager? budget=null)=>
        new(fixture.Setup.Accounts.State,fixture.Setup.Accounts.Service,fixture.Setup.Keys,budget ?? fixture.Budget);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentControlAllocationRetainsIdentityButRejectsAuthorityChangedDuringProviderIo(bool changedSources)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=ControlAllocator(fixture);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate=async _=>
        {
            fixture.Allocations++;
            if (changedSources) await fixture.Setup.AddSourceAsync("changed-during-allocation").ConfigureAwait(false);
            else await fixture.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
            return "same-returned-current-id";
        };
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var retained=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("same-returned-current-id",retained!.FileId);Assert.AreEqual(1L,retained.AllocationAccountRevision);
        Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);
    }

    [TestMethod]
    public async Task CurrentControlAllocationFailedRetirementRetainsBorrowersUntilTheSameReceiptPersists()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);
        await using var owner=ControlAllocator(fixture,budget);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate=_=>
        { fixture.Allocations++;space.Available=1;return Task.FromResult("retirement-pending-id"); };
        try
        {
            await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.DisposeAsync().AsTask()).ConfigureAwait(false);
            Assert.IsFalse(owner.ExitConfirmed);Assert.AreEqual(0,fixture.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally { space.Available=20*StorageBudgetManager.Gibibyte; }
        await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);
        var retained=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("retirement-pending-id",retained!.FileId);Assert.AreEqual(3,fixture.Allocations);Assert.AreEqual(2,fixture.Creates);
    }

    private sealed class CurrentAllocationSpace : IStorageSpaceReader
    {
        internal long Available { get; set; }=20*StorageBudgetManager.Gibibyte;
        public StorageSpaceSnapshot Read()=>new(100*StorageBudgetManager.Gibibyte,Available,0);
    }
}
