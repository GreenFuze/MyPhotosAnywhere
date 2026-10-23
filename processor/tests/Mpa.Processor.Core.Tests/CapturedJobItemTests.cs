using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class CapturedJobItemTests
{
    private static readonly string Hash = new('a', 64);

    [TestMethod]
    public void FolderInputContextSurvivesPauseRestartAndRetryWithoutReplacingTheJobDestination()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); var job = Guid.NewGuid(); var item = Guid.NewGuid();
        using (var database = new ProcessorStateDatabase(root.Path))
        {
            var jobs = new DurableJobRepository(database);
            jobs.CreateWithInitialItem(job, "source-discovery", "{\"destination\":\"captured-destination\"}"u8,
                Item(item, "{\"folder\":\"captured-folder\",\"depth\":2}"));
            var first = jobs.ClaimNext("source-discovery")!;
            Assert.AreEqual("{\"depth\":2,\"folder\":\"captured-folder\"}", first.CapturedItemContext);
            jobs.SaveCheckpoint(first, "{\"pageToken\":\"captured-next-page\"}"u8);
            var stopping = jobs.Pause(job).Single(); Assert.AreEqual(first.CapturedItemContext, stopping.CapturedItemContext);
            jobs.ReleaseStoppedAttempt(stopping); jobs.Resume(job);
        }
        using var reopened = new ProcessorStateDatabase(root.Path); var durable = new DurableJobRepository(reopened);
        var retry = durable.ClaimNext("source-discovery")!;
        Assert.AreEqual(2, retry.Attempt); Assert.AreEqual("{\"depth\":2,\"folder\":\"captured-folder\"}", retry.CapturedItemContext);
        Assert.AreEqual("{\"destination\":\"captured-destination\"}", retry.CapturedContext);
        StringAssert.Contains(retry.Checkpoint!, "captured-next-page");
        durable.Fail(retry, "network-unavailable", "Synthetic source read failed.", true); Assert.AreEqual(1, durable.RetryFailed(job));
        Assert.AreEqual(retry.CapturedItemContext, durable.ClaimNext("source-discovery")!.CapturedItemContext);
    }

    [TestMethod]
    public void AtomicJobAdmissionRejectsInvalidItemsAndRollsBackFailedInsertion()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path); var jobs = new DurableJobRepository(database);
        var rejected = Guid.NewGuid(); var invalid = Item(Guid.NewGuid(), "[]");
        Assert.ThrowsExactly<ArgumentException>(() => jobs.CreateWithInitialItem(rejected, "source-discovery", "{}"u8, invalid));
        Assert.ThrowsExactly<KeyNotFoundException>(() => jobs.Summary(rejected));
        var accepted = Guid.NewGuid(); jobs.CreateWithInitialItem(accepted, "source-discovery", "{}"u8, Item(Guid.NewGuid(), "{}"));
        Assert.ThrowsExactly<SqliteException>(() => jobs.CreateWithInitialItem(accepted, "source-discovery", "{\"changed\":true}"u8, Item(Guid.NewGuid(), "{}")));
        Assert.AreEqual(1, jobs.Summary(accepted).Queued); Assert.AreEqual("{}", jobs.ClaimNext()!.CapturedContext);
    }

    [TestMethod]
    public void AReadOnlyScannerCannotClaimAnUploadJob()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path); var jobs = new DurableJobRepository(database);
        var upload = Guid.Parse("00000000-0000-4000-8000-000000000001"); var scan = Guid.Parse("00000000-0000-4000-8000-000000000002");
        jobs.CreateWithInitialItem(upload, "takeout-import", "{}"u8, Item(Guid.NewGuid(), "{\"original\":true}"));
        jobs.CreateWithInitialItem(scan, "source-discovery", "{}"u8, Item(Guid.NewGuid(), "{\"folder\":\"root-folder\"}"));
        Assert.AreEqual(scan, jobs.ClaimNext("source-discovery")!.JobId); Assert.IsNull(jobs.ClaimNext("source-discovery"));
        Assert.AreEqual(upload, jobs.ClaimNext()!.JobId);
    }

    [TestMethod]
    public void SchemaFourUpgradeAddsEmptyItemContextWithoutChangingExistingJobsOrCheckpoints()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); var job = Guid.NewGuid(); StageLease lease;
        using (var database = new ProcessorStateDatabase(root.Path))
        {
            var jobs = new DurableJobRepository(database); jobs.CreateWithInitialItem(job, "source-discovery", "{\"destination\":\"old-destination\"}"u8, Item(Guid.NewGuid(), "{}"));
            lease = jobs.ClaimNext()!; jobs.SaveCheckpoint(lease, "{\"offset\":42}"u8);
        }
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root.Path, "processor.db"), Pooling = false }.ToString()))
        {
            old.Open(); using var command = old.CreateCommand(); command.CommandText = "DROP INDEX workspace_publication_generation; DROP TABLE catalog_generation_capture; DROP TABLE signing_intent; DROP TABLE signing_actor; DROP TABLE workspace_publication; DROP TABLE current_source_scan; ALTER TABLE job_item DROP COLUMN captured_context; PRAGMA user_version=4;"; command.ExecuteNonQuery();
        }
        using var upgraded = new ProcessorStateDatabase(root.Path); var durable = new DurableJobRepository(upgraded);
        var stopping = durable.Pause(job).Single(); Assert.AreEqual(lease.LeaseId, stopping.LeaseId); Assert.AreEqual("{}", stopping.CapturedItemContext);
        Assert.AreEqual("{\"offset\":42}", stopping.Checkpoint); Assert.AreEqual("{\"destination\":\"old-destination\"}", stopping.CapturedContext);
        durable.ReleaseStoppedAttempt(stopping); durable.Resume(job); Assert.AreEqual("{}", durable.ClaimNext()!.CapturedItemContext);
    }
    private static JobItemSpec Item(Guid id, string context) => new(id, Hash, [new("metadata", Hash, [])], context);

    [TestMethod]
    public void IdempotentJobAdmissionPreservesReceiptsAndRejectsChangedOrNonAtomicExistingJobs()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        var jobs = new DurableJobRepository(database); var job = Guid.NewGuid(); var item = Item(Guid.NewGuid(), "{}");
        Assert.IsTrue(jobs.EnsureJobWithInitialItem(job, "source-discovery", "{}"u8, item));
        Assert.IsFalse(jobs.EnsureJobWithInitialItem(job, "source-discovery", "{}"u8, item));
        jobs.Complete(jobs.ClaimNext()!, "{\"verified\":true}"u8);
        Assert.IsFalse(jobs.EnsureJobWithInitialItem(job, "source-discovery", "{}"u8, item));
        Assert.AreEqual(1, jobs.Summary(job).Ready); Assert.AreEqual(1, jobs.PendingReceiptCount());
        Assert.ThrowsExactly<ConflictingJobItemException>(() => jobs.EnsureJobWithInitialItem(job, "source-discovery", "{\"changed\":true}"u8, item));
        Assert.ThrowsExactly<ConflictingJobItemException>(() => jobs.EnsureJobWithInitialItem(job, "another-kind", "{}"u8, item));
        Assert.ThrowsExactly<ConflictingJobItemException>(() => jobs.EnsureJobWithInitialItem(job, "source-discovery", "{}"u8, item with { CapturedContext = "{\"changed\":true}" }));
        Assert.IsNull(jobs.ClaimNext());
        var nonAtomic = Guid.NewGuid(); jobs.Create(nonAtomic, "source-discovery", "{}"u8);
        Assert.ThrowsExactly<InvalidDataException>(() => jobs.EnsureJobWithInitialItem(nonAtomic, "source-discovery", "{}"u8, Item(Guid.NewGuid(), "{}")));
        Assert.AreEqual(0, jobs.Summary(nonAtomic).Queued);
    }

    [TestMethod]
    public void ReplayedFolderAdmissionNeverRequeuesReadyWorkAndRejectsReusedIdentitiesWithDifferentRecipes()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        var jobs = new DurableJobRepository(database); var job = Guid.NewGuid(); var item = Item(Guid.NewGuid(), "{\"folder\":\"captured\"}");
        jobs.CreateWithInitialItem(job, "source-discovery", "{}"u8, item);
        Assert.IsFalse(jobs.EnsureItem(job, item));
        jobs.Complete(jobs.ClaimNext()!, "{\"verifiedMetadata\":true}"u8);
        Assert.IsFalse(jobs.EnsureItem(job, item)); Assert.IsNull(jobs.ClaimNext()); Assert.AreEqual(1, jobs.Summary(job).Ready);
        Assert.ThrowsExactly<ConflictingJobItemException>(() => jobs.EnsureItem(job, item with { CapturedContext = "{\"folder\":\"other\"}" }));
        Assert.ThrowsExactly<ConflictingJobItemException>(() => jobs.EnsureItem(job, item with { Stages = [new("metadata", new string('b', 64), [])] }));
        Assert.ThrowsExactly<ConflictingJobItemException>(() => jobs.EnsureItem(job, item with { InputFingerprint = new string('b', 64) }));
        Assert.AreEqual(1, jobs.PendingReceiptCount()); Assert.AreEqual(1, jobs.Summary(job).Ready);
        Assert.IsTrue(jobs.EnsureItem(job, Item(Guid.NewGuid(), "{\"folder\":\"next\"}"))); Assert.AreEqual(1, jobs.Summary(job).Queued);
    }
}
