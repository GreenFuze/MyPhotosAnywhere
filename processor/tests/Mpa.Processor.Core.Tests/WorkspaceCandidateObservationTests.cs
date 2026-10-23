using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void UnreadCandidatesSurviveRestartAndCannotDisappearFromACompleteScan()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); WorkspaceObservationContext context; WorkspaceObservationAttempt captured;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            var original = repository.BeginScan(context); captured = repository.RememberCandidates(original, [new("unread-policy", 7, 123)]);
            Assert.AreEqual(original.Id, captured.Id); Assert.AreNotEqual(original.CandidateInventorySha256, captured.CandidateInventorySha256);
            Assert.AreEqual("unread-policy", repository.KnownIdentities(context).Single()); Assert.AreEqual(0, repository.Known(context).Count);
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.CompleteScan(original, "old-plan", []));
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.CompleteScan(captured, "missing", []));
        }
        using var database = new ProcessorStateDatabase(root.Path); var recovered = new WorkspaceObservationRepository(database, new(new Space()));
        Assert.AreEqual(captured, recovered.LastAttempt()); var fresh = recovered.BeginScan(context);
        Assert.AreEqual("unread-policy", recovered.KnownIdentities(context).Single());
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => recovered.CompleteScan(fresh, "empty", []));
        foreach (var changed in new[] { new WorkspaceCandidateObservation("unread-policy", 6, 123), new("unread-policy", 8, 124) })
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => recovered.RememberCandidates(fresh, [changed]));
        var read = new WorkspaceDocumentObservation("unread-policy", 7, 123, new string('a', 64)); recovered.RememberRead(context, read);
        var completed = recovered.CompleteScan(fresh, "actually-read", [read]); recovered.AssertCompletedScan(completed, fresh.Id);
    }

    [TestMethod]
    public void CandidateBatchAndOwnerDigestAreOneAtomicWriteAndRetainPreviousReads()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        repository.RememberRead(context, FirstObservation); var attempt = repository.BeginScan(context);
        fixture.Sql("CREATE TRIGGER fail_candidate BEFORE INSERT ON processor_meta WHEN NEW.key='workspace_observed_candidate_v1:second' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        WorkspaceCandidateObservation[] batch = [new("first", 1, 1), new("second", 1, 1)];
        Assert.ThrowsExactly<SqliteException>(() => repository.RememberCandidates(attempt, batch));
        Assert.AreEqual(attempt, repository.LastAttempt()); Assert.AreEqual(FirstObservation.FileId, repository.KnownIdentities(context).Single());
        fixture.Sql("DROP TRIGGER fail_candidate");
        fixture.Sql("CREATE TRIGGER fail_candidate_digest BEFORE INSERT ON processor_meta WHEN NEW.key='workspace_observation_attempt_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => repository.RememberCandidates(attempt, batch)); Assert.AreEqual(attempt, repository.LastAttempt());
        Assert.AreEqual(FirstObservation.FileId, repository.KnownIdentities(context).Single()); fixture.Sql("DROP TRIGGER fail_candidate_digest");
        var updated = repository.RememberCandidates(attempt, batch); Assert.AreNotEqual(attempt.CandidateInventorySha256, updated.CandidateInventorySha256);
        Assert.AreEqual(3, repository.KnownIdentities(context).Count); Assert.AreEqual(FirstObservation, repository.Known(context).Single());
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void DeletedRewrittenOrOrphanCandidateHistoryCannotBeResetByAnotherScan()
    {
        foreach (var sql in new[] {
            "DELETE FROM processor_meta WHERE key='workspace_observed_candidate_v1:unread'",
            "UPDATE processor_meta SET value='{}' WHERE key='workspace_observed_candidate_v1:unread'",
            "DELETE FROM processor_meta WHERE key IN('workspace_observation_attempt_v1','workspace_observation_attempt_v1:sha256')" })
        {
            using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            repository.RememberCandidates(repository.BeginScan(context), [new("unread", 1, 10)]); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => repository.KnownIdentities(context));
            Assert.ThrowsExactly<InvalidDataException>(() => repository.BeginScan(context)); Assert.IsNull(repository.LastCheckpoint());
        }
    }

    [TestMethod]
    public void CandidateHistoryIsBoundedAndDoesNotRelaxProtocolArrayLimits()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var all = Enumerable.Range(0, WorkspaceObservationRepository.MaximumDocuments).Select(number => new WorkspaceCandidateObservation("unread-" + number, 1, 2)).ToArray();
        var captured = repository.RememberCandidates(repository.BeginScan(context), all); Assert.AreEqual(4096, repository.KnownIdentities(context).Count);
        Assert.AreEqual(captured, repository.RememberCandidates(captured, all.Reverse().ToArray()));
        Assert.ThrowsExactly<InvalidDataException>(() => repository.RememberCandidates(captured, [new("additional", 1, 2)]));
        Assert.AreEqual(captured, repository.LastAttempt());
        Assert.ThrowsExactly<ArgumentException>(() => repository.RememberCandidates(captured, all.Append(new("additional", 1, 2)).ToArray()));
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.RememberCandidates(captured, [all[0], all[0]]));
        Assert.ThrowsExactly<ArgumentException>(() => repository.RememberCandidates(captured, [new(context.FolderId, 1, 2)]));
        Assert.AreEqual(0, repository.Known(context).Count); Assert.IsNull(repository.LastCheckpoint());
    }
}
