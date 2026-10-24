using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void BasisPrecedesWritesAndRetriesCannotReplaceParentSnapshotOrHumanSettings()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true);
        var (journal, capture, basis) = PublicationCapture(fixture);
        var retained = journal.CapturePublicationBasis(capture.Scope.GenerationId, basis);
        Assert.AreEqual(retained, journal.CapturePublicationBasis(capture.Scope.GenerationId, basis));
        foreach (var changed in new[] { basis with { SettingsRevision = 1 }, basis with { SourcePolicySha256 = new string('b', 64) },
            basis with { ParentSequence = 1, Parent = new("account-A", "parent", capture.Scope.FolderId, 32, new string('c', 64)) },
            basis with { Snapshot = basis.Snapshot with { DiscoveryRevision = 2 } } })
            Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.CapturePublicationBasis(capture.Scope.GenerationId, changed));
        Assert.IsNull(new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(capture.CommitIntentId));
        var outbox = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        outbox.Prepare(Guid.NewGuid(), capture.Scope, "facts-index", "application/json", "{}"u8);
        Assert.AreEqual(retained, journal.Publication(capture.Scope.GenerationId));
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.AbandonUnpublished(capture.Scope.GenerationId));
    }

    [TestMethod]
    public void ExistingUploadCannotBeGivenALaterDifferentGenerationBasis()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var (journal, capture, basis) = PublicationCapture(fixture);
        new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Prepare(Guid.NewGuid(), capture.Scope, "facts-index", "application/json", "{}"u8);
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.CapturePublicationBasis(capture.Scope.GenerationId, basis));
        Assert.IsNull(journal.Publication(capture.Scope.GenerationId)); Assert.AreEqual(capture, journal.Active().Single());
    }

    [TestMethod]
    public void DependencyCheckpointRequiresExactVerifiedGenerationRolesAndRetainsExclusiveClaim()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var (journal, capture, basis) = PublicationCapture(fixture);
        journal.CapturePublicationBasis(capture.Scope.GenerationId, basis);
        var root = PublicationDependency(fixture, capture, "facts-index", "index", verified: false);
        var coverage = PublicationDependency(fixture, capture, "coverage-root", "coverage"); var plan = new CapturedCatalogCommitPlan(basis, root, coverage);
        Assert.ThrowsExactly<InvalidDataException>(() => journal.SealDependencies(capture.Scope.GenerationId, plan));
        Assert.AreEqual("publication-planned", journal.Read(capture.Scope.GenerationId)!.Phase);
        var outbox = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var rootId = outbox.Identities().Single(id => outbox.Read(id)!.FileId == "index");
        outbox.ConfirmVerified(rootId, root.AccountId, root.WorkspaceId, root.FileId, root.Sha256, root.Bytes, 1);
        var prepared = journal.SealDependencies(capture.Scope.GenerationId, plan);
        Assert.AreEqual("dependencies-ready", prepared.Capture.Phase); Assert.AreEqual(prepared, journal.SealDependencies(capture.Scope.GenerationId, plan));
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.SealDependencies(capture.Scope.GenerationId, plan with { RootIndex = coverage, SourceCoverage = root }));
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.AbandonUnpublished(capture.Scope.GenerationId));
        Assert.ThrowsExactly<CatalogGenerationInProgressException>(() => journal.Begin(capture.Scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid()));
        Assert.IsNull(new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(capture.CommitIntentId));
    }

    [TestMethod]
    public void ActualSignatureBeforePhaseCheckpointRecoversExactPlanAcrossRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CapturedCatalogPublication retained; byte[] envelope;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(registerSigning: true); var prepared = PreparedPublication(fixture); retained = prepared;
            var signing = new ProtocolSigningJournal(fixture.Database, fixture.Budget);
            var intent = signing.Reserve(prepared.Capture.CommitIntentId, prepared.Capture.Scope.ActorId, prepared.Capture.Scope.ActorEpoch, "catalog-commit",
                sequence => prepared.Plan!.EncodeBody(prepared.Capture.Scope, sequence));
            var sealedIntent = signing.Seal(intent.Id, intent.BodySha256, new ProtocolDocumentSigner(fixture.Publisher).Sign(intent.Body.Span));
            envelope = sealedIntent.Envelope!.Value.ToArray();
            Assert.AreEqual("dependencies-ready", prepared.Capture.Phase); // Deliberate lost phase update after actual sealing.
        }
        using var database = new ProcessorStateDatabase(root.Path); var budget = new StorageBudgetManager(new Space());
        var journal = new CatalogGenerationCaptureRepository(database, budget);
        Assert.AreEqual(retained, journal.Publication(retained.Capture.Scope.GenerationId));
        var recovered = journal.ConfirmCommitSealed(retained.Capture.Scope.GenerationId);
        Assert.AreEqual("commit-sealed", recovered.Capture.Phase); Assert.AreEqual(retained.Plan, recovered.Plan);
        Assert.AreEqual(recovered, journal.ConfirmCommitSealed(retained.Capture.Scope.GenerationId));
        CollectionAssert.AreEqual(envelope, new ProtocolSigningJournal(database, budget).Read(retained.Capture.CommitIntentId)!.Envelope!.Value.ToArray());
        Assert.AreEqual(recovered.Capture, journal.Active().Single());
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.AbandonUnpublished(retained.Capture.Scope.GenerationId));
    }

    [TestMethod]
    public void UnsealedOrDifferentReservedBodyCannotBeMarkedCommitSealed()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true); var prepared = PreparedPublication(fixture);
        var capture = prepared.Capture; var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.ConfirmCommitSealed(capture.Scope.GenerationId));
        var signing = new ProtocolSigningJournal(fixture.Database, fixture.Budget);
        signing.Reserve(capture.CommitIntentId, capture.Scope.ActorId, capture.Scope.ActorEpoch, "catalog-commit", sequence => prepared.Plan!.EncodeBody(capture.Scope, sequence));
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.ConfirmCommitSealed(capture.Scope.GenerationId));
        Assert.AreEqual("dependencies-ready", journal.Read(capture.Scope.GenerationId)!.Phase);
        fixture.Sql("UPDATE signing_intent SET kind='library-source-configuration'");
        Assert.ThrowsExactly<InvalidDataException>(() => journal.ConfirmCommitSealed(capture.Scope.GenerationId));
        Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM catalog_generation_capture"));
    }

    [TestMethod]
    public void FailedPlanWritesPreserveBasisClaimAndDoNotReserveASigningSequence()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true); var (journal, capture, basis) = PublicationCapture(fixture);
        fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.CapturePublicationBasis(capture.Scope.GenerationId, basis));
        Assert.IsNull(journal.Publication(capture.Scope.GenerationId)); fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        journal.CapturePublicationBasis(capture.Scope.GenerationId, basis);
        var plan = new CapturedCatalogCommitPlan(basis, PublicationDependency(fixture, capture, "facts-index", "index"), PublicationDependency(fixture, capture, "coverage-root", "coverage"));
        fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.SealDependencies(capture.Scope.GenerationId, plan));
        Assert.IsNull(journal.Publication(capture.Scope.GenerationId)!.Plan); Assert.AreEqual("publication-planned", journal.Read(capture.Scope.GenerationId)!.Phase);
        Assert.IsNull(new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(capture.CommitIntentId));
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void SavedBasisReadDoesNotGrantReconnectedAccountPermissionAndSafeAbandonRetainsHistory()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var (journal, capture, basis) = PublicationCapture(fixture);
        journal.CapturePublicationBasis(capture.Scope.GenerationId, basis);
        new DriveAccountRepository(fixture.Database).Disconnect(capture.Scope.AccountId, capture.Scope.AccountRevision);
        Assert.AreEqual(basis, journal.Publication(capture.Scope.GenerationId)!.Basis);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertLocalPublicationScope(capture.Scope));
        var abandoned = journal.AbandonUnpublished(capture.Scope.GenerationId);
        Assert.AreEqual(abandoned, journal.Read(capture.Scope.GenerationId)); Assert.AreEqual(basis, journal.Publication(capture.Scope.GenerationId)!.Basis);
        journal.AssertUnpublishedSnapshotCleanup(capture.SnapshotId);
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.CapturePublicationBasis(capture.Scope.GenerationId, basis));
    }

    [TestMethod]
    [DataRow("UPDATE catalog_generation_publication SET basis_hash=replace(basis_hash,substr(basis_hash,1,1),'z')")]
    [DataRow("UPDATE catalog_generation_publication SET plan_hash='corrupt'")]
    [DataRow("UPDATE catalog_generation_publication SET plan='{}'")]
    [DataRow("DELETE FROM catalog_generation_publication")]
    [DataRow("DELETE FROM workspace_publication WHERE role='facts-index'")]
    public void CorruptOrMissingPlanEvidenceNeverReleasesTheGenerationClaim(string sql)
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var prepared = PreparedPublication(fixture);
        fixture.Sql(sql); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        Assert.ThrowsExactly<InvalidDataException>(() => journal.Read(prepared.Capture.Scope.GenerationId));
        Assert.ThrowsExactly<InvalidDataException>(() => journal.Active());
        Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM catalog_generation_capture"));
        Assert.ThrowsExactly<InvalidDataException>(() => journal.AssertUnpublishedSnapshotCleanup(prepared.Capture.SnapshotId));
    }

    [TestMethod]
    public void LostBasisBeforeFirstUploadCannotBeReplacedByANewParentOrSettings()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var (journal, capture, basis) = PublicationCapture(fixture);
        journal.CapturePublicationBasis(capture.Scope.GenerationId, basis); fixture.Sql("DELETE FROM catalog_generation_publication");
        Assert.ThrowsExactly<InvalidDataException>(() => journal.Read(capture.Scope.GenerationId));
        Assert.ThrowsExactly<InvalidDataException>(() => journal.CapturePublicationBasis(capture.Scope.GenerationId, basis with { SettingsRevision = 1 }));
        Assert.AreEqual("publication-planned", fixture.Scalar("SELECT phase FROM catalog_generation_capture"));
    }

    [TestMethod]
    public void ActualFrozenSnapshotRemainsClaimedAndReadableThroughoutPreparationAndSealing()
    {
        using var fixture = new Fixture();
        var capture = SaveRetentionFixture(fixture, registerSigning: true); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var basis = new CapturedCatalogBasis(capture.Snapshot!, null, 0, fixture.Store.CurrentSourceConfiguration()!.EnvelopeSha256, 0, 1);
        var publication = journal.CapturePublicationBasis(capture.Scope.GenerationId, basis);
        using (var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget))
        using (var frozen = store.Open(publication.Capture, CancellationToken.None)) Assert.AreEqual(basis.Snapshot.Assets, frozen.Count);
        publication = journal.SealDependencies(capture.Scope.GenerationId, new(basis, PublicationDependency(fixture, capture, "facts-index", "index"), PublicationDependency(fixture, capture, "coverage-root", "coverage")));
        using (var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget))
        using (var frozen = store.Open(publication.Capture, CancellationToken.None)) Assert.AreEqual("retained-photo", frozen.ReadPage("").Assets.Single().FileId);
        var signing = new ProtocolSigningJournal(fixture.Database, fixture.Budget);
        var intent = signing.Reserve(capture.CommitIntentId, capture.Scope.ActorId, capture.Scope.ActorEpoch, "catalog-commit", sequence => publication.Plan!.EncodeBody(capture.Scope, sequence));
        signing.Seal(intent.Id, intent.BodySha256, new ProtocolDocumentSigner(fixture.Publisher).Sign(intent.Body.Span));
        publication = journal.ConfirmCommitSealed(capture.Scope.GenerationId);
        using (var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget))
        using (var frozen = store.Open(publication.Capture, CancellationToken.None)) Assert.AreEqual(basis.Snapshot.Assets, frozen.Count);
        Assert.IsTrue(File.Exists(RetainedPath(fixture, capture.SnapshotId))); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.AssertUnpublishedSnapshotCleanup(capture.SnapshotId));
    }

    [TestMethod]
    public void ActualVersionTenMigrationPreservesFrozenClaimsOriginalOutboxAndHumanJobs()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CatalogGenerationCapture capture; WorkspacePublicationRecord pending;
        Guid installation; Guid job;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(registerSigning: true); var prepared = PublicationCapture(fixture); capture = prepared.Capture;
            installation = fixture.Database.InstallationId;
            pending = new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Prepare(Guid.NewGuid(), capture.Scope, "facts-index", "application/json", "{}"u8);
            pending = new WorkspacePublicationRepository(fixture.Database, fixture.Budget).AssignFileId(pending.Id, pending.Sha256, "allocated-before-upgrade");
            job = Guid.NewGuid(); new DurableJobRepository(fixture.Database).Create(job, "takeout-import", "{\"caption\":\"Human draft\"}"u8);
        }
        RestoreActualVersionTenSchema(root.Path);
        using var upgraded = new ProcessorStateDatabase(root.Path); var budget = new StorageBudgetManager(new Space());
        Assert.AreEqual(installation, upgraded.InstallationId);
        var journal = new CatalogGenerationCaptureRepository(upgraded, budget); Assert.AreEqual(capture, journal.Read(capture.Scope.GenerationId));
        Assert.IsNull(journal.Publication(capture.Scope.GenerationId)); Assert.AreEqual(capture, journal.Active().Single());
        var work = new WorkspacePublicationRepository(upgraded, budget).Read(pending.Id)!;
        Assert.AreEqual(pending.Scope, work.Scope); Assert.AreEqual(pending.FileId, work.FileId); Assert.AreEqual(pending.Sha256, work.Sha256);
        CollectionAssert.AreEqual(pending.Payload.ToArray(), work.Payload.ToArray());
        using var connection = Open(root.Path); using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(12L, query.ExecuteScalar());
        query.CommandText = "SELECT captured_context FROM job"; StringAssert.Contains((string)query.ExecuteScalar()!, "Human draft");
        query.CommandText = "SELECT count(*) FROM signing_actor"; Assert.AreEqual(2L, query.ExecuteScalar());
    }

    [TestMethod]
    public void VersionElevenMigrationConflictRollsBackWholeCaptureReplacementAndKeepsRecoveryEvidence()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CatalogGenerationCapture capture;
        using (var fixture = new Fixture(root.Path)) { fixture.PrepareLocalPublication(); capture = PublicationCapture(fixture).Capture; }
        RestoreActualVersionTenSchema(root.Path);
        using (var connection = Open(root.Path))
        { using var query = connection.CreateCommand(); query.CommandText = "CREATE TABLE catalog_generation_publication(notes TEXT); INSERT INTO catalog_generation_publication VALUES('Keep conflict evidence')"; query.ExecuteNonQuery(); }
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => new ProcessorStateDatabase(root.Path));
        using (var connection = Open(root.Path))
        {
            using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(10L, query.ExecuteScalar());
            query.CommandText = "SELECT notes FROM catalog_generation_publication"; Assert.AreEqual("Keep conflict evidence", query.ExecuteScalar());
            query.CommandText = "SELECT count(*) FROM catalog_generation_capture"; Assert.AreEqual(1L, query.ExecuteScalar());
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE name='catalog_generation_capture'"; Assert.IsFalse(((string)query.ExecuteScalar()!).Contains("dependencies-ready", StringComparison.Ordinal));
            query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='catalog_generation_capture_v11'"; Assert.AreEqual(0L, query.ExecuteScalar());
            query.CommandText = "DROP TABLE catalog_generation_publication"; query.ExecuteNonQuery(); // Remove this test's deliberate conflict only.
        }
        using var recovered = new ProcessorStateDatabase(root.Path);
        Assert.AreEqual(capture, new CatalogGenerationCaptureRepository(recovered, new(new Space())).Read(capture.Scope.GenerationId));
    }

    private static void RestoreActualVersionTenSchema(string root)
    {
        using var connection = Open(root); using var transaction = connection.BeginTransaction(); using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            DROP TABLE catalog_generation_verification; DROP TABLE catalog_generation_publication;
            ALTER TABLE catalog_generation_capture RENAME TO newer_capture;
            CREATE TABLE catalog_generation_capture(
              id TEXT PRIMARY KEY, library_id TEXT NOT NULL, snapshot_id TEXT NOT NULL UNIQUE,
              commit_intent_id TEXT NOT NULL UNIQUE, scope TEXT NOT NULL CHECK(length(scope) BETWEEN 1 AND 4096), context_hash TEXT NOT NULL,
              phase TEXT NOT NULL CHECK(phase IN('capturing','frozen','abandoned')),
              snapshot TEXT CHECK(length(snapshot) BETWEEN 1 AND 4096), snapshot_hash TEXT,
              CHECK((snapshot IS NULL)=(snapshot_hash IS NULL)), CHECK(phase!='frozen' OR snapshot IS NOT NULL)
            ) STRICT;
            INSERT INTO catalog_generation_capture(id,library_id,snapshot_id,commit_intent_id,scope,context_hash,phase,snapshot,snapshot_hash)
              SELECT id,library_id,snapshot_id,commit_intent_id,scope,context_hash,phase,snapshot,snapshot_hash FROM newer_capture;
            DROP TABLE newer_capture;
            CREATE UNIQUE INDEX one_active_catalog_capture ON catalog_generation_capture(library_id) WHERE phase!='abandoned';
            PRAGMA user_version=10;
            """;
        query.ExecuteNonQuery(); transaction.Commit();
    }

    private static (CatalogGenerationCaptureRepository Journal, CatalogGenerationCapture Capture, CapturedCatalogBasis Basis) PublicationCapture(Fixture fixture)
    {
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
        capture = journal.SealSnapshot(capture.Scope.GenerationId, SnapshotProof(capture));
        var basis = new CapturedCatalogBasis(capture.Snapshot!, null, 0, fixture.Store.CurrentSourceConfiguration()!.EnvelopeSha256, 0, 1);
        return (journal, capture, basis);
    }
    private static CapturedCatalogPublication PreparedPublication(Fixture fixture)
    {
        var (journal, capture, basis) = PublicationCapture(fixture); journal.CapturePublicationBasis(capture.Scope.GenerationId, basis);
        return journal.SealDependencies(capture.Scope.GenerationId, new(basis, PublicationDependency(fixture, capture, "facts-index", "index"), PublicationDependency(fixture, capture, "coverage-root", "coverage")));
    }
    private static CatalogObjectReference PublicationDependency(Fixture fixture, CatalogGenerationCapture capture, string role, string fileId, bool verified = true)
    {
        // Journal-only fixture. Actual complete graph/current remote reads are tested by
        // the application reconciliation owner, not simulated by these local receipts.
        var outbox = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        var work = outbox.Prepare(Guid.NewGuid(), capture.Scope, role, "application/json", "{}"u8);
        outbox.AssignFileId(work.Id, work.Sha256, fileId);
        if (verified) outbox.ConfirmVerified(work.Id, capture.Scope.AccountId, capture.Scope.FolderId, fileId, work.Sha256, work.Payload.Length, 1);
        return new(capture.Scope.AccountId, fileId, capture.Scope.FolderId, work.Payload.Length, Convert.ToHexStringLower(SHA256.HashData(work.Payload.Span)));
    }
}
