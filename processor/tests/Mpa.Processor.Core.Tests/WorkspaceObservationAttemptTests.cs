using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void ScanFailureFenceSurvivesRestartAndOnlyAFreshExactAttemptCanComplete()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); WorkspaceObservationContext context;
        WorkspaceObservationAttempt interrupted; WorkspaceInventoryCheckpoint earlier;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            repository.RememberRead(context, FirstObservation); var first = repository.BeginScan(context);
            earlier = repository.CompleteScan(first, "same-cursor", [FirstObservation]); repository.AssertCompletedScan(earlier, first.Id);
            interrupted = repository.BeginScan(context);
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.AssertCompletedScan(earlier));
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.CompleteScan(first, "late", [FirstObservation]));
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.Complete(context, "bypass", [FirstObservation]));
            Assert.AreEqual(earlier, repository.LastCheckpoint());
        }
        using var database = new ProcessorStateDatabase(root.Path); var recovered = new WorkspaceObservationRepository(database, new(new Space()));
        Assert.AreEqual(interrupted, recovered.LastAttempt());
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => recovered.AssertCompletedScan(earlier));
        var fresh = recovered.BeginScan(context); Assert.AreEqual(interrupted.Revision + 1, fresh.Revision); Assert.AreNotEqual(interrupted.Id, fresh.Id);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => recovered.CompleteScan(interrupted, "late", [FirstObservation]));
        var current = recovered.CompleteScan(fresh, "same-cursor", [FirstObservation]); Assert.AreEqual(earlier, current);
        recovered.AssertCompletedScan(current, fresh.Id);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => recovered.AssertCompletedScan(current, interrupted.Id));
    }

    [TestMethod]
    public void ScanCheckpointAndCompletionFenceRollbackTogetherOnHashWriteFailure()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture);
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); repository.RememberRead(context, FirstObservation);
        var attempt = repository.BeginScan(context);
        fixture.Sql("CREATE TRIGGER fail_scan_hash BEFORE INSERT ON processor_meta WHEN NEW.key='workspace_observation_attempt_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => repository.CompleteScan(attempt, "cursor", [FirstObservation]));
        Assert.IsNull(repository.LastCheckpoint()); Assert.AreEqual(attempt, repository.LastAttempt());
        fixture.Sql("DROP TRIGGER fail_scan_hash"); var complete = repository.CompleteScan(attempt, "cursor", [FirstObservation]); repository.AssertCompletedScan(complete);
        fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => repository.BeginScan(context));
        Assert.AreEqual(attempt with { Completed = complete }, repository.LastAttempt());
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void ChangedInventoryAndRetiredAccessCannotReuseACompletedScan()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture);
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); repository.RememberRead(context, FirstObservation);
        var checkpoint = repository.CompleteScan(repository.BeginScan(context), "cursor", [FirstObservation]);
        repository.RememberRead(context, FirstObservation with { Version = FirstObservation.Version + 1 });
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.AssertCompletedScan(checkpoint));
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.BeginScan(context with { SharedDriveId = "other-drive" }));
        var accounts = new DriveAccountRepository(fixture.Database); accounts.Disconnect("account-A", 1);
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => repository.AssertCompletedScan(checkpoint));
        var pending = accounts.Begin(Guid.NewGuid(), "account-A"); var account = accounts.Promote(pending.Id, "account-A", "Again", null);
        var renewed = repository.BeginScan(context with { AccountRevision = account.Revision });
        Assert.AreEqual(account.Revision, renewed.Context.AccountRevision); Assert.IsNull(renewed.Completed);
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => repository.CompleteScan(renewed with { Context = context }, "old-access", [FirstObservation]));
        var current = repository.CompleteScan(renewed, "renewed", repository.Known(renewed.Context)); repository.AssertCompletedScan(current);
    }

    [TestMethod]
    public void DamagedOrMissingAttemptPartsAndCheckpointSubstitutionFailClosed()
    {
        foreach (var sql in new[] {
            "DELETE FROM processor_meta WHERE key='workspace_observation_attempt_v1:sha256'",
            "DELETE FROM processor_meta WHERE key='workspace_observation_attempt_v1'",
            "UPDATE processor_meta SET value='{}' WHERE key='workspace_observation_attempt_v1'",
            "DELETE FROM workspace_inventory_checkpoint" })
        {
            using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture);
            var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            var checkpoint = repository.CompleteScan(repository.BeginScan(context), "cursor", []); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => repository.LastAttempt());
            Assert.ThrowsExactly<InvalidDataException>(() => repository.BeginScan(context));
            Assert.ThrowsExactly<InvalidDataException>(() => repository.AssertCompletedScan(checkpoint));
        }
    }

    [TestMethod]
    public void InitialStageCompletionCannotReuseCheckpointAfterAnInterruptedNewerScan()
    {
        using var fixture = new Fixture(); var work = fixture.PrepareCompletionEvidence();
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); var attempt = repository.BeginScan(work.Checkpoint.Context);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => fixture.Store.CompleteInitialPublication(work.Plan, work.Checkpoint));
        Assert.IsNull(fixture.Store.InitialPublicationCompletion()); Assert.AreEqual(work.Checkpoint, repository.LastCheckpoint());
        var fresh = repository.CompleteScan(attempt, "fresh", repository.Known(attempt.Context));
        Assert.IsNotNull(fixture.Store.CompleteInitialPublication(work.Plan, fresh));
        repository.BeginScan(attempt.Context); Assert.IsNotNull(fixture.Store.InitialPublicationCompletion()); // Historical receipt remains readable.
    }
}
