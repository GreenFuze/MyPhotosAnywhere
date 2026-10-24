using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core.Tests;

public sealed partial class SourceDiscoveryRepositoryTests
{
    [TestMethod]
    public void ScanActivityIsBoundedAndCannotExposeOrRetryAnotherJobKind()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 3; index++) fixture.Queue.RequestScan(new(Guid.NewGuid(), fixture.Source, 1, 1));
        var unrelated = Guid.NewGuid(); fixture.Jobs.Create(unrelated, "upload", "{}"u8);
        var first = fixture.Queue.ReadActivity(limit: 2); Assert.AreEqual(2, first.Jobs.Count); Assert.IsNotNull(first.NextCursor);
        var next = fixture.Queue.ReadActivity(first.NextCursor, 2); Assert.AreEqual(1, next.Jobs.Count); Assert.IsNull(next.NextCursor);
        Assert.AreEqual(3, first.Jobs.Concat(next.Jobs).Select(job => job.JobId).Distinct().Count());
        Assert.IsTrue(first.Jobs.All(job => job.Current && job.Summary.Queued == 1));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.RetryFailed(new(unrelated, fixture.Source, 1, 1)));
    }

    [TestMethod]
    public void ExplicitRescanReplayAndFailedOnlyRetryPreserveCompletedVerificationAndCheckpoint()
    {
        using var fixture = new Fixture(); var command = new SourceScanRequest(Guid.NewGuid(), fixture.Source, 1, 1);
        Assert.IsTrue(fixture.Queue.RequestScan(command).Created); Assert.IsFalse(fixture.Queue.RequestScan(command).Created);
        var verified = fixture.Queue.SaveVerifiedRoot(fixture.Queue.ClaimVerification()!, "folder-A", null); fixture.Queue.CompleteVerification(verified);
        var folder = fixture.Queue.Advance(fixture.Queue.ClaimNext()!, "captured-next-page", 2, 0, false);
        fixture.Jobs.Fail(folder.Lease, "drive-unavailable", "Synthetic recoverable read", true);
        var row = fixture.Queue.ReadActivity().Jobs.Single(); Assert.AreEqual(1, row.Summary.Ready); Assert.AreEqual(1, row.Summary.Failed);
        Assert.AreEqual(1, row.RetryableFailures); Assert.AreEqual("drive-unavailable", row.Problems.Single().Code);
        Assert.AreEqual(1, fixture.Queue.RetryFailed(new(command.RequestId, fixture.Source, 1, 1)));
        Assert.AreEqual(0, fixture.Queue.RetryFailed(new(command.RequestId, fixture.Source, 1, 1)));
        Assert.IsFalse(fixture.Queue.RequestScan(command).Created); Assert.IsNull(fixture.Queue.ClaimVerification());
        var resumed = fixture.Queue.ClaimNext()!;
        Assert.AreEqual("captured-next-page", fixture.Queue.Checkpoint(resumed).NextPageToken); Assert.AreEqual(2L, fixture.Queue.Checkpoint(resumed).ObservedItems);
        Assert.AreEqual(1, fixture.Jobs.Summary(command.RequestId).Ready);
    }

    [TestMethod]
    public void HistoricalScopeCannotBeRetargetedByARetryOrRescanReplay()
    {
        using var fixture = new Fixture(); var request = new SourceScanRequest(Guid.NewGuid(), fixture.Source, 1, 1);
        fixture.Queue.RequestScan(request); var checking = fixture.Queue.ClaimVerification()!;
        fixture.Jobs.Fail(checking.Lease, "drive-authorization", "Synthetic denied authorization", true);
        fixture.Sources.Update(fixture.Source, true, false, 1, fixture.Sources.Snapshot().Revision);
        Assert.IsFalse(fixture.Queue.ReadActivity().Jobs.Single().Current);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.RetryFailed(new(request.RequestId, fixture.Source, 1, 1)));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.RequestScan(request));
        Assert.AreEqual(1, fixture.Jobs.Summary(request.RequestId).Failed);
    }

    [TestMethod]
    public void ActivityRejectsCorruptCapturedScopeInsteadOfPresentingEmptyOrReadyWork()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory();
        using var fixture = new Fixture(directory.Path); fixture.Queue.EnsureScheduled(fixture.Source, 1, 1);
        using var external = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.Path, "processor.db"), Pooling = false }.ToString());
        external.Open(); using var corrupt = external.CreateCommand();
        corrupt.CommandText = "UPDATE job SET captured_context='{}' WHERE kind='source-discovery'"; corrupt.ExecuteNonQuery();
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.ReadActivity());
    }
}
