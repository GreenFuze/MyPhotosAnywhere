using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void ActualSchemaElevenUpgradePreservesSealedPlanNativeHistoryAndOriginalOutbox()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CapturedCatalogPublication retained; Guid installation; byte[] signed;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(registerSigning: true); retained = SealCompletionFixture(fixture); installation = fixture.Database.InstallationId;
            signed = new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(retained.Capture.CommitIntentId)!.Envelope!.Value.ToArray();
            new DurableJobRepository(fixture.Database).Create(Guid.NewGuid(), "takeout-import", "{\"caption\":\"Preserve human draft\"}"u8);
        }
        RestoreActualVersionElevenSchema(root.Path); var exactPlan = CompletionPlanText(root.Path);
        using var upgraded = new ProcessorStateDatabase(root.Path); var budget = new StorageBudgetManager(new Space());
        Assert.AreEqual(installation, upgraded.InstallationId); var journal = new CatalogGenerationCaptureRepository(upgraded, budget);
        Assert.AreEqual(retained, journal.Publication(retained.Capture.Scope.GenerationId)); Assert.AreEqual(retained.Capture, journal.Active().Single());
        Assert.IsNull(journal.Verification(retained.Capture.Scope.GenerationId)); Assert.IsNull(journal.PublishedHead());
        CollectionAssert.AreEqual(signed, new ProtocolSigningJournal(upgraded, budget).Read(retained.Capture.CommitIntentId)!.Envelope!.Value.ToArray());
        Assert.AreEqual(exactPlan, CompletionPlanText(root.Path));
        using var connection = Open(root.Path); using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA user_version"; Assert.AreEqual(13L, query.ExecuteScalar());
        query.CommandText = "SELECT captured_context FROM job"; StringAssert.Contains((string)query.ExecuteScalar()!, "Preserve human draft");
        query.CommandText = "PRAGMA foreign_key_check"; Assert.IsNull(query.ExecuteScalar());
        query.CommandText = "SELECT count(*) FROM workspace_publication WHERE verified_version IS NOT NULL"; Assert.AreEqual(2L, query.ExecuteScalar());
    }

    [TestMethod]
    public void ActualSchemaTwelveFailureRollsBackBothForeignKeyTablesAndKeepsConflictEvidence()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CapturedCatalogPublication retained;
        using (var fixture = new Fixture(root.Path)) { fixture.PrepareLocalPublication(registerSigning: true); retained = SealCompletionFixture(fixture); }
        RestoreActualVersionElevenSchema(root.Path); var exactPlan = CompletionPlanText(root.Path);
        using (var sql = Open(root.Path))
        { using var query = sql.CreateCommand(); query.CommandText = "CREATE TABLE catalog_generation_verification(notes TEXT); INSERT INTO catalog_generation_verification VALUES('Keep completion migration evidence')"; query.ExecuteNonQuery(); }
        Assert.ThrowsExactly<SqliteException>(() => new ProcessorStateDatabase(root.Path));
        Assert.AreEqual(exactPlan, CompletionPlanText(root.Path));
        using (var sql = Open(root.Path))
        {
            using var query = sql.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(11L, query.ExecuteScalar());
            query.CommandText = "SELECT notes FROM catalog_generation_verification"; Assert.AreEqual("Keep completion migration evidence", query.ExecuteScalar());
            query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name LIKE '%_v12'"; Assert.AreEqual(0L, query.ExecuteScalar());
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE name='catalog_generation_capture'"; Assert.IsFalse(((string)query.ExecuteScalar()!).Contains("commit-verified", StringComparison.Ordinal));
            query.CommandText = "PRAGMA foreign_key_check"; Assert.IsNull(query.ExecuteScalar());
            query.CommandText = "DROP TABLE catalog_generation_verification"; query.ExecuteNonQuery(); // This deliberate conflict only.
        }
        using var recovered = new ProcessorStateDatabase(root.Path);
        Assert.AreEqual(retained, new CatalogGenerationCaptureRepository(recovered, new(new Space())).Publication(retained.Capture.Scope.GenerationId));
    }

    [TestMethod]
    public void LaterPhaseWithoutItsActualVerificationCannotReleaseAnActiveSnapshotClaim()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(registerSigning: true); var sealedPlan = SealCompletionFixture(fixture);
        foreach (var phase in new[] { "commit-verified", "complete", "snapshot-retiring", "retired" })
        {
            fixture.Sql("UPDATE catalog_generation_capture SET phase='" + phase + "'");
            var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
            Assert.ThrowsExactly<InvalidDataException>(() => journal.Read(sealedPlan.Capture.Scope.GenerationId));
            if (phase == "retired")
            {
                Assert.ThrowsExactly<InvalidDataException>(() => journal.Active());
                Assert.ThrowsExactly<InvalidDataException>(() => journal.Begin(sealedPlan.Capture.Scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid()));
            }
            fixture.Sql("UPDATE catalog_generation_capture SET phase='commit-sealed'");
            Assert.AreEqual(sealedPlan.Capture, journal.Active().Single());
        }
    }

    private static CapturedCatalogPublication SealCompletionFixture(Fixture fixture)
    {
        var prepared = PreparedPublication(fixture); var signing = new ProtocolSigningJournal(fixture.Database, fixture.Budget);
        var intent = signing.Reserve(prepared.Capture.CommitIntentId, prepared.Capture.Scope.ActorId, prepared.Capture.Scope.ActorEpoch,
            "catalog-commit", sequence => prepared.Plan!.EncodeBody(prepared.Capture.Scope, sequence));
        signing.Seal(intent.Id, intent.BodySha256, new ProtocolDocumentSigner(fixture.Publisher).Sign(intent.Body.Span));
        return new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget).ConfirmCommitSealed(prepared.Capture.Scope.GenerationId);
    }
    private static string CompletionPlanText(string root)
    {
        using var sql = Open(root); using var query = sql.CreateCommand();
        query.CommandText = "SELECT basis||char(0)||basis_hash||char(0)||plan||char(0)||plan_hash FROM catalog_generation_publication";
        return (string)query.ExecuteScalar()!;
    }
    private static void RestoreActualVersionElevenSchema(string root)
    {
        using var sql = Open(root); using var transaction = sql.BeginTransaction(); using var query = sql.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            DROP TABLE catalog_generation_access_attempt; DROP TABLE catalog_generation_verification;
            ALTER TABLE catalog_generation_publication RENAME TO newer_publication;
            ALTER TABLE catalog_generation_capture RENAME TO newer_capture;
            CREATE TABLE catalog_generation_capture(
              id TEXT PRIMARY KEY, library_id TEXT NOT NULL, snapshot_id TEXT NOT NULL UNIQUE,
              commit_intent_id TEXT NOT NULL UNIQUE, scope TEXT NOT NULL CHECK(length(scope) BETWEEN 1 AND 4096), context_hash TEXT NOT NULL,
              phase TEXT NOT NULL CHECK(phase IN('capturing','frozen','publication-planned','dependencies-ready','commit-sealed','abandoned')),
              snapshot TEXT CHECK(length(snapshot) BETWEEN 1 AND 4096), snapshot_hash TEXT,
              CHECK((snapshot IS NULL)=(snapshot_hash IS NULL)),
              CHECK(phase NOT IN('frozen','publication-planned','dependencies-ready','commit-sealed') OR snapshot IS NOT NULL)
            ) STRICT;
            INSERT INTO catalog_generation_capture SELECT * FROM newer_capture;
            CREATE TABLE catalog_generation_publication(
              id TEXT PRIMARY KEY REFERENCES catalog_generation_capture(id),
              basis TEXT NOT NULL CHECK(length(basis) BETWEEN 1 AND 16384), basis_hash TEXT NOT NULL CHECK(length(basis_hash)=64),
              plan TEXT CHECK(length(plan) BETWEEN 1 AND 16384), plan_hash TEXT,
              CHECK((plan IS NULL)=(plan_hash IS NULL))
            ) STRICT;
            INSERT INTO catalog_generation_publication SELECT * FROM newer_publication;
            DROP TABLE newer_publication;
            DROP TABLE newer_capture;
            CREATE UNIQUE INDEX one_active_catalog_capture ON catalog_generation_capture(library_id) WHERE phase!='abandoned';
            PRAGMA user_version=11;
            """;
        query.ExecuteNonQuery(); transaction.Commit();
    }
}
