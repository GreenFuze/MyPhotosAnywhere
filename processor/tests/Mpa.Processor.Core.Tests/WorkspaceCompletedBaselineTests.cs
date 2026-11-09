using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class ProtocolSigningJournalTests
{
    [TestMethod]
    public void AnIdenticalLaterCheckpointCannotSubstituteForTheActualBaselineAttempt()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap();
        var journal = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null);
        var complete = journal.CompleteScan(journal.BeginScan(context), "same-actual-end", []); var first = journal.LastAttempt()!;
        var same = journal.CompleteScan(journal.BeginScan(context), "same-actual-end", []); var second = journal.LastAttempt()!;
        Assert.AreEqual(complete, same); Assert.AreNotEqual(first.Id, second.Id);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.BeginScanFromCompleted(complete, first.Id));
        Assert.AreEqual(second, journal.LastAttempt()); journal.AssertCompletedScan(same, second.Id);
        var next = journal.BeginScanFromCompleted(same, second.Id); Assert.IsNull(next.Completed);
        Assert.AreEqual(second.Revision + 1, next.Revision);
    }

    [TestMethod]
    public void ACompletedBaselineIsFencedAndInvalidatedInTheSameNativeTransition()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap();
        var journal = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null);
        var complete = journal.CompleteScan(journal.BeginScan(context), "actual-baseline-end", []);
        var before = journal.LastAttempt()!;
        var next = journal.BeginScanFromCompleted(complete, before!.Id);
        Assert.AreNotEqual(before.Id, next.Id); Assert.AreEqual(before.Revision + 1, next.Revision);
        Assert.AreEqual(complete.Revision, next.BaseCheckpointRevision); Assert.IsNull(next.Completed);
        Assert.AreEqual(complete, journal.LastCheckpoint()); Assert.AreEqual(next, journal.LastAttempt());
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.AssertCompletedScan(complete));
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.BeginScanFromCompleted(complete, before!.Id));
        Assert.AreEqual(next, journal.LastAttempt());
        var fresh = journal.CompleteScan(next, "actual-later-end", []);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.BeginScanFromCompleted(complete, before!.Id));
        Assert.AreEqual(fresh, journal.LastAttempt()!.Completed); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void ChangedSigningHistoryCannotOpenAnOldCompletedBaseline()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap();
        var journal = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null);
        var complete = journal.CompleteScan(journal.BeginScan(context), "before-new-intent", []);
        var before = journal.LastAttempt(); var intent = fixture.Reserve(Guid.NewGuid());
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => journal.BeginScanFromCompleted(complete, before!.Id));
        Assert.AreEqual(before, journal.LastAttempt()); Assert.AreEqual(complete, journal.LastCheckpoint());
        var retained = fixture.Journal.Read(intent.Id); Assert.IsNotNull(retained);
        CollectionAssert.AreEqual(intent.Body.ToArray(), retained.Body.ToArray());
        Assert.AreEqual(intent with { Body = retained.Body }, retained); Assert.IsNull(retained.Envelope);
    }

    [TestMethod]
    public void FailedBaselineFenceWritePreservesTheExactCompletedAttemptAndCheckpoint()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap();
        var journal = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var context = new WorkspaceObservationContext(pin.LibraryId, pin.AccountId, pin.FolderId, 1, null);
        var complete = journal.CompleteScan(journal.BeginScan(context), "before-failed-transition", []);
        var before = journal.LastAttempt();
        fixture.Sql("CREATE TRIGGER fail_baseline_transition BEFORE UPDATE ON processor_meta WHEN NEW.key='workspace_observation_attempt_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic atomic transition failure'); END");
        Assert.ThrowsExactly<SqliteException>(() => journal.BeginScanFromCompleted(complete, before!.Id));
        Assert.AreEqual(before, journal.LastAttempt()); Assert.AreEqual(complete, journal.LastCheckpoint()); journal.AssertCompletedScan(complete);
        fixture.Sql("DROP TRIGGER fail_baseline_transition");
        var next = journal.BeginScanFromCompleted(complete, before!.Id); Assert.IsNull(next.Completed);
        Assert.AreEqual(before!.Revision + 1, next.Revision); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }
}
