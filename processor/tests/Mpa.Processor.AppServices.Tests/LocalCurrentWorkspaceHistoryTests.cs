using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentWorkspaceHistoryPreservesTheActualPinnedRootAndFailedUnreadWorkWithoutNetwork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var signing=await fixture.Reader.CaptureSigningInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var preceding=await fixture.Reader.CaptureWorkspaceHistoryAsync(signing,capture,CancellationToken.None).ConfigureAwait(false);
        var pin=LibraryConnectionDescriptor.Decode(signing.ConnectionCode);var root=await TransitionIntentAsync(fixture,capture.Attempt.SlotId).ConfigureAwait(false);
        var known=new WorkspaceDocumentObservation(pin.CurrentFileId!,7,root.Envelope!.Value.Length,root.EnvelopeSha256!);
        var unknown=new WorkspaceCandidateObservation("still-unread-metadata",9,123);
        var context=new WorkspaceObservationContext(pin.LibraryId,pin.Workspace.AccountId,pin.Workspace.FolderId,capture.Attempt.AccountRevision,capture.Attempt.SharedDriveId);
        var attempt=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var repository=new WorkspaceObservationRepository(database,fixture.Data.Budget);repository.RememberRead(context,known);
            var pending=repository.StartInventory(repository.BeginScan(context),"unresolved-history-start");
            return repository.RememberCandidates(pending,[new(known.FileId,known.Version,known.Bytes),unknown]);
        },CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;
        var history=await fixture.Reader.CaptureWorkspaceHistoryAsync(signing,capture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(known,history.RootObservation);
        Assert.AreEqual(attempt,history.RetainedAttempt);Assert.AreEqual("current",history.RetainedAttempt!.FirstUnresolvedCursor);
        Assert.AreEqual("unresolved-history-start",history.RetainedAttempt.InventoryStartCursor);
        Assert.IsNull(history.RetainedAttempt.Completed);
        CollectionAssert.AreEqual(preceding.ImmutableCandidates.Append(unknown).OrderBy(value=>value.FileId,StringComparer.Ordinal).ToArray(),history.ImmutableCandidates.ToArray());
        CollectionAssert.AreEqual(preceding.ImmutableObservations.ToArray(),history.ImmutableObservations.ToArray());
        Assert.AreEqual(unknown,history.ImmutableCandidates.Single(value=>value.FileId==unknown.FileId));
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).Known(context),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(known,retained.Single(value=>value.FileId==known.FileId));Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentWorkspaceHistoryRejectsForeignAndStaleCapturesAndJoinsCancellation()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var signing=await fixture.Reader.CaptureSigningInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.CaptureWorkspaceHistoryAsync(signing,capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.CaptureWorkspaceHistoryAsync(signing,capture,CancellationToken.None)).ConfigureAwait(false);
        using var stop=new CancellationTokenSource();stop.Cancel();
        var cancelled=await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>fixture.Reader.CaptureWorkspaceHistoryAsync(signing,capture,stop.Token)).ConfigureAwait(false);
        Assert.IsTrue(cancelled.CancellationToken.IsCancellationRequested);Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);
    }
}
