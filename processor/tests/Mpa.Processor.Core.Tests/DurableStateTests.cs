using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class DurableStateTests
{
    private const string Hash = "c477c29974484c04a2940ad6ed99ac6f7fd37318f24480d519c814da574c6995";

    [TestMethod]
    public void FailedItemDoesNotStopOthersAndRetryNeverRequeuesSuccessfulUploads()
    {
        using var directory = new TestStateDirectory();
        var job = Guid.NewGuid();
        using (var state = new ProcessorStateDatabase(directory.Path))
        {
            Console.WriteLine("Native SQLite runtime: " + state.SqliteVersion);
            var jobs = new DurableJobRepository(state);
            jobs.Create(job, "takeout-import", "{\"destination\":\"captured-folder-A\"}"u8);
            jobs.AddItem(job, Item(Guid.Parse("00000000-0000-4000-8000-000000000001"), new JobStageSpec("upload-original", Hash, [])));
            jobs.AddItem(job, Item(Guid.Parse("00000000-0000-4000-8000-000000000002"), new JobStageSpec("upload-original", Hash, [])));
            var failing = jobs.ClaimNext() ?? throw new InvalidOperationException("Expected initial work.");
            jobs.SaveCheckpoint(failing, "{\"verifiedOffset\":\"262144\",\"vaultSessionId\":\"synthetic-ref\"}"u8);
            jobs.Fail(failing, "network-unavailable", "Synthetic connection interruption", retryable: true);
            var other = jobs.ClaimNext() ?? throw new InvalidOperationException("Unrelated item was stopped.");
            Assert.AreNotEqual(failing.ItemId, other.ItemId);
            jobs.Complete(other, "{\"verified\":true,\"fileId\":\"synthetic-file\"}"u8);
            Assert.AreEqual(1, jobs.Summary(job).Ready);
            Assert.AreEqual(1, jobs.Summary(job).Failed);
            Assert.AreEqual(1, jobs.PendingReceiptCount());
            Assert.AreEqual(1, jobs.RetryFailed(job));
            var retry = jobs.ClaimNext() ?? throw new InvalidOperationException("Failed item was not resumed.");
            Assert.AreEqual(failing.ItemId, retry.ItemId);
            Assert.AreEqual(2, retry.Attempt);
            Assert.AreNotEqual(failing.LeaseId, retry.LeaseId);
            Assert.IsTrue(retry.Checkpoint!.Contains("262144", StringComparison.Ordinal));
            Assert.AreEqual("{\"destination\":\"captured-folder-A\"}", retry.CapturedContext);
            Assert.ThrowsExactly<StaleStageLeaseException>(() => jobs.Complete(failing, "{\"verified\":true}"u8));
            Assert.AreEqual(1, jobs.PendingReceiptCount());
            jobs.Complete(retry, "{\"verified\":true,\"fileId\":\"other-synthetic-file\"}"u8);
            Assert.AreEqual(2, jobs.PendingReceiptCount());
            Assert.AreEqual(0, jobs.RetryFailed(job));
            Assert.IsNull(jobs.ClaimNext());
        }
        using var reopened = new ProcessorStateDatabase(directory.Path);
        var durable = new DurableJobRepository(reopened);
        Assert.AreEqual(2, durable.Summary(job).Ready);
        Assert.AreEqual(2, durable.PendingReceiptCount());
        Assert.IsNull(durable.ClaimNext());
    }

    [TestMethod]
    public void DependenciesAndPerStageFailuresPreserveAlreadyVerifiedOriginals()
    {
        using var directory = new TestStateDirectory();
        using var state = new ProcessorStateDatabase(directory.Path);
        var jobs = new DurableJobRepository(state);
        var job = Guid.NewGuid();
        jobs.Create(job, "takeout-import", "{\"destination\":\"A\"}"u8);
        jobs.AddItem(job, Item(Guid.NewGuid(), new JobStageSpec("upload-original", Hash, []), new JobStageSpec("metadata", Hash, ["upload-original"]), new JobStageSpec("thumbnail", Hash, ["upload-original"])));
        var original = jobs.ClaimNext()!;
        Assert.AreEqual("upload-original", original.Stage);
        jobs.Complete(original, "{\"verified\":true}"u8);
        var metadata = jobs.ClaimNext()!;
        Assert.AreEqual("metadata", metadata.Stage);
        jobs.Fail(metadata, "xmp-invalid", "Synthetic malformed metadata; original already retained", retryable: true);
        var preview = jobs.ClaimNext()!;
        Assert.AreEqual("thumbnail", preview.Stage);
        jobs.Complete(preview, "{\"verified\":true}"u8);
        Assert.AreEqual(1, jobs.RetryFailed(job));
        Assert.AreEqual("metadata", jobs.ClaimNext()!.Stage);
        Assert.AreEqual(2, jobs.Summary(job).Ready);
    }

    [TestMethod]
    public void CrashedRunningAttemptRemainsFencedUntilAWorkerExitRecoveryOwnerHandlesIt()
    {
        using var directory = new TestStateDirectory();
        var job = Guid.NewGuid();
        using (var state = new ProcessorStateDatabase(directory.Path))
        {
            var jobs = new DurableJobRepository(state);
            jobs.Create(job, "source-scan", "{}"u8);
            jobs.AddItem(job, Item(Guid.NewGuid(), new JobStageSpec("metadata", Hash, [])));
            var lease = jobs.ClaimNext()!;
            jobs.SaveCheckpoint(lease, "{\"position\":\"100\"}"u8);
        }
        using var reopened = new ProcessorStateDatabase(directory.Path);
        var durable = new DurableJobRepository(reopened);
        Assert.AreEqual(1, durable.Summary(job).Running);
        Assert.IsNull(durable.ClaimNext()); // Never assume an orphaned native reader has already exited.
    }

    [TestMethod]
    public void SecondOwnerUnknownDirectoriesAndFutureSchemasFailWithoutClearingData()
    {
        using var directory = new TestStateDirectory();
        using (var first = new ProcessorStateDatabase(directory.Path))
            Assert.ThrowsExactly<IOException>(() => { using var second = new ProcessorStateDatabase(directory.Path); });
        using (var external = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(directory.Path, "processor.db"), Pooling = false }.ToString()))
        {
            external.Open();
            using var command = external.CreateCommand();
            command.CommandText = "PRAGMA user_version=2147483647";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "processor.db"));
        Assert.ThrowsExactly<InvalidDataException>(() => { using var future = new ProcessorStateDatabase(directory.Path); });
        CollectionAssert.AreEqual(before, File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "processor.db")));
        using var unknown = new TestStateDirectory();
        var original = System.IO.Path.Combine(unknown.Path, "original.jpg");
        File.WriteAllBytes(original, [1, 2, 3]);
        Assert.ThrowsExactly<InvalidDataException>(() => { using var wrong = new ProcessorStateDatabase(unknown.Path); });
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(original));
    }

    [TestMethod]
    public void CyclicStageGraphsDoNotLeaveHalfInsertedItems()
    {
        using var directory = new TestStateDirectory();
        using var state = new ProcessorStateDatabase(directory.Path);
        var jobs = new DurableJobRepository(state);
        var job = Guid.NewGuid(); var item = Guid.NewGuid();
        jobs.Create(job, "takeout-import", "{}"u8);
        Assert.ThrowsExactly<ArgumentException>(() => jobs.AddItem(job, Item(item, new JobStageSpec("a", Hash, ["b"]), new JobStageSpec("b", Hash, ["a"]))));
        jobs.AddItem(job, Item(item, new JobStageSpec("a", Hash, [])));
        Assert.AreEqual(1, jobs.Summary(job).Queued);
    }

    private static JobItemSpec Item(Guid id, params JobStageSpec[] stages) => new(id, Hash, stages);

    [TestMethod]
    public void BlockedStagesWaitForEveryDependencyAcrossReopenAndFailedOnlyRetry()
    {
        using var directory = new TestStateDirectory();
        var job = Guid.NewGuid(); var item = Guid.NewGuid();
        using (var state = new ProcessorStateDatabase(directory.Path))
        {
            var jobs = new DurableJobRepository(state);
            jobs.Create(job, "takeout-import", "{}"u8);
            jobs.AddItem(job, Item(item, new JobStageSpec("a", Hash, []), new JobStageSpec("b", Hash, []),
                new JobStageSpec("combine", Hash, ["a", "b"]), new JobStageSpec("publish", Hash, ["combine"])));
            jobs.Complete(jobs.ClaimNext()!, "{}"u8);
            var failed = jobs.ClaimNext()!;
            Assert.AreEqual("b", failed.Stage);
            jobs.Fail(failed, "temporary-network", "Synthetic interrupted input", retryable: true);
            Assert.IsNull(jobs.ClaimNext());
            Assert.AreEqual(2, jobs.Summary(job).Queued);
        }
        using var reopened = new ProcessorStateDatabase(directory.Path);
        var durable = new DurableJobRepository(reopened);
        Assert.IsNull(durable.ClaimNext());
        Assert.AreEqual(1, durable.RetryFailed(job));
        var retry = durable.ClaimNext()!;
        Assert.AreEqual("b", retry.Stage);
        Assert.AreEqual(2, retry.Attempt);
        Assert.IsNull(durable.ClaimNext());
        durable.Complete(retry, "{}"u8);
        var combined = durable.ClaimNext()!;
        Assert.AreEqual("combine", combined.Stage);
        Assert.IsNull(durable.ClaimNext());
        durable.Complete(combined, "{}"u8);
        var publish = durable.ClaimNext()!;
        Assert.AreEqual("publish", publish.Stage);
        durable.Complete(publish, "{}"u8);
        Assert.AreEqual(4, durable.Summary(job).Ready);
        Assert.AreEqual(0, durable.Summary(job).Queued);
        Assert.IsNull(durable.ClaimNext());
    }

    [TestMethod]
    public void CancellationIncludesDependencyBlockedStagesWithoutUnlockingThem()
    {
        using var directory = new TestStateDirectory();
        using var state = new ProcessorStateDatabase(directory.Path);
        var jobs = new DurableJobRepository(state);
        var job = Guid.NewGuid();
        jobs.Create(job, "takeout-import", "{}"u8);
        jobs.AddItem(job, Item(Guid.NewGuid(), new JobStageSpec("original", Hash, []), new JobStageSpec("preview", Hash, ["original"])));
        var running = jobs.ClaimNext()!;
        jobs.Pause(job, cancelJob: true);
        jobs.ReleaseStoppedAttempt(running);
        Assert.AreEqual(2, jobs.Summary(job).Canceled);
        Assert.AreEqual(0, jobs.Summary(job).Queued);
        Assert.IsNull(jobs.ClaimNext());
    }

    [TestMethod]
    public void PauseFencesLateResultsAndResumeWaitsForActualWorkerExitAcknowledgement()
    {
        using var directory = new TestStateDirectory();
        using var state = new ProcessorStateDatabase(directory.Path);
        var jobs = new DurableJobRepository(state);
        var job = Guid.NewGuid();
        jobs.Create(job, "device-backup", "{\"destination\":\"captured-A\"}"u8);
        jobs.AddItem(job, Item(Guid.NewGuid(), new JobStageSpec("upload-original", Hash, [])));
        var running = jobs.ClaimNext()!;
        jobs.SaveCheckpoint(running, "{\"verifiedOffset\":\"262144\"}"u8);
        Assert.AreEqual(running.LeaseId, jobs.Pause(job).Single().LeaseId);
        Assert.ThrowsExactly<StaleStageLeaseException>(() => jobs.Complete(running, "{\"verified\":true}"u8));
        Assert.ThrowsExactly<InvalidOperationException>(() => jobs.Resume(job));
        Assert.IsNull(jobs.ClaimNext());
        Assert.AreEqual(0, jobs.PendingReceiptCount());
        jobs.ReleaseStoppedAttempt(running); // Test simulates the process owner's completed join.
        Assert.IsNull(jobs.ClaimNext());
        jobs.Resume(job);
        var resumed = jobs.ClaimNext()!;
        Assert.AreEqual(2, resumed.Attempt);
        Assert.AreNotEqual(running.LeaseId, resumed.LeaseId);
        Assert.AreEqual(running.CapturedContext, resumed.CapturedContext);
        Assert.IsTrue(resumed.Checkpoint!.Contains("262144", StringComparison.Ordinal));
        jobs.Complete(resumed, "{\"verified\":true}"u8);
        var receipt = jobs.ReadPendingReceipts(1).Single();
        Assert.AreEqual(resumed.LeaseId, receipt.Id);
        jobs.MarkReceiptPublished(receipt.Id);
        jobs.MarkReceiptPublished(receipt.Id); // Lost publication acknowledgement is harmless.
        Assert.AreEqual(0, jobs.PendingReceiptCount());
    }

    [TestMethod]
    public void CancelPreservesReadyReceiptsAndCannotRestartAnUnapprovedNewAttempt()
    {
        using var directory = new TestStateDirectory();
        using var state = new ProcessorStateDatabase(directory.Path);
        var jobs = new DurableJobRepository(state);
        var job = Guid.NewGuid();
        jobs.Create(job, "device-backup", "{}"u8);
        jobs.AddItem(job, Item(Guid.NewGuid(), new JobStageSpec("upload-original", Hash, [])));
        jobs.AddItem(job, Item(Guid.NewGuid(), new JobStageSpec("upload-original", Hash, [])));
        jobs.Complete(jobs.ClaimNext()!, "{\"verified\":true}"u8);
        var running = jobs.ClaimNext()!;
        jobs.Pause(job, cancelJob: true);
        Assert.ThrowsExactly<StaleStageLeaseException>(() => jobs.Complete(running, "{\"verified\":true}"u8));
        jobs.ReleaseStoppedAttempt(running);
        Assert.ThrowsExactly<InvalidOperationException>(() => jobs.Resume(job));
        Assert.AreEqual(1, jobs.Summary(job).Ready);
        Assert.AreEqual(1, jobs.Summary(job).Canceled);
        Assert.AreEqual(1, jobs.PendingReceiptCount());
        Assert.AreEqual(0, jobs.RetryFailed(job));
        Assert.IsNull(jobs.ClaimNext());
    }

    private sealed class TestStateDirectory : IDisposable
    {
        private readonly string _temporaryRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        public string Path { get; }
        public TestStateDirectory()
        {
            Path = System.IO.Path.Combine(_temporaryRoot, "MPA-Core-Test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (System.IO.Path.GetDirectoryName(resolved) != _temporaryRoot || !System.IO.Path.GetFileName(resolved).StartsWith("MPA-Core-Test-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(System.IO.Path.GetFileName(resolved)[14..], "N", out _))
                throw new InvalidOperationException("Test cleanup escaped its owned temporary directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
