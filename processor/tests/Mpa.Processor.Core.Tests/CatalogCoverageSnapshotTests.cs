using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class CatalogCoverageSnapshotTests
{
    [TestMethod]
    public void FactsAndCoverageRetainOneObservationWhileImportsAndSelectionAdvance()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        work = fixture.Queue.Advance(work, "private-page-token", 2, 0, false);
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("old-file")]);
        using var old = CatalogFactsSnapshot.Open(fixture.Database);
        var before = old.ReadCoverageSources().Single(); Assert.AreEqual("scanning", before.State);
        fixture.Queue.CompleteFolder(fixture.Queue.Advance(work, null, 1, 0, false));
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("new-file")]);
        using (var complete = CatalogFactsSnapshot.Open(fixture.Database))
        {
            var coverage = complete.ReadCoverageSources().Single(); Assert.AreEqual("complete", coverage.State);
            Assert.AreEqual(3L, coverage.Checkpoint!.ObservedItems); Assert.AreNotEqual(before.Checkpoint!.Sha256, coverage.Checkpoint.Sha256);
            Assert.AreEqual(2, complete.ReadPage("").Assets.Count);
        }
        var replacement = fixture.Create(); Assert.AreNotEqual(job, replacement);
        using var latest = CatalogFactsSnapshot.Open(fixture.Database);
        Assert.AreEqual(replacement, latest.ReadCoverageSources().Single().Checkpoint!.JobId);
        Assert.AreEqual("pending", latest.ReadCoverageSources().Single().State);
        Assert.AreEqual(before, old.ReadCoverageSources().Single()); Assert.AreEqual(1, old.ReadPage("").Assets.Count);
        Assert.AreEqual(job, before.Checkpoint!.JobId);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(before).Contains("private-page-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RepairCompletionRemovesCurrentFailureWithoutRewritingHistoricalCounters()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        fixture.Queue.RecordFailure(work, "bad-file");
        fixture.Queue.CompleteFolder(fixture.Queue.Advance(work, null, 3, 1, false));
        using var failed = CatalogFactsSnapshot.Open(fixture.Database);
        Assert.AreEqual("partial", failed.ReadCoverageSources().Single().State);
        Assert.AreEqual(1L, failed.ReadCoverageSources().Single().Checkpoint!.FailedItems);
        Assert.AreEqual(1, fixture.Jobs.RetryFailed(job)); var repair = fixture.Queue.ClaimRepair()!;
        fixture.Queue.CompleteRepair(repair);
        using var recovered = CatalogFactsSnapshot.Open(fixture.Database);
        var coverage = recovered.ReadCoverageSources().Single(); Assert.AreEqual("complete", coverage.State);
        Assert.AreEqual(0L, coverage.Checkpoint!.FailedItems); Assert.AreEqual(4L, coverage.Checkpoint.ObservedItems);
        Assert.AreEqual("partial", failed.ReadCoverageSources().Single().State);
    }

    [TestMethod]
    public void IncompleteSearchCannotBecomeCompleteAfterSuccessfulPageRepair()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        fixture.Queue.RecordFailure(work, null); fixture.Queue.CompleteFolder(fixture.Queue.Advance(work, null, 2, 0, true));
        fixture.Jobs.RetryFailed(job); fixture.Queue.CompleteRepair(fixture.Queue.ClaimRepair()!);
        var coverage = fixture.Read(); Assert.AreEqual("partial", coverage.State);
        Assert.IsTrue(coverage.Checkpoint!.IncompleteSearch); Assert.AreEqual(0L, coverage.Checkpoint.FailedItems);
    }

    [TestMethod]
    public void VerificationAndFolderCompletionRequireTheirAcceptedReceipts()
    {
        using var fixture = new Fixture();
        fixture.Queue.RequestScan(new(Guid.NewGuid(), fixture.Source, 1, 1));
        Assert.AreEqual("pending", fixture.Read().State);
        var verification = fixture.Queue.ClaimVerification()!; Assert.AreEqual("scanning", fixture.Read().State);
        fixture.Queue.CompleteVerification(fixture.Queue.SaveVerifiedRoot(verification, "folder-A", "shared-drive"));
        Assert.AreEqual("scanning", fixture.Read().State);
        var folder = fixture.Queue.ClaimNext()!; Assert.AreEqual("shared-drive", folder.Folder.SharedDriveId);
        fixture.Queue.CompleteFolder(fixture.Queue.Advance(folder, null, 0, 0, false));
        Assert.AreEqual("complete", fixture.Read().State);
        fixture.Jobs.Pause(verification.Lease.JobId); Assert.AreEqual("complete", fixture.Read().State);
        fixture.Jobs.Pause(verification.Lease.JobId, cancelJob: true); Assert.AreEqual("complete", fixture.Read().State);
    }

    [TestMethod]
    public void PolicyAndAccountChangesNeverClaimAnOldScanCompletedTheNewScope()
    {
        using var fixture = new Fixture(); fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        fixture.Queue.CompleteFolder(fixture.Queue.Advance(work, null, 1, 0, false));
        using var old = CatalogFactsSnapshot.Open(fixture.Database);
        fixture.Sources.Update(fixture.Source, true, false, 1, fixture.Sources.Snapshot().Revision);
        Assert.AreEqual("pending", fixture.Read().State); Assert.AreEqual(1L, fixture.Read().Checkpoint!.SourceRevision);
        fixture.Accounts.Disconnect("account-A", 1); Assert.AreEqual("disconnected", fixture.Read().State);
        fixture.Sources.Update(fixture.Source, false, false, 2, fixture.Sources.Snapshot().Revision);
        Assert.AreEqual("disabled", fixture.Read().State); Assert.AreEqual("complete", old.ReadCoverageSources().Single().State);
    }

    [TestMethod]
    [DataRow("drive-authorization", "access-lost")]
    [DataRow("drive-unavailable", "offline")]
    [DataRow("drive-timeout", "offline")]
    [DataRow("invalid-source-metadata", "partial")]
    public void OperationalFailureRemainsDistinctAndRetainsCachedFacts(string code, string expected)
    {
        using var fixture = new Fixture(); fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("cached-file")]);
        fixture.Jobs.Fail(work.Lease, code, "Actionable test diagnostic", true);
        using var snapshot = CatalogFactsSnapshot.Open(fixture.Database);
        var coverage = snapshot.ReadCoverageSources().Single(); Assert.AreEqual(expected, coverage.State);
        Assert.AreEqual(1L, coverage.Checkpoint!.FailedItems); Assert.AreEqual(1, snapshot.ReadPage("").Assets.Count);
    }

    [TestMethod]
    [DataRow("UPDATE job SET context_hash='invalid'")]
    [DataRow("DELETE FROM item_stage")]
    [DataRow("UPDATE item_stage SET name='unexpected-stage'")]
    [DataRow("UPDATE item_stage SET state='ready',result_receipt='{}'")]
    [DataRow("UPDATE item_stage SET checkpoint=printf('%040000d',0)")]
    [DataRow("UPDATE job_item SET input_fingerprint='changed'")]
    [DataRow("UPDATE item_stage SET recipe_hash='changed'")]
    [DataRow("DELETE FROM job")]
    public void CorruptOrMissingJournalFailsInsteadOfPublishingEmptyOrComplete(string corruption)
    {
        using var fixture = new Fixture(); fixture.Create(); fixture.Sql(corruption);
        using var snapshot = CatalogFactsSnapshot.Open(fixture.Database);
        Assert.ThrowsExactly<InvalidDataException>(() => snapshot.ReadCoverageSources());
        Assert.AreEqual(0, snapshot.ReadPage("").Assets.Count);
    }

    [TestMethod]
    public void NoScanCancellationAndDisposalRetainExplicitSnapshotOwnership()
    {
        using var fixture = new Fixture(); using var snapshot = CatalogFactsSnapshot.Open(fixture.Database);
        var source = snapshot.ReadCoverageSources().Single(); Assert.AreEqual("pending", source.State); Assert.IsNull(source.Checkpoint);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => snapshot.ReadCoverageSources(canceled.Token));
        Assert.ThrowsExactly<InvalidOperationException>(fixture.Database.Dispose);
        Assert.AreEqual(source, snapshot.ReadCoverageSources().Single()); snapshot.Dispose(); snapshot.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => snapshot.ReadCoverageSources());
    }

    [TestMethod]
    public void MissingVerificationChildAndFailedObservationJournalsCannotClaimCompletion()
    {
        using (var fixture = new Fixture())
        {
            fixture.Queue.RequestScan(new(Guid.NewGuid(), fixture.Source, 1, 1));
            var verification = fixture.Queue.ClaimVerification()!;
            fixture.Queue.CompleteVerification(fixture.Queue.SaveVerifiedRoot(verification, "folder-A", null));
            fixture.Sql("DELETE FROM item_stage WHERE name='list-metadata'");
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Read());
        }
        using (var fixture = new Fixture())
        {
            fixture.Create(); var work = fixture.Queue.ClaimNext()!;
            fixture.Queue.CompleteFolder(fixture.Queue.Advance(work, null, 0, 1, false));
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Read());
        }
    }

    [TestMethod]
    public void PausedAndCanceledWorkRetainsOutstandingStepsAndNeverBecomesComplete()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        var paused = fixture.Jobs.Pause(job).Single();
        Assert.AreEqual("scanning", fixture.Read().State); Assert.AreEqual(1L, fixture.Read().Checkpoint!.PendingItems);
        fixture.Jobs.ReleaseStoppedAttempt(paused); fixture.Jobs.Resume(job);
        var resumed = fixture.Queue.ClaimNext()!; Assert.AreEqual(work.Lease.ItemId, resumed.Lease.ItemId);
        var canceled = fixture.Jobs.Pause(job, cancelJob: true).Single(); fixture.Jobs.ReleaseStoppedAttempt(canceled);
        Assert.AreEqual("scanning", fixture.Read().State); Assert.AreEqual(1L, fixture.Read().Checkpoint!.PendingItems);
    }

    [TestMethod]
    public void SourceLimitCanonicalOrderAndMissingAccountAreValidatedWithoutSilentlyTruncating()
    {
        using var fixture = new Fixture();
        for (var index = 1; index < 256; index++)
            fixture.Sources.Add(Guid.NewGuid(), "account-A", 1, "folder-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Synthetic", true, fixture.Sources.Snapshot().Revision);
        using var captured = CatalogFactsSnapshot.Open(fixture.Database);
        var sources = captured.ReadCoverageSources(); Assert.AreEqual(256, sources.Count);
        CollectionAssert.AreEqual(sources.Select(source => source.Id).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray(), sources.Select(source => source.Id).ToArray());
        fixture.Sql("INSERT INTO source_binding VALUES('ffffffff-ffff-4fff-8fff-ffffffffffff','account-A','extra-folder','Extra',1,1,1)");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Read());
        Assert.AreEqual(256, captured.ReadCoverageSources().Count);
        fixture.Sql("DELETE FROM source_binding WHERE folder_id='extra-folder'; DELETE FROM drive_account");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Read());
    }

    private static DiscoveredDriveAsset Fact(string id) => new(id, 1, "Photo.jpg", "image/jpeg", 40, 30, 100, null, null, 0);
    private sealed class Fixture : IDisposable
    {
        private readonly WorkerProcessTests.WorkerTestDirectory _directory = new();
        public ProcessorStateDatabase Database { get; }
        public DriveAccountRepository Accounts { get; }
        public LibrarySourceRepository Sources { get; }
        public SourceDiscoveryRepository Queue { get; }
        public DurableJobRepository Jobs { get; }
        public DiscoveredCatalogRepository Catalog { get; }
        public Guid Source { get; } = Guid.NewGuid();
        public Fixture()
        {
            Database = new(_directory.Path); Accounts = new(Database); Sources = new(Database); Queue = new(Database);
            Jobs = new(Database); Catalog = new(Database);
            var pending = Accounts.Begin(Guid.NewGuid()); Accounts.Promote(pending.Id, "account-A", "Synthetic", null);
            Sources.Add(Source, "account-A", 1, "folder-A", "Synthetic source", true, Sources.Snapshot().Revision);
        }
        public Guid Create() { var job = Guid.NewGuid(); Queue.Create(job, Source, 1, 1, "folder-A", null); return job; }
        public CatalogSourceCoverage Read() { using var snapshot = CatalogFactsSnapshot.Open(Database); return snapshot.ReadCoverageSources().Single(); }
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory.Path, "processor.db"), Pooling = false, ForeignKeys = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() { Database.Dispose(); _directory.Dispose(); }
    }
}
