using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void ActualSchemaTwelveAccessUpgradePreservesCompletedHeadSignaturePlanAndHumanDraft()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); PublishedCatalogHead head; CatalogGenerationVerification receipt; Guid installation;
        string exactPlan; byte[] signed;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(registerSigning: true); var evidence = LocalCompletionEvidence(fixture);
            var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget); var id = evidence.Publication.Capture.Scope.GenerationId;
            receipt = journal.ConfirmCurrentCommitVerified(id, evidence.Checkpoint, evidence.Commit); head = journal.CompleteCurrentGeneration(id, evidence.Checkpoint, evidence.Commit);
            installation = fixture.Database.InstallationId; exactPlan = CompletionPlanText(root.Path);
            signed = new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(evidence.Publication.Capture.CommitIntentId)!.Envelope!.Value.ToArray();
            new DurableJobRepository(fixture.Database).Create(Guid.NewGuid(), "takeout-import", "{\"caption\":\"Preserve human draft\"}"u8);
        }
        using (var sql = Open(root.Path)) { using var query = sql.CreateCommand(); query.CommandText = "DROP TABLE catalog_generation_access_attempt; PRAGMA user_version=12"; query.ExecuteNonQuery(); }
        using var upgraded = new ProcessorStateDatabase(root.Path); var budget = new StorageBudgetManager(new Space());
        var recovered = new CatalogGenerationCaptureRepository(upgraded, budget);
        Assert.AreEqual(installation, upgraded.InstallationId); Assert.AreEqual(head, recovered.PublishedHead()); Assert.AreEqual(receipt, recovered.Verification(head.GenerationId));
        Assert.AreEqual(exactPlan, CompletionPlanText(root.Path));
        CollectionAssert.AreEqual(signed, new ProtocolSigningJournal(upgraded, budget).Read(receipt.CommitIntentId)!.Envelope!.Value.ToArray());
        Assert.IsNull(recovered.AccessAttempt(head.GenerationId, receipt.Scope.AccountRevision + 1));
        using var connection = Open(root.Path); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version"; Assert.AreEqual(13L, command.ExecuteScalar());
        command.CommandText = "PRAGMA foreign_key_check"; Assert.IsNull(command.ExecuteScalar());
        command.CommandText = "SELECT captured_context FROM job"; StringAssert.Contains((string)command.ExecuteScalar()!, "Preserve human draft");
    }

    [TestMethod]
    public void ActualAccessMigrationFailurePreservesOriginalSchemaAndConflictEvidenceUntilExplicitRepair()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CapturedCatalogPublication retained;
        using (var fixture = new Fixture(root.Path)) { fixture.PrepareLocalPublication(registerSigning: true); retained = SealCompletionFixture(fixture); }
        var exactPlan = CompletionPlanText(root.Path);
        using (var sql = Open(root.Path))
        {
            using var query = sql.CreateCommand(); query.CommandText = """
                DROP TABLE catalog_generation_access_attempt; PRAGMA user_version=12;
                CREATE TABLE catalog_generation_access_attempt(notes TEXT);
                INSERT INTO catalog_generation_access_attempt VALUES('Preserve renewal conflict evidence');
                """; query.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<SqliteException>(() => new ProcessorStateDatabase(root.Path)); Assert.AreEqual(exactPlan, CompletionPlanText(root.Path));
        using (var sql = Open(root.Path))
        {
            using var query = sql.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(12L, query.ExecuteScalar());
            query.CommandText = "SELECT notes FROM catalog_generation_access_attempt"; Assert.AreEqual("Preserve renewal conflict evidence", query.ExecuteScalar());
            query.CommandText = "PRAGMA foreign_key_check"; Assert.IsNull(query.ExecuteScalar());
            query.CommandText = "DROP TABLE catalog_generation_access_attempt"; query.ExecuteNonQuery(); // Only this test's deliberately injected conflict.
        }
        using var recovered = new ProcessorStateDatabase(root.Path);
        Assert.AreEqual(retained, new CatalogGenerationCaptureRepository(recovered, new(new Space())).Publication(retained.Capture.Scope.GenerationId));
    }
}
