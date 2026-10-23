using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    private static readonly WorkspaceDocumentObservation FirstObservation = new("policy-first", 3, 1024, new string('a', 64));
    private static WorkspaceObservationContext ObservationContext(Fixture fixture) => new(fixture.Store.Controller()!.LibraryId, "account-A", "metadata-workspace", 1, null);

    [TestMethod]
    public void ObservationsSurviveRestartAndCheckpointOnlyACompleteExactInventory()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); WorkspaceObservationContext context; WorkspaceInventoryCheckpoint checkpoint;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            Assert.IsNull(repository.LastCheckpoint()); Assert.AreEqual(FirstObservation, repository.RememberRead(context, FirstObservation));
            checkpoint = repository.Complete(context, "actual-cursor-1", [FirstObservation]); Assert.AreEqual(1L, checkpoint.Revision);
            Assert.AreEqual(checkpoint, repository.Complete(context, "actual-cursor-1", [FirstObservation]));
            Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        }
        using var database = new ProcessorStateDatabase(root.Path); var recovered = new WorkspaceObservationRepository(database, new(new Space()));
        Assert.AreEqual(checkpoint, recovered.LastCheckpoint()); CollectionAssert.AreEqual(new[] { FirstObservation }, recovered.Known(context));
        var next = recovered.Complete(context, "actual-cursor-2", [FirstObservation]); Assert.AreEqual(2L, next.Revision); Assert.AreEqual(checkpoint.InventorySha256, next.InventorySha256);
    }

    [TestMethod]
    public void MissingOrRewrittenDocumentsCannotAdvanceCursorOrEraseKnownHistory()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        repository.RememberRead(context, FirstObservation); var before = repository.Complete(context, "cursor", [FirstObservation]);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.Complete(context, "missing", []));
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.Complete(context, "duplicate", [FirstObservation, FirstObservation]));
        foreach (var changed in new[] { FirstObservation with { Version = 2 }, FirstObservation with { Bytes = 12 }, FirstObservation with { Sha256 = new string('b', 64), Version = 4 } })
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.RememberRead(context, changed));
        Assert.AreEqual(before, repository.LastCheckpoint()); Assert.AreEqual(FirstObservation, repository.Known(context).Single());
        var updated = FirstObservation with { Version = 4 }; repository.RememberRead(context, updated);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.Complete(context, "stale", [FirstObservation]));
        Assert.AreEqual(2L, repository.Complete(context, "updated", [updated]).Revision);
    }

    [TestMethod]
    public void ForeignContextAndRetiredAccessCannotRetargetObservationHistory()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        repository.RememberRead(context, FirstObservation); var before = repository.Complete(context, "cursor", [FirstObservation]);
        foreach (var changed in new[] { context with { LibraryId = Guid.NewGuid() }, context with { AccountId = "foreign" }, context with { FolderId = "foreign" } })
            Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => repository.Known(changed));
        var accounts = new DriveAccountRepository(fixture.Database); accounts.Disconnect("account-A", 1);
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => repository.Known(context));
        var pending = accounts.Begin(Guid.NewGuid(), "account-A"); var reconnected = accounts.Promote(pending.Id, "account-A", "Again", null);
        var renewed = context with { AccountRevision = reconnected.Revision }; Assert.AreEqual(FirstObservation, repository.Known(renewed).Single());
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => repository.Complete(context, "old-access", [FirstObservation]));
        Assert.AreEqual(before.Revision + 1, repository.Complete(renewed, "new-access", [FirstObservation]).Revision);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.Complete(renewed with { SharedDriveId = "foreign" }, "wrong-drive", [FirstObservation]));
    }

    [TestMethod]
    public void FailedObservationAndCursorWritesAreAtomicAndKeepEarlierEvidence()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        repository.RememberRead(context, FirstObservation); var before = repository.Complete(context, "cursor", [FirstObservation]);
        var second = new WorkspaceDocumentObservation("policy-second", 1, 512, new string('b', 64)); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => repository.RememberRead(context, second));
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => repository.Complete(context, "new-cursor", [FirstObservation]));
        Assert.AreEqual(before, repository.LastCheckpoint()); Assert.AreEqual(1, repository.Known(context).Count);
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_observation BEFORE INSERT ON workspace_document_observation BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => repository.RememberRead(context, second)); Assert.AreEqual(1, repository.Known(context).Count);
        fixture.Sql("DROP TRIGGER fail_observation"); repository.RememberRead(context, second);
        fixture.Sql("CREATE TRIGGER fail_inventory BEFORE INSERT ON workspace_inventory_checkpoint BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => repository.Complete(context, "new-cursor", [FirstObservation, second]));
        Assert.AreEqual(before, repository.LastCheckpoint()); Assert.AreEqual(2, repository.Known(context).Count);
    }

    [TestMethod]
    [DataRow("UPDATE workspace_document_observation SET row_hash='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [DataRow("UPDATE workspace_document_observation SET document='{}'")]
    public void DamagedObservationCannotBecomeNewInventoryOrCleanupAuthority(string sql)
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        repository.RememberRead(context, FirstObservation);
        fixture.Sql(sql);
        Assert.ThrowsExactly<InvalidDataException>(() => repository.Known(context)); Assert.IsNull(repository.LastCheckpoint());
    }

    [TestMethod]
    public void MaximumInventoryUsesBoundedRecordsWithoutRelaxingProtocolArrayLimits()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var all = new List<WorkspaceDocumentObservation>();
        using (var connection = Open(fixture.Database.StateDirectory))
        using (var transaction = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction; insert.CommandText = "INSERT INTO workspace_document_observation(id,document,row_hash) VALUES($id,$document,$hash)";
            var id = insert.Parameters.Add("$id", SqliteType.Text); var text = insert.Parameters.Add("$document", SqliteType.Text); var digest = insert.Parameters.Add("$hash", SqliteType.Text);
            for (var number = 0; number < WorkspaceObservationRepository.MaximumDocuments; number++)
            {
                var observation = new WorkspaceDocumentObservation("policy-" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), 1, 512, FirstObservation.Sha256); all.Add(observation);
                var bytes = new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(new { context.LibraryId, context.AccountId, context.FolderId, Observation = observation }));
                id.Value = observation.FileId; text.Value = Encoding.UTF8.GetString(bytes); digest.Value = Convert.ToHexStringLower(SHA256.HashData(bytes)); Assert.AreEqual(1, insert.ExecuteNonQuery());
            }
            transaction.Commit();
        }
        var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget); Assert.AreEqual(4096, repository.Known(context).Count);
        var checkpoint = repository.Complete(context, "full-cursor", all); Assert.AreEqual(4096, checkpoint.Documents);
        Assert.AreEqual(checkpoint, repository.Complete(context, "full-cursor", all.AsEnumerable().Reverse().ToArray()));
        Assert.ThrowsExactly<InvalidOperationException>(() => repository.RememberRead(context, FirstObservation));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, 1001).ToArray())));
    }

    [TestMethod]
    public void CursorRemainsOpaqueAndBoundedIncludingNonAsciiText()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var cursor = new string('ש', 4096); var checkpoint = repository.Complete(context, cursor, []); Assert.AreEqual(cursor, repository.LastCheckpoint()!.Cursor);
        Assert.ThrowsExactly<ArgumentException>(() => repository.Complete(context, cursor + "x", []));
        Assert.ThrowsExactly<ArgumentException>(() => repository.Complete(context, "bad\n", []));
        Assert.ThrowsExactly<ArgumentException>(() => repository.RememberRead(context, FirstObservation with { FileId = context.FolderId }));
        Assert.AreEqual(checkpoint, repository.LastCheckpoint()); Assert.AreEqual(0, repository.Known(context).Count);
    }

    [TestMethod]
    public void VersionTenMigrationPreservesCapturesAndRollsBackOnAnExistingNameConflict()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CatalogGenerationCapture claim;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(); claim = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget)
                .Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
            fixture.Sql("DROP TABLE workspace_inventory_checkpoint; DROP TABLE workspace_document_observation; CREATE TABLE workspace_inventory_checkpoint(notes TEXT); INSERT INTO workspace_inventory_checkpoint VALUES('Keep evidence'); PRAGMA user_version=9");
        }
        Assert.ThrowsExactly<SqliteException>(() => new ProcessorStateDatabase(root.Path));
        using (var sql = Open(root.Path))
        {
            using var command = sql.CreateCommand(); command.CommandText = "PRAGMA user_version"; Assert.AreEqual(9L, command.ExecuteScalar());
            command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='workspace_document_observation'"; Assert.AreEqual(0L, command.ExecuteScalar());
            command.CommandText = "SELECT notes FROM workspace_inventory_checkpoint"; Assert.AreEqual("Keep evidence", command.ExecuteScalar());
            command.CommandText = "DROP TABLE workspace_inventory_checkpoint"; command.ExecuteNonQuery();
        }
        using var recovered = new ProcessorStateDatabase(root.Path); Assert.AreEqual(claim, new CatalogGenerationCaptureRepository(recovered, new(new Space())).Read(claim.Scope.GenerationId));
        using var connection = Open(root.Path); using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(10L, query.ExecuteScalar());
    }
}
