using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class SourceDiscoveryRepositoryTests
{
    private static readonly string[] PreservedScanTables = ["processor_meta", "drive_account", "pending_drive_connection", "credential_cleanup",
        "library_configuration", "source_binding", "configuration_outbox", "job", "job_item", "item_stage", "stage_dependency", "state_outbox",
        "discovered_asset", "source_asset", "discovery_revision"];
    private static readonly string[] ScanCorruptions = ["UPDATE job SET context_hash='broken'",
        "DELETE FROM item_stage", "PRAGMA foreign_keys=OFF; DELETE FROM job"];

    [TestMethod]
    public void ExplicitManualScanRemainsCurrentWhenAutomaticSchedulingReplays()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); using var fixture = new Fixture(directory.Path);
        var automatic = fixture.Queue.EnsureScheduled(fixture.Source, 1, 1); var before = fixture.Jobs.Summary(automatic.JobId);
        var manual = fixture.Create(); var selected = fixture.Queue.EnsureScheduled(fixture.Source, 1, 1);
        Assert.AreEqual(manual, selected.JobId); Assert.IsFalse(selected.Created);
        Assert.AreEqual(before, fixture.Jobs.Summary(automatic.JobId));
        Assert.AreEqual(2L, ScanScalar(directory.Path, "SELECT count(*) FROM job"));
        Assert.AreEqual(manual.ToString("D"), ScanScalar(directory.Path, "SELECT job_id FROM current_source_scan"));
    }

    [TestMethod]
    public void CurrentScanUsesAtomicAdmissionOrderAndSurvivesRestartWithoutUuidOrdering()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); Guid source;
        var earlier = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"); var later = Guid.Parse("00000000-0000-4000-8000-000000000001");
        using (var fixture = new Fixture(directory.Path))
        {
            source = fixture.Source; fixture.Queue.Create(earlier, source, 1, 1, "folder-A", null);
            fixture.Queue.Create(later, source, 1, 1, "folder-A", null);
            Assert.AreEqual(later, fixture.Queue.EnsureScheduled(source, 1, 1).JobId);
            Assert.AreEqual(1, fixture.Jobs.Summary(earlier).Queued);
        }
        using var database = new ProcessorStateDatabase(directory.Path);
        Assert.AreEqual(later, new SourceDiscoveryRepository(database).EnsureScheduled(source, 1, 1).JobId);
        Assert.AreEqual(2L, ScanScalar(directory.Path, "SELECT count(*) FROM job"));
    }

    [TestMethod]
    public void LostHttpReplyReplayReturnsItsOriginalJobWithoutRewindingANewerExplicitScan()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); using var fixture = new Fixture(directory.Path);
        var first = new SourceScanRequest(Guid.NewGuid(), fixture.Source, 1, 1); var second = first with { RequestId = Guid.NewGuid() };
        Assert.IsTrue(fixture.Queue.RequestScan(first).Created); Assert.IsTrue(fixture.Queue.RequestScan(second).Created);
        var replay = fixture.Queue.RequestScan(first); Assert.IsFalse(replay.Created); Assert.AreEqual(first.RequestId, replay.JobId);
        var current = fixture.Queue.EnsureScheduled(fixture.Source, 1, 1); Assert.AreEqual(second.RequestId, current.JobId); Assert.IsFalse(current.Created);
        Assert.AreEqual(2L, ScanScalar(directory.Path, "SELECT count(*) FROM job"));
    }

    [TestMethod]
    public void AnExistingAutomaticJobWithoutItsAtomicSelectionFailsInsteadOfResettingSavedWork()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); using var fixture = new Fixture(directory.Path);
        var initial = fixture.Queue.EnsureScheduled(fixture.Source, 1, 1); var before = fixture.Jobs.Summary(initial.JobId);
        ScanSql(directory.Path, "DELETE FROM current_source_scan");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.EnsureScheduled(fixture.Source, 1, 1));
        Assert.AreEqual(before, fixture.Jobs.Summary(initial.JobId)); Assert.AreEqual(1L, ScanScalar(directory.Path, "SELECT count(*) FROM job"));
        Assert.AreEqual(0L, ScanScalar(directory.Path, "SELECT count(*) FROM current_source_scan"));
    }

    [TestMethod]
    public void FailedScanSelectionRollsBackTheJobAndInitialItemAndPreservesPriorSelection()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); using var fixture = new Fixture(directory.Path);
        ScanSql(directory.Path, "CREATE TRIGGER reject_scan BEFORE INSERT ON current_source_scan BEGIN SELECT RAISE(ABORT,'Synthetic interrupted admission'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Queue.EnsureScheduled(fixture.Source, 1, 1));
        Assert.AreEqual(0L, ScanScalar(directory.Path, "SELECT count(*) FROM job"));
        Assert.AreEqual(0L, ScanScalar(directory.Path, "SELECT count(*) FROM job_item"));
        Assert.AreEqual(0L, ScanScalar(directory.Path, "SELECT count(*) FROM item_stage"));
        ScanSql(directory.Path, "DROP TRIGGER reject_scan"); var first = fixture.Create(); var before = CaptureScanRows(directory.Path);
        ScanSql(directory.Path, "CREATE TRIGGER reject_scan BEFORE INSERT ON current_source_scan BEGIN SELECT RAISE(ABORT,'Synthetic interrupted replacement'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Create());
        Assert.AreEqual(first.ToString("D"), ScanScalar(directory.Path, "SELECT job_id FROM current_source_scan"));
        CollectionAssert.AreEqual(before, CaptureScanRows(directory.Path));
    }

    [TestMethod]
    public void CorruptCurrentScanCannotBeDisguisedAsAnEmptySourceOrReplacedAutomatically()
    {
        foreach (var corruption in ScanCorruptions)
        {
            using var directory = new WorkerProcessTests.WorkerTestDirectory(); using var fixture = new Fixture(directory.Path);
            var job = fixture.Create(); ScanSql(directory.Path, corruption);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.EnsureScheduled(fixture.Source, 1, 1));
            Assert.AreEqual(job.ToString("D"), ScanScalar(directory.Path, "SELECT job_id FROM current_source_scan"));
        }
    }

    [TestMethod]
    public void SchemaFiveUpgradePreservesEveryExistingRowAndRequestsAFreshScanWithoutGuessingRecency()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); Guid source; Guid oldJob; StageLease oldLease; Guid installation;
        var original = Path.Combine(directory.Path, "retained-source-original.bin"); var originalBytes = "synthetic original, never rewritten"u8.ToArray();
        using (var fixture = new Fixture(directory.Path))
        {
            source = fixture.Source; installation = fixture.Database.InstallationId; oldJob = fixture.Create();
            var work = fixture.Queue.ClaimNext()!; oldLease = fixture.Queue.Advance(work, "next-page", 3, 0, false).Lease;
            fixture.Jobs.Create(Guid.NewGuid(), "takeout-import", "{\"caption\":\"Reviewed name\",\"destination\":\"captured-folder\"}"u8);
            File.WriteAllBytes(original, originalBytes);
        }
        ScanSql(directory.Path, "DROP TABLE catalog_generation_verification; DROP TABLE catalog_generation_publication; DROP TABLE workspace_inventory_checkpoint; DROP TABLE workspace_document_observation; DROP INDEX workspace_publication_generation; DROP TABLE catalog_generation_capture; DROP TABLE signing_intent; DROP TABLE signing_actor; DROP TABLE workspace_publication; DROP TABLE current_source_scan; PRAGMA user_version=5"); var before = CaptureScanRows(directory.Path);
        using var upgraded = new ProcessorStateDatabase(directory.Path);
        Assert.AreEqual(12L, ScanScalar(directory.Path, "PRAGMA user_version")); Assert.AreEqual(installation, upgraded.InstallationId);
        CollectionAssert.AreEqual(before, CaptureScanRows(directory.Path)); Assert.AreEqual(0L, ScanScalar(directory.Path, "SELECT count(*) FROM current_source_scan"));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(original));
        var fresh = new SourceDiscoveryRepository(upgraded).EnsureScheduled(source, 1, 1);
        Assert.IsTrue(fresh.Created); Assert.AreNotEqual(oldJob, fresh.JobId);
        var jobs = new DurableJobRepository(upgraded); var retained = jobs.Pause(oldJob).Single();
        Assert.AreEqual(oldLease.LeaseId, retained.LeaseId); Assert.AreEqual(oldLease.Checkpoint, retained.Checkpoint);
        Assert.AreEqual(oldLease.CapturedContext, retained.CapturedContext); Assert.AreEqual(oldLease.CapturedItemContext, retained.CapturedItemContext);
        Assert.AreEqual(fresh.JobId.ToString("D"), ScanScalar(directory.Path, "SELECT job_id FROM current_source_scan"));
    }

    private static SqliteConnection ScanConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "processor.db"), Pooling = false }.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static void ScanSql(string path, string sql)
    { using var connection = ScanConnection(path); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? ScanScalar(string path, string sql)
    { using var connection = ScanConnection(path); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static string[] CaptureScanRows(string path)
    {
        using var connection = ScanConnection(path); var tables = new List<string>();
        foreach (var table in PreservedScanTables)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM " + table;
            using var reader = command.ExecuteReader(); var rows = new List<string>();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)).ToArray();
                rows.Add(JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal); tables.Add(table + ":" + string.Join('\n', rows));
        }
        return tables.ToArray();
    }
}
