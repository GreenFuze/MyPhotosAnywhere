using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void CurrentSigningInventorySeparatesInitialAndUnsignedRootWorkWithoutDroppingAnyNativeIntent()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);
        var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var before=signing.Inventory(current.Slot.ConnectionDescriptor().LibraryId);
        var inventory=journal.CaptureSigningInventory(current.Fenced);
        Assert.AreEqual(current.Slot.ConnectionDescriptor().Encode(),inventory.ConnectionCode);Assert.AreEqual(current.Fenced.Id,inventory.AttemptId);
        Assert.AreEqual(current.Head.EnvelopeSha256,inventory.RootSha256);Assert.AreEqual(before.Sha256,inventory.Signing.Sha256);
        Assert.AreEqual(4,inventory.Signing.Intents.Count);Assert.AreEqual(2,inventory.Roots.Count);
        var initial=inventory.Roots[0];Assert.AreEqual(current.Slot.Id,initial.Intent.Id);Assert.AreEqual(CurrentRootSigningDisposition.InitialPublished,initial.Disposition);
        Assert.IsTrue(initial.Observed);Assert.IsNull(initial.Transition);Assert.IsNull(initial.TerminalSha256);
        var pending=inventory.Roots[1];Assert.AreEqual(plan,pending.Transition);Assert.AreEqual(CurrentRootSigningDisposition.Pending,pending.Disposition);
        Assert.IsFalse(pending.Observed);Assert.IsNull(pending.Intent.EnvelopeSha256);Assert.IsNull(pending.TerminalSha256);
        Assert.IsNull(signing.Read(plan.Id)!.Envelope);Assert.AreEqual(before.Sha256,signing.Inventory(inventory.Signing.LibraryId).Sha256);
        journal.AssertSigningInventoryUnchanged(inventory,current.Fenced);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CurrentSigningInventoryRefusesMissingPlanEvenForAnAlreadyObservedNativeRoot(bool observed)
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        var attempt=current.Fenced;
        if(observed)
        {
            var signed=SealTransition(fixture,plan);attempt=CaptureTransitionRoot(fixture,signed.Envelope!.Value.ToArray(),8).Attempt;
        }
        fixture.Sql("DELETE FROM processor_meta WHERE key GLOB 'current_control_transition_v1:*'");
        Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(()=>journal.CaptureSigningInventory(attempt));
        Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
        Assert.IsNotNull(new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(plan.Id));
    }

    [TestMethod]
    public void CurrentSigningInventoryTerminalOnlyChangeInvalidatesItsCaptureWithoutChangingAnySigningBytes()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        var before=journal.CaptureSigningInventory(current.Fenced);
        journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled);
        var after=journal.CaptureSigningInventory(current.Fenced);
        Assert.AreEqual(before.Signing.Sha256,after.Signing.Sha256);Assert.AreNotEqual(before.Sha256,after.Sha256);
        Assert.AreEqual(CurrentRootSigningDisposition.Pending,before.Roots[1].Disposition);
        Assert.AreEqual(CurrentRootSigningDisposition.Withdrawn,after.Roots[1].Disposition);Assert.IsNotNull(after.Roots[1].TerminalSha256);
        Assert.ThrowsExactly<CurrentControlSigningInventoryChangedException>(()=>journal.AssertSigningInventoryUnchanged(before,current.Fenced));
        journal.AssertSigningInventoryUnchanged(after,current.Fenced);Assert.IsNull(new CurrentCatalogActivationRepository(fixture.Database,fixture.Budget).Read());
        Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
    }

    [TestMethod]
    public void CurrentSigningInventoryRechecksTheSameFinalFenceButNeverBorrowsAnotherNonce()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);
        var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var bytes=signing.Read(current.Slot.Id)!.Envelope!.Value.ToArray();
        var captured=CaptureTransitionRoot(fixture,bytes,7);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var before=journal.CaptureSigningInventory(captured.Attempt);
        var fenced=new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Fence(captured.Attempt,captured.Metadata,current.Head.EnvelopeSha256);
        journal.AssertSigningInventoryUnchanged(before,fenced);Assert.AreEqual(before.Sha256,journal.CaptureSigningInventory(fenced).Sha256);
        var next=CaptureTransitionRoot(fixture,bytes,7);
        Assert.ThrowsExactly<CurrentControlObservationConflictException>(()=>journal.AssertSigningInventoryUnchanged(before,fenced));
        Assert.ThrowsExactly<CurrentControlSigningInventoryChangedException>(()=>journal.AssertSigningInventoryUnchanged(before,next.Attempt));
        Assert.AreEqual(before.Signing.Sha256,journal.CaptureSigningInventory(next.Attempt).Signing.Sha256);
    }

    [TestMethod]
    public void CurrentSigningInventoryKeepsAnActuallyAppliedSignedRootPendingUntilCanonicalAdmission()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));var signed=SealTransition(fixture,plan);
        var captured=CaptureTransitionRoot(fixture,signed.Envelope!.Value.ToArray(),8);var inventory=journal.CaptureSigningInventory(captured.Attempt);
        Assert.AreEqual(signed.EnvelopeSha256,inventory.RootSha256);var root=inventory.Roots.Single(value=>value.Intent.Id==plan.Id);
        Assert.IsTrue(root.Observed);Assert.AreEqual(CurrentRootSigningDisposition.Pending,root.Disposition);Assert.AreEqual(plan,root.Transition);
        Assert.AreEqual(signed.EnvelopeSha256,root.Intent.EnvelopeSha256);Assert.IsNull(root.TerminalSha256);
        Assert.AreEqual(plan,journal.Pending().Single());Assert.IsNull(new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget).PublishedHead());
        Assert.IsNull(new CurrentCatalogActivationRepository(fixture.Database,fixture.Budget).Read());
    }

    [TestMethod]
    public void CurrentSigningInventoryDistinguishesActualCanonicalActivationFromAnObservedPendingRoot()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));var signed=SealTransition(fixture,plan);
        var captured=CaptureTransitionRoot(fixture,signed.Envelope!.Value.ToArray(),8);var before=journal.CaptureSigningInventory(captured.Attempt);
        var observations=new LocalCurrentGraphObservations(fixture.Database,fixture.Budget);
        foreach(var policy in new[]{current.Head.Publishers,current.Head.Sources})
        {
            var metadata=observations.RememberMetadata(captured.Attempt,policy.Reference,new(7,checked((int)policy.Reference.Bytes),"application/json",null,new string('a',64)));
            observations.RememberBytes(captured.Attempt,metadata,policy.Reference.Sha256);
        }
        var stages=new CurrentCatalogStageJournal(fixture.Database,fixture.Budget);var claim=stages.Begin(captured.Attempt,Guid.NewGuid());CurrentCatalogStageDescriptor descriptor;
        using(var area=new CurrentCatalogStageArea(fixture.Database.StateDirectory,fixture.Database.InstallationId,[claim],fixture.Budget))
        using(var index=area.Open(claim,fixture.Budget)) descriptor=index.Seal(0);
        claim=stages.RecordSealed(claim,captured.Attempt,descriptor);
        var fenced=new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Fence(captured.Attempt,captured.Metadata,signed.EnvelopeSha256!);
        var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);
        var publisher=signing.Read(signing.Inventory(before.Signing.LibraryId).Intents.Single(value=>value.Kind=="library-publisher-authorization").Id)!.Envelope!.Value;
        var source=signing.Read(current.SourceIntentId)!.Envelope!.Value;
        _=new CurrentCatalogActivationRepository(fixture.Database,fixture.Budget).Activate(fenced,claim,publisher,source,0,0,[]);
        var after=journal.CaptureSigningInventory(fenced);Assert.AreEqual(before.Signing.Sha256,after.Signing.Sha256);
        Assert.AreEqual(CurrentRootSigningDisposition.Pending,before.Roots[1].Disposition);
        Assert.AreEqual(CurrentRootSigningDisposition.Activated,after.Roots[1].Disposition);Assert.IsTrue(after.Roots[1].Observed);Assert.IsNotNull(after.Roots[1].TerminalSha256);
        Assert.ThrowsExactly<CurrentControlSigningInventoryChangedException>(()=>journal.AssertSigningInventoryUnchanged(before,fenced));
        journal.AssertSigningInventoryUnchanged(after,fenced);Assert.IsNull(new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget).PublishedHead());
        Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
    }

    [TestMethod]
    public void CurrentSigningInventoryIncludesOtherReservedNativeKindsAndHonorsCancellation()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var before=journal.CaptureSigningInventory(current.Fenced);var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);
        var policy=signing.Reserve(Guid.NewGuid(),fixture.ActorId,1,"library-publisher-authorization",sequence=>InitialPolicyBody(fixture.Policy(sequence,revoked:true)));
        Assert.ThrowsExactly<CurrentControlSigningInventoryChangedException>(()=>journal.AssertSigningInventoryUnchanged(before,current.Fenced));
        var after=journal.CaptureSigningInventory(current.Fenced);Assert.AreEqual(1,after.Roots.Count);Assert.AreEqual(4,after.Signing.Intents.Count);
        Assert.AreEqual(policy.Id,after.Signing.Intents.Single(value=>value.Id==policy.Id).Id);Assert.IsNull(signing.Read(policy.Id)!.Envelope);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>journal.CaptureSigningInventory(current.Fenced,cancelled.Token));
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }
}
