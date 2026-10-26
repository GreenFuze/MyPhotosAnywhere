using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentSigningInventoryUsesTheActualCaptureAndFinalFenceWithoutAdditionalNetworkOrSigning()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var reads=fixture.Http.BodyReads;
        var inventory=await fixture.Reader.CaptureSigningInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(2,inventory.Roots.Count);
        Assert.AreEqual(plan,inventory.Roots.Single(value=>value.Intent.Id==plan.Id).Transition);
        Assert.IsNull((await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Envelope);
        var fence=await fixture.Reader.FenceAsync(capture,CancellationToken.None).ConfigureAwait(false);reads=fixture.Http.BodyReads;
        await fixture.Reader.AssertSigningInventoryUnchangedAsync(inventory,fence,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(3,fixture.Data.Allocations);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
    }

    [TestMethod]
    public async Task LocalCurrentSigningInventoryRefusesAnotherReadersCaptureAndFenceBeforeAnyNetwork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var inventory=await fixture.Reader.CaptureSigningInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var fence=await fixture.Reader.FenceAsync(capture,CancellationToken.None).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.CaptureSigningInventoryAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.AssertSigningInventoryUnchangedAsync(inventory,fence,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);
        await fixture.Reader.AssertSigningInventoryUnchangedAsync(inventory,fence,CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentSigningInventoryRejectsNewerNonceAndTerminalOnlyChangesAndPropagatesCancellation()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var inventory=await fixture.Reader.CaptureSigningInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var fence=await fixture.Reader.FenceAsync(capture,CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget)
            .Withdraw(plan.Id,fence.Attempt,CurrentControlTransitionWithdrawalReason.Cancelled),CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlSigningInventoryChangedException>(()=>fixture.Reader.AssertSigningInventoryUnchangedAsync(inventory,fence,CancellationToken.None)).ConfigureAwait(false);
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.AssertSigningInventoryUnchangedAsync(inventory,fence,CancellationToken.None)).ConfigureAwait(false);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        var stopped=await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>fixture.Reader.CaptureSigningInventoryAsync(capture,cancelled.Token)).ConfigureAwait(false);
        Assert.IsTrue(stopped.CancellationToken.IsCancellationRequested);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.IsNull((await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Envelope);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
