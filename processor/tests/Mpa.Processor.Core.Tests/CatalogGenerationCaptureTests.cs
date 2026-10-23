using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void CaptureAdmissionRetainsEveryIdentityAndDatabaseEnforcesOneActiveGeneration()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var scope = Scope(fixture.Store.CaptureLocalPublicationAuthority()); var snapshot = Guid.NewGuid(); var intent = Guid.NewGuid();
        var captured = journal.Begin(scope, snapshot, intent); Assert.AreEqual("capturing", captured.Phase); Assert.IsNull(captured.Snapshot);
        Assert.AreEqual(captured, journal.Begin(scope, snapshot, intent)); Assert.AreEqual(captured, journal.Active().Single());
        Assert.ThrowsExactly<CatalogGenerationInProgressException>(() => journal.Begin(scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid()));
        // The SQL constraint protects ownership even against a second state writer.
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Sql("INSERT INTO catalog_generation_capture SELECT '00000000-0000-4000-8000-000000000001',library_id,'00000000-0000-4000-8000-000000000002','00000000-0000-4000-8000-000000000003',scope,context_hash,phase,snapshot,snapshot_hash FROM catalog_generation_capture"));
        Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM catalog_generation_capture"));
    }

    [TestMethod]
    public void FrozenCaptureProofAndOriginalScopeSurviveRestartWithoutCurrentAuthorityReplacement()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CatalogGenerationCapture retained;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
            var captured = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
            retained = journal.SealSnapshot(captured.Scope.GenerationId, SnapshotProof(captured));
            Assert.AreEqual(retained, journal.SealSnapshot(captured.Scope.GenerationId, SnapshotProof(captured)));
            new DriveAccountRepository(fixture.Database).Disconnect("account-A", 1);
        }
        using var database = new ProcessorStateDatabase(root.Path); var budget = new StorageBudgetManager(new Space()); var restarted = new CatalogGenerationCaptureRepository(database, budget);
        Assert.AreEqual(retained, restarted.Read(retained.Scope.GenerationId)); Assert.AreEqual(retained, restarted.Active().Single());
        Assert.AreEqual(retained, restarted.Begin(retained.Scope, retained.SnapshotId, retained.CommitIntentId));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => restarted.Begin(retained.Scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid()));
    }

    [TestMethod]
    public void CaptureRetryCannotRemintRetargetOrReplaceFrozenBytes()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var captured = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid()); var id = captured.Scope.GenerationId;
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.Begin(captured.Scope, Guid.NewGuid(), captured.CommitIntentId));
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.Begin(captured.Scope, captured.SnapshotId, Guid.NewGuid()));
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.Begin(captured.Scope with { AccountRevision = 2 }, captured.SnapshotId, captured.CommitIntentId));
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.SealSnapshot(id, SnapshotProof(captured) with { SnapshotId = Guid.NewGuid() }));
        var sealedCapture = journal.SealSnapshot(id, SnapshotProof(captured));
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.SealSnapshot(id, SnapshotProof(captured) with { Sha256 = new string('b', 64) }));
        Assert.AreEqual(sealedCapture, journal.Read(id));
    }

    [TestMethod]
    public void CaptureHasNoFreshAdmissionWithoutCurrentPolicyAndFailedWritesRetainClaims()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var scope = Scope(fixture.Store.CaptureLocalPublicationAuthority()); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.Begin(scope, Guid.NewGuid(), Guid.NewGuid())); Assert.IsNull(journal.Read(scope.GenerationId));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        var captured = journal.Begin(scope, Guid.NewGuid(), Guid.NewGuid()); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => journal.SealSnapshot(scope.GenerationId, SnapshotProof(captured)));
        Assert.AreEqual(captured, journal.Active().Single()); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte; journal.AbandonUnpublished(scope.GenerationId);
        fixture.Store.SelectPublisherPolicy(fixture.Policy(3, revoked: true));
        Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(() => journal.Begin(scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid()));
    }

    [TestMethod]
    public void UnpublishedAbandonmentRetainsHistoryAndNeverDiscardsUploadedWork()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid()); journal.SealSnapshot(capture.Scope.GenerationId, SnapshotProof(capture));
        var abandoned = journal.AbandonUnpublished(capture.Scope.GenerationId); Assert.AreEqual("abandoned", abandoned.Phase); Assert.IsNotNull(abandoned.Snapshot); Assert.AreEqual(0, journal.Active().Count);
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => journal.Begin(capture.Scope, capture.SnapshotId, capture.CommitIntentId));
        var next = journal.Begin(capture.Scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid());
        new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Prepare(Guid.NewGuid(), next.Scope, "facts-index", "application/json", "{}"u8);
        Assert.ThrowsExactly<InvalidOperationException>(() => journal.AbandonUnpublished(next.Scope.GenerationId)); Assert.AreEqual(next, journal.Active().Single());
        Assert.AreEqual(2L, fixture.Scalar("SELECT count(*) FROM catalog_generation_capture"));
    }

    [TestMethod]
    [DataRow("UPDATE catalog_generation_capture SET context_hash='changed'")]
    [DataRow("UPDATE catalog_generation_capture SET snapshot_hash='changed'")]
    [DataRow("UPDATE catalog_generation_capture SET snapshot='{}'")]
    [DataRow("UPDATE catalog_generation_capture SET scope='{}'")]
    public void CorruptCaptureProofNeverBecomesCleanupOrReplacementAuthority(string sql)
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var captured = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid()); journal.SealSnapshot(captured.Scope.GenerationId, SnapshotProof(captured)); fixture.Sql(sql);
        Assert.ThrowsExactly<InvalidDataException>(() => journal.Read(captured.Scope.GenerationId)); Assert.ThrowsExactly<InvalidDataException>(() => journal.Active());
        Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM catalog_generation_capture"));
    }

    [TestMethod]
    public void VersionEightMigrationPreservesAuthorityJobsAndDoesNotInventCaptureWork()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); LocalCatalogPublicationAuthority authority; Guid installation;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(); authority = fixture.Store.CaptureLocalPublicationAuthority(); installation = fixture.Database.InstallationId;
            new DurableJobRepository(fixture.Database).Create(Guid.NewGuid(), "takeout-import", "{\"caption\":\"Human draft remains\"}"u8);
        }
        using (var old = Open(root.Path)) { using var command = old.CreateCommand(); command.CommandText = "DROP INDEX workspace_publication_generation; DROP TABLE catalog_generation_capture; PRAGMA user_version=8"; command.ExecuteNonQuery(); }
        using var upgraded = new ProcessorStateDatabase(root.Path); Assert.AreEqual(installation, upgraded.InstallationId);
        Assert.AreEqual(authority, new LibraryAuthorityRepository(upgraded).CaptureLocalPublicationAuthority());
        Assert.AreEqual(0, new CatalogGenerationCaptureRepository(upgraded, new(new Space())).Active().Count);
        using var sql = Open(root.Path); using var query = sql.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(9L, query.ExecuteScalar());
        query.CommandText = "SELECT captured_context FROM job"; StringAssert.Contains((string)query.ExecuteScalar()!, "Human draft remains");
    }

    [TestMethod]
    public void ExistingGenerationUploadCannotBeAdoptedByAFreshSnapshotOwner()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); var scope = Scope(fixture.Store.CaptureLocalPublicationAuthority());
        var id = Guid.NewGuid(); var outbox = new WorkspacePublicationRepository(fixture.Database, fixture.Budget);
        outbox.Prepare(id, scope, "facts-index", "application/json", "{}"u8);
        var captures = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => captures.Begin(scope, Guid.NewGuid(), Guid.NewGuid()));
        Assert.IsNull(captures.Read(scope.GenerationId)); Assert.IsNotNull(outbox.Read(id));
    }

    [TestMethod]
    public void EvenAnUnsealedCommitIntentPreventsAbandoningItsGenerationSnapshot()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true);
        var scope = Scope(fixture.Store.CaptureLocalPublicationAuthority()); var captures = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = captures.Begin(scope, Guid.NewGuid(), Guid.NewGuid());
        var work = new ProtocolSigningJournal(fixture.Database, fixture.Budget).Reserve(capture.CommitIntentId, scope.ActorId, scope.ActorEpoch, "catalog-commit",
            sequence => new Mpa.Processor.Protocol.CanonicalJson().Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "mpa", major = 1, minor = 0, libraryId = scope.LibraryId.ToString("D"), actorId = scope.ActorId.ToString("D"),
                epoch = scope.ActorEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), sequence = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kind = "catalog-commit", requiredCapabilities = Array.Empty<string>(), payload = new { retained = true }
            })));
        Assert.IsNull(work.Envelope); Assert.ThrowsExactly<InvalidOperationException>(() => captures.AbandonUnpublished(scope.GenerationId));
        Assert.AreEqual(capture, captures.Active().Single()); Assert.IsNotNull(new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(work.Id));
    }

    [TestMethod]
    public void FailedVersionNineMigrationRollsBackWithoutReplacingExistingState()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication();
            fixture.Sql("DROP INDEX workspace_publication_generation; DROP TABLE catalog_generation_capture; CREATE TABLE one_active_catalog_capture(notes TEXT); INSERT INTO one_active_catalog_capture VALUES('Preserve migration evidence'); PRAGMA user_version=8");
        }
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => new ProcessorStateDatabase(root.Path));
        using (var sql = Open(root.Path))
        {
            using var query = sql.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(8L, query.ExecuteScalar());
            query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='catalog_generation_capture'"; Assert.AreEqual(0L, query.ExecuteScalar());
            query.CommandText = "SELECT notes FROM one_active_catalog_capture"; Assert.AreEqual("Preserve migration evidence", query.ExecuteScalar());
            query.CommandText = "DROP TABLE one_active_catalog_capture"; query.ExecuteNonQuery(); // Remove this test's injected conflict.
        }
        using var recovered = new ProcessorStateDatabase(root.Path); Assert.IsNotNull(new LibraryAuthorityRepository(recovered).CurrentSourceConfiguration());
    }

    private static CatalogSnapshotDescriptor SnapshotProof(CatalogGenerationCapture capture) => new(capture.SnapshotId, capture.Scope.LibraryId,
        capture.Scope.ConfigurationRevision, 1, 12, 8192, new string('a', 64)); // Journal-only fixture; private file verification is the snapshot owner's duty.
}
