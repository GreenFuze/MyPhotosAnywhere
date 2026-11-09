using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void CurrentHistorySeparatesThePinnedMutableIdButKeepsOldNativeRootBytesAndEveryOtherUnreadIdentity()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var context=CurrentHistoryContext(current);var originalRoot=new WorkspaceDocumentObservation(current.Slot.FileId!,7,
            new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(current.Slot.Id)!.Envelope!.Value.Length,current.Head.EnvelopeSha256);
        observations.RememberRead(context,originalRoot);var before=observations.Known(context).ToArray();
        var attempt=observations.StartInventory(observations.BeginScan(context),"retained-unresolved-start");
        var rootCandidate=new WorkspaceCandidateObservation(originalRoot.FileId,7,originalRoot.Bytes);
        var unread=new WorkspaceCandidateObservation("unread-other-document",9,123);
        attempt=observations.RememberCandidates(attempt,[rootCandidate,unread]);
        var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));var signed=SealTransition(fixture,plan);
        var next=CaptureTransitionRoot(fixture,signed.Envelope!.Value.ToArray(),8);var signing=journal.CaptureSigningInventory(next.Attempt);
        var rows=fixture.Scalar("SELECT count(*) FROM processor_meta");fixture.Space.Available=1;
        var history=observations.CaptureCurrentHistory(signing,next.Attempt);
        Assert.AreEqual(originalRoot,history.RootObservation);Assert.AreEqual(rootCandidate,history.RootCandidate);
        Assert.AreNotEqual(history.Signing.RootSha256,history.RootObservation!.Sha256);
        Assert.AreEqual(attempt,history.RetainedAttempt);Assert.AreEqual("retained-unresolved-start",history.RetainedAttempt!.FirstUnresolvedCursor);
        Assert.IsNull(history.RetainedAttempt.Completed);Assert.AreEqual(unread,history.ImmutableCandidates.Single());
        CollectionAssert.AreEqual(before.Where(value=>value.FileId!=originalRoot.FileId).ToArray(),history.ImmutableObservations.ToArray());
        CollectionAssert.AreEqual(before,observations.Known(context).ToArray());Assert.AreEqual(rows,fixture.Scalar("SELECT count(*) FROM processor_meta"));
        Assert.IsTrue(observations.KnownIdentities(context).Contains(unread.FileId));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CurrentHistoryCannotReclassifyUnownedBytesJustBecauseTheirFileHasThePinnedName()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var bad=new WorkspaceDocumentObservation(current.Slot.FileId!,7,123,new string('f',64));observations.RememberRead(CurrentHistoryContext(current),bad);
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(current.Fenced);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced));
        Assert.AreEqual(bad,observations.Known(CurrentHistoryContext(current)).Single(value=>value.FileId==bad.FileId));
    }

    [TestMethod]
    public void CurrentHistoryCannotRewindAReadVersionAheadOfTheActualRoot()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var intent=new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(current.Slot.Id)!;
        observations.RememberRead(CurrentHistoryContext(current),new(current.Slot.FileId!,8,intent.Envelope!.Value.Length,intent.EnvelopeSha256!));
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(current.Fenced);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced));
    }

    [TestMethod]
    public void CurrentHistoryCannotHideAnUnreadRootCandidateAboveTheActualVersion()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var context=CurrentHistoryContext(current);var attempt=observations.BeginScan(context);
        attempt=observations.RememberCandidates(attempt,[new(current.Slot.FileId!,8,123)]);
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(current.Fenced);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced));
        Assert.IsTrue(observations.KnownIdentities(context).Contains(current.Slot.FileId!));Assert.IsNull(attempt.Completed);
    }

    [TestMethod]
    [DataRow("application/gzip")]
    [DataRow("application/octet-stream")]
    public void CurrentHistoryCannotExcludeAChangedRootCandidateEncoding(string mime)
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var context=CurrentHistoryContext(current);var attempt=observations.BeginScan(context);
        observations.RememberCandidates(attempt,[new(current.Slot.FileId!,7,123,mime)]);
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(current.Fenced);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced));
        Assert.IsTrue(observations.KnownIdentities(context).Contains(current.Slot.FileId!));
    }

    [TestMethod]
    public void CurrentHistoryCannotClassifyAnEncodedReadAsTheExactJsonRootEvenWithAnIdenticalDigest()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var intent=new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(current.Slot.Id)!;
        observations.RememberRead(CurrentHistoryContext(current),new(current.Slot.FileId!,7,intent.Envelope!.Value.Length,intent.EnvelopeSha256!,"application/gzip"));
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(current.Fenced);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced));
    }

    [TestMethod]
    public void CurrentHistoryRechecksTerminalChangesAndNewNoncesBeforeClassifyingAnyIdentity()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));var signing=journal.CaptureSigningInventory(current.Fenced);
        journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled);
        var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        Assert.ThrowsExactly<CurrentControlSigningInventoryChangedException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced));
        new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Begin(1);
        Assert.ThrowsExactly<CurrentControlObservationConflictException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced));
        Assert.IsNull(new CurrentCatalogActivationRepository(fixture.Database,fixture.Budget).Read());
    }

    [TestMethod]
    public void CurrentHistoryHonorsCancellationWithoutWritingOrDroppingRetainedObservations()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var observations=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var context=CurrentHistoryContext(current);var before=observations.Known(context).ToArray();
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(current.Fenced);
        using var stop=new CancellationTokenSource();stop.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>observations.CaptureCurrentHistory(signing,current.Fenced,stop.Token));
        CollectionAssert.AreEqual(before,observations.Known(context).ToArray());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    private static WorkspaceObservationContext CurrentHistoryContext((LibraryCurrentControlSlot Slot,LibraryCurrentControl Head,LocalCurrentControlAttempt Fenced,Guid SourceIntentId) current)
    {
        var pin=current.Slot.ConnectionDescriptor();return new(pin.LibraryId,pin.Workspace.AccountId,pin.Workspace.FolderId,current.Fenced.AccountRevision,current.Fenced.SharedDriveId);
    }
}
