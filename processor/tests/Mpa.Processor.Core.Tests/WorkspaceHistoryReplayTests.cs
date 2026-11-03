using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void PartialHistoryAndUnreadCandidatesSurviveRestartWithoutAdvancingTheUnresolvedStart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); WorkspaceObservationContext context; WorkspaceObservationAttempt partial;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            var attempt = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), "earliest"));
            partial = repository.RememberHistoryPage(attempt, "earliest", "second-page", false,
                [new("file", "unread", false, new string('a', 64))], [new("unread", 2, 3)]);
            Assert.IsNull(repository.LastCheckpoint()); Assert.IsFalse(partial.HistoryReplay!.AtEnd);
        }
        using var database = new ProcessorStateDatabase(root.Path); var recovered = new WorkspaceObservationRepository(database, new(new Space()));
        Assert.AreEqual(partial, recovered.LastAttempt()); Assert.AreEqual("unread", recovered.KnownIdentities(context).Single());
        var next = recovered.StartHistoryReplay(recovered.StartInventory(recovered.BeginScan(context), "fresh-listing"));
        Assert.AreEqual("earliest", next.HistoryReplay!.StartCursor); Assert.AreEqual("second-page", next.HistoryReplay.Cursor);
        Assert.AreEqual(1L, next.HistoryReplay.Pages); Assert.AreEqual(0, next.HistoryReplay.BatchPages);
        next = recovered.RememberHistoryPage(next, "second-page", "history-end", true, [], []);
        recovered.RememberRead(context, new("unread", 2, 3, new string('b', 64)));
        var completed = recovered.CompleteScan(next, "final-fence", recovered.Known(context)); recovered.AssertCompletedScan(completed);
        Assert.AreEqual("earliest", recovered.LastAttempt()!.FirstUnresolvedCursor); // No signature/ancestry proof was supplied.
        Assert.AreEqual(1L, recovered.KnownHistory(context).Single().Occurrences);
    }

    [TestMethod]
    public void AmbiguousRemovalIsCommittedBeforeRecoveryAndCannotBeClearedByALaterSuccessfulReplay()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var attempt = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), "first"));
        attempt = repository.RememberHistoryPage(attempt, "first", "end", true, [new("file", "unknown-removed", true, new string('c', 64))], []);
        Assert.ThrowsExactly<WorkspaceHistoryRecoveryRequiredException>(() => repository.CompleteScan(attempt, "end", []));
        Assert.IsNull(repository.LastCheckpoint()); Assert.IsNull(repository.LastAttempt()!.Completed);
        var next = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), "new-start"));
        next = repository.RememberHistoryPage(next, "end", "later", true, [new("file", "unknown-removed", false, new string('d', 64))], []);
        var retained = repository.KnownHistory(context).Single(); Assert.IsTrue(retained.RecoveryRequired); Assert.AreEqual(2L, retained.Occurrences);
        Assert.AreEqual(new string('c', 64), retained.FirstEvidenceSha256); Assert.AreEqual(new string('d', 64), retained.LastEvidenceSha256);
        Assert.ThrowsExactly<WorkspaceHistoryRecoveryRequiredException>(() => repository.CompleteScan(next, "later", []));
        Assert.AreEqual("first", repository.LastAttempt()!.FirstUnresolvedCursor); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void HistoryCandidateAndProgressWritesRollbackTogetherAndRetryKeepsTheirIdentities()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var attempt = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), "start"));
        var events = new[] { new WorkspaceHistoryEvent("file", "captured", false, new string('e', 64)) };
        var candidates = new[] { new WorkspaceCandidateObservation("captured", 1, 2) };
        fixture.Sql("CREATE TRIGGER fail_history_candidate BEFORE INSERT ON processor_meta WHEN NEW.key GLOB 'workspace_observed_candidate_v1:*' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => repository.RememberHistoryPage(attempt, "start", "end", true, events, candidates));
        Assert.AreEqual(attempt, repository.LastAttempt()); Assert.HasCount(0, repository.KnownHistory(context)); Assert.HasCount(0, repository.KnownIdentities(context));
        fixture.Sql("DROP TRIGGER fail_history_candidate"); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => repository.RememberHistoryPage(attempt, "start", "end", true, events, candidates));
        Assert.AreEqual(attempt, repository.LastAttempt()); Assert.HasCount(0, repository.KnownHistory(context));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        var completed = repository.RememberHistoryPage(attempt, "start", "end", true, events, candidates);
        Assert.AreEqual("captured", repository.KnownIdentities(context).Single()); Assert.AreEqual(1L, repository.KnownHistory(context).Single().Occurrences);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.CompleteScan(completed, "end", [])); // Candidate still needs an actual read.
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void MissingHistoryRowsAndSupersededReplayCallbacksFailWithoutReplacingNewerEvidence()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var first = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), "first"));
        var second = repository.BeginScan(context);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.RememberHistoryPage(first, "first", "late", true, [], []));
        Assert.AreEqual(second, repository.LastAttempt());
        second = repository.StartHistoryReplay(repository.StartInventory(second, "fresh"));
        second = repository.RememberHistoryPage(second, "first", "end", true, [new("file", "observed", false, new string('f', 64))], []);
        fixture.Sql("DELETE FROM processor_meta WHERE key GLOB 'workspace_history_observed_v1:*'");
        Assert.ThrowsExactly<InvalidDataException>(() => repository.LastAttempt()); Assert.ThrowsExactly<InvalidDataException>(() => repository.BeginScan(context));
        Assert.ThrowsExactly<InvalidDataException>(() => repository.CompleteScan(second, "end", []));
    }

    [TestMethod]
    public void CompleteHistoryInventoryRefusesTruncationAndRetainsAllPreviouslyObservedIdentities()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var attempt = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), "start")); var cursor = "start";
        for (var offset = 0; offset < 4096; offset += 100)
        {
            var count = Math.Min(100, 4096 - offset); var next = "page-" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var events = Enumerable.Range(offset, count).Select(index => new WorkspaceHistoryEvent("file", "history-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), false, new string('a', 64))).ToArray();
            attempt = repository.RememberHistoryPage(attempt, cursor, next, false, events, []); cursor = next;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => repository.RememberHistoryPage(attempt, cursor, "overflow", false, [new("file", "extra", false, new string('b', 64))], []));
        Assert.HasCount(4096, repository.KnownHistory(context)); Assert.AreEqual(attempt, repository.LastAttempt());
    }

    [TestMethod]
    public void FiveMaximumEscapedCursorsFitThePrivateAttemptBoundWithoutChangingProtocolCaps()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var first = new string('ש', 4096); repository.StartInventory(repository.BeginScan(context), first);
        var attempt = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), new string('א', 4096)));
        attempt = repository.RememberHistoryPage(attempt, first, new string('ב', 4096), true, [], []);
        var checkpoint = repository.CompleteScan(attempt, new string('ג', 4096), []); repository.AssertCompletedScan(checkpoint);
        Assert.AreEqual(first, repository.LastAttempt()!.FirstUnresolvedCursor); Assert.IsTrue(repository.LastAttempt()!.HistoryReplay!.AtEnd);
    }

    [TestMethod]
    public void LargeChangeFeedsResumeInBoundedBatchesWithoutReplayingOrDiscardingCommittedPages()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var attempt = repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context), "earliest")); var cursor = "earliest";
        for (var number = 0; number < 128; number++)
        {
            var next = "retained-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attempt = repository.RememberHistoryPage(attempt, cursor, next, false, [], []); cursor = next;
        }
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.RememberHistoryPage(attempt, cursor, "too-far", true, [], []));
        var nextAttempt = repository.BeginScan(context); Assert.IsFalse(nextAttempt.HistoryReplay!.Active); Assert.AreEqual(cursor, nextAttempt.HistoryReplay.Cursor);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.CompleteScan(nextAttempt, "not-replayed", []));
        nextAttempt = repository.StartHistoryReplay(repository.StartInventory(nextAttempt, "current-listing"));
        Assert.AreEqual(0, nextAttempt.HistoryReplay!.BatchPages); Assert.AreEqual(128L, nextAttempt.HistoryReplay.Pages);
        nextAttempt = repository.RememberHistoryPage(nextAttempt, cursor, "actual-end", true, [], []);
        var checkpoint = repository.CompleteScan(nextAttempt, "final-current-fence", []); repository.AssertCompletedScan(checkpoint);
        Assert.AreEqual(129L, repository.LastAttempt()!.HistoryReplay!.Pages); Assert.AreEqual("earliest", repository.LastAttempt()!.FirstUnresolvedCursor);
    }
}
