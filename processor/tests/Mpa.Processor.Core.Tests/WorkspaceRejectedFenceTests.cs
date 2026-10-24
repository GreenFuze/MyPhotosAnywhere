using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void RejectedFinalObservationsCloseCompletionWithoutAdvancingHistoryAndRemovalRemainsSticky()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture);
        var journal = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var attempt = journal.StartHistoryReplay(journal.StartInventory(journal.BeginScan(context), "original-start"));
        attempt = journal.RememberHistoryPage(attempt, "original-start", "history-end", true, [], []);
        var rejected = journal.RememberRejectedFence(attempt, [new("file", "unknown-removed", true, new string('a', 64)),
            new("file", "ancestor-folder", false, new string('b', 64))], []);
        Assert.AreEqual("history-end", rejected.HistoryReplay!.Cursor); Assert.AreEqual("original-start", rejected.FirstUnresolvedCursor);
        Assert.IsFalse(rejected.HistoryReplay.Active); Assert.IsFalse(rejected.HistoryReplay.AtEnd);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.CompleteScan(rejected, "late-final-end", []));
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.RememberRejectedFence(attempt, [new("file", "late", false, new string('c', 64))], []));
        var next = journal.StartHistoryReplay(journal.StartInventory(journal.BeginScan(context), "new-list-start"));
        next = journal.RememberHistoryPage(next, "history-end", "new-history-end", true, [], []);
        Assert.ThrowsExactly<WorkspaceHistoryRecoveryRequiredException>(() => journal.CompleteScan(next, "new-final-end", []));
        Assert.AreEqual(2, journal.KnownHistory(context).Count); Assert.IsNull(journal.LastCheckpoint());
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void RejectedFinalHistorySurvivesNativeOwnerRecreationAndCannotBeCompletedAsAnOldEnd()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); WorkspaceObservationAttempt rejected;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); var context = ObservationContext(fixture); var journal = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            var attempt = journal.StartHistoryReplay(journal.StartInventory(journal.BeginScan(context), "first-start"));
            attempt = journal.RememberHistoryPage(attempt, "first-start", "history-end", true, [], []);
            rejected = journal.RememberRejectedFence(attempt, [new("file", "removed-after-replay", true, new string('f', 64))], []);
        }
        using var database = new ProcessorStateDatabase(root.Path); var journalAfterRestart = new WorkspaceObservationRepository(database, new(new Space()));
        Assert.AreEqual(rejected, journalAfterRestart.LastAttempt()); Assert.IsTrue(journalAfterRestart.KnownHistory(rejected.Context).Single().RecoveryRequired);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journalAfterRestart.CompleteScan(rejected, "old-final-end", []));
        var next = journalAfterRestart.StartHistoryReplay(journalAfterRestart.StartInventory(journalAfterRestart.BeginScan(rejected.Context), "fresh-start"));
        Assert.AreEqual("history-end", next.HistoryReplay!.Cursor);
        next = journalAfterRestart.RememberHistoryPage(next, "history-end", "current-end", true, [], []);
        Assert.ThrowsExactly<WorkspaceHistoryRecoveryRequiredException>(() => journalAfterRestart.CompleteScan(next, "current-final-end", []));
    }

    [TestMethod]
    public void RejectedFinalCandidateEvidenceIsAtomicAndNonRecoveryChangesStillRequireAFreshScan()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture);
        var journal = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var attempt = journal.StartHistoryReplay(journal.StartInventory(journal.BeginScan(context), "start"));
        attempt = journal.RememberHistoryPage(attempt, "start", "end", true, [], []);
        var events = new[] { new WorkspaceHistoryEvent("file", "new-policy", false, new string('d', 64)) };
        var candidates = new[] { new WorkspaceCandidateObservation("new-policy", 1, 2) };
        fixture.Sql("CREATE TRIGGER fail_rejected_candidate BEFORE INSERT ON processor_meta WHEN NEW.key GLOB 'workspace_observed_candidate_v1:*' BEGIN SELECT RAISE(ABORT,'synthetic checkpoint failure'); END");
        Assert.ThrowsExactly<SqliteException>(() => journal.RememberRejectedFence(attempt, events, candidates));
        Assert.AreEqual(attempt, journal.LastAttempt()); Assert.HasCount(0, journal.KnownHistory(context)); Assert.HasCount(0, journal.KnownIdentities(context));
        fixture.Sql("DROP TRIGGER fail_rejected_candidate"); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.RememberRejectedFence(attempt, events, candidates));
        Assert.AreEqual(attempt, journal.LastAttempt()); Assert.HasCount(0, journal.KnownHistory(context));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        var rejected = journal.RememberRejectedFence(attempt, events, candidates);
        Assert.AreEqual("new-policy", journal.KnownIdentities(context).Single());
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.CompleteScan(rejected, "late", []));
        var next = journal.StartHistoryReplay(journal.StartInventory(journal.BeginScan(context), "new-start"));
        next = journal.RememberHistoryPage(next, "end", "actual-new-end", true, [], []);
        journal.RememberRead(context, new("new-policy", 1, 2, new string('e', 64)));
        var completed = journal.CompleteScan(next, "actual-final-end", journal.Known(context)); journal.AssertCompletedScan(completed);
        Assert.IsFalse(journal.KnownHistory(context).Single().RecoveryRequired); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }
}
