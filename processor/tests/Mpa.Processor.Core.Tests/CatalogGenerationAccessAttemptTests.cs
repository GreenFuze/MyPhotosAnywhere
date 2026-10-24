using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void RenewedAccessRetainsOriginalCaptureAndFirstAttemptWithoutOpeningStrictWriteFences()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true);
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var captured = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
        var evidence = RenewedAccessEvidence(fixture); var before = journal.Read(captured.Scope.GenerationId);
        var first = journal.RecordRenewedAccess(captured.Scope.GenerationId, evidence.Current, evidence.Checkpoint, evidence.Signing, evidence.Publications);
        Assert.AreEqual(before, journal.Active().Single()); Assert.AreEqual(captured.Scope, first.Scope);
        Assert.AreEqual(captured.SnapshotId, first.SnapshotId); Assert.AreEqual(captured.CommitIntentId, first.CommitIntentId);
        Assert.AreEqual(first, journal.AccessAttempt(captured.Scope.GenerationId, evidence.Current.AccountRevision));
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var scan = observations.BeginScan(evidence.Checkpoint.Context);
        var later = observations.CompleteScan(scan, "second-completed-scan", []);
        Assert.AreEqual(first, journal.RecordRenewedAccess(captured.Scope.GenerationId, evidence.Current, later, evidence.Signing, evidence.Publications));
        Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM catalog_generation_access_attempt"));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertLocalPublicationScope(captured.Scope));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => new WorkspacePublicationRepository(fixture.Database, fixture.Budget)
            .Prepare(Guid.NewGuid(), captured.Scope, "facts-index", "application/json", "{}"u8));
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void RenewedAccessRequiresAnActualCompletedScanAndUnchangedCurrentInventoriesAndSemanticPolicy()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true);
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
        var evidence = RenewedAccessEvidence(fixture);
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); var scan = observations.BeginScan(evidence.Checkpoint.Context);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current,
            evidence.Checkpoint, evidence.Signing, evidence.Publications));
        var actual = observations.CompleteScan(scan, "after-incomplete", []);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId,
            evidence.Current with { AuthoritySha256 = new string('0',64) }, actual, evidence.Signing, evidence.Publications));
        Assert.ThrowsExactly<ProtocolSigningInventoryChangedException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current,
            actual, evidence.Signing with { Sha256 = new string('0',64) }, evidence.Publications));
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current,
            actual, evidence.Signing, evidence.Publications with { Sha256 = new string('0',64) }));
        fixture.DisablePublicationSource();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current,
            actual, evidence.Signing, evidence.Publications));
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM catalog_generation_access_attempt")); Assert.AreEqual(capture, journal.Active().Single());
    }

    [TestMethod]
    public void LostAccessCheckpointAndLowStoragePreserveOriginalWorkAndRetryOnlyTheCurrentRevision()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true);
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
        var evidence = RenewedAccessEvidence(fixture); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current,
            evidence.Checkpoint, evidence.Signing, evidence.Publications));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        Assert.IsNull(journal.AccessAttempt(capture.Scope.GenerationId, evidence.Current.AccountRevision));
        fixture.Sql("CREATE TRIGGER fail_access BEFORE INSERT ON catalog_generation_access_attempt BEGIN SELECT RAISE(ABORT,'synthetic failed access checkpoint'); END");
        Assert.ThrowsExactly<SqliteException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current,
            evidence.Checkpoint, evidence.Signing, evidence.Publications));
        fixture.Sql("DROP TRIGGER fail_access");
        var first = journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current, evidence.Checkpoint, evidence.Signing, evidence.Publications);
        var next = RenewedAccessEvidence(fixture);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current,
            evidence.Checkpoint, evidence.Signing, evidence.Publications));
        var second = journal.RecordRenewedAccess(capture.Scope.GenerationId, next.Current, next.Checkpoint, next.Signing, next.Publications);
        Assert.AreNotEqual(first.Id, second.Id); Assert.AreEqual(first.Scope, second.Scope);
        Assert.AreEqual(first, journal.AccessAttempt(capture.Scope.GenerationId, first.AccountRevision));
        Assert.AreEqual(second, journal.AccessAttempt(capture.Scope.GenerationId, second.AccountRevision));
        Assert.AreEqual(capture, journal.Active().Single()); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void AccessAttemptSurvivesNativeRestartAndDisconnectButCorruptionDoesNotBecomeAReplacementAttempt()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CatalogGenerationAccessAttempt retained;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(registerSigning: true); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
            var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
            var evidence = RenewedAccessEvidence(fixture);
            retained = journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current, evidence.Checkpoint, evidence.Signing, evidence.Publications);
            var digest = (string)fixture.Scalar("SELECT document_hash FROM catalog_generation_access_attempt");
            fixture.Sql("UPDATE catalog_generation_access_attempt SET document_hash='" + new string('0',64) + "'");
            Assert.ThrowsExactly<InvalidDataException>(() => journal.AccessAttempt(capture.Scope.GenerationId, retained.AccountRevision));
            Assert.ThrowsExactly<InvalidDataException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId, evidence.Current, evidence.Checkpoint, evidence.Signing, evidence.Publications));
            // Restore only this test's injected digest corruption, without deleting or resetting the journal.
            fixture.Sql("UPDATE catalog_generation_access_attempt SET document_hash='" + digest + "'");
            new DriveAccountRepository(fixture.Database).Disconnect(retained.Scope.AccountId, retained.AccountRevision);
        }
        using var restarted = new ProcessorStateDatabase(root.Path);
        var recovered = new CatalogGenerationCaptureRepository(restarted, new(new Space()));
        Assert.AreEqual(retained, recovered.AccessAttempt(retained.Scope.GenerationId, retained.AccountRevision));
        Assert.AreEqual(retained.Scope, recovered.Active().Single().Scope);
    }

    [TestMethod]
    public void RenewedAccessHistoryFailsAtItsBoundWithoutTruncatingOrReplacingEarlierAttempts()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true);
        var journal = new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()),Guid.NewGuid(),Guid.NewGuid());
        var history = new List<CatalogGenerationAccessAttempt>();
        for (var index=0; index<CatalogGenerationCaptureRepository.MaximumAccessAttemptsPerGeneration; index++)
        {
            var evidence = RenewedAccessEvidence(fixture);
            history.Add(journal.RecordRenewedAccess(capture.Scope.GenerationId,evidence.Current,evidence.Checkpoint,evidence.Signing,evidence.Publications));
        }
        var next = RenewedAccessEvidence(fixture);
        Assert.ThrowsExactly<InvalidDataException>(() => journal.RecordRenewedAccess(capture.Scope.GenerationId,next.Current,next.Checkpoint,next.Signing,next.Publications));
        Assert.IsNull(journal.AccessAttempt(capture.Scope.GenerationId,next.Current.AccountRevision));
        Assert.AreEqual((long)history.Count,fixture.Scalar("SELECT count(*) FROM catalog_generation_access_attempt"));
        foreach (var original in history) Assert.AreEqual(original,journal.AccessAttempt(capture.Scope.GenerationId,original.AccountRevision));
        Assert.AreEqual(capture,journal.Active().Single()); Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    private static (LocalCatalogPublicationAuthority Current, WorkspaceInventoryCheckpoint Checkpoint, ProtocolSigningInventory Signing, WorkspacePublicationInventory Publications) RenewedAccessEvidence(Fixture fixture)
    {
        // Journal-only native fixture. Actual network/native admission is tested through
        // the concrete reconciler, not fabricated by this local checkpoint fixture.
        var accounts = new DriveAccountRepository(fixture.Database); var old = fixture.Store.CaptureLocalPublicationAuthority();
        accounts.Disconnect(old.AccountId, old.AccountRevision); var pending = accounts.Begin(Guid.NewGuid(), old.AccountId);
        accounts.Promote(pending.Id, old.AccountId, "Reconnected", null);
        var current = fixture.Store.CaptureLocalPublicationAuthority();
        var context = new WorkspaceObservationContext(current.LibraryId,current.AccountId,current.FolderId,current.AccountRevision,null);
        var observations = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var checkpoint = observations.CompleteScan(observations.BeginScan(context), "actual-renewed-read", []);
        return (current, checkpoint, new ProtocolSigningJournal(fixture.Database, fixture.Budget).Inventory(current.LibraryId),
            new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Inventory(context));
    }

}
