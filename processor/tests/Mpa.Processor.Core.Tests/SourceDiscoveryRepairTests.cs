using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class SourceDiscoveryRepositoryTests
{
    private static readonly string[] CapturedRepairPages = ["captured-page-token", "later-page-token"];
    [TestMethod]
    public void InvalidItemsRemainIndividuallyRetryableWhileOtherFoldersComplete()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var parent = fixture.Queue.ClaimNext()!;
        Assert.IsTrue(fixture.Queue.RecordFailure(parent, "bad-file-A"));
        Assert.IsFalse(fixture.Queue.RecordFailure(parent, "bad-file-A"));
        Assert.IsNull(fixture.Queue.ClaimRepair()); Assert.AreEqual(1, fixture.Jobs.Summary(job).Failed);
        fixture.Queue.EnqueueChild(parent, "healthy-child", null);
        fixture.Jobs.Complete(fixture.Queue.ClaimNext()!.Lease, "{\"healthyFolder\":true}"u8);
        Assert.AreEqual(1, fixture.Jobs.RetryFailed(job));
        Assert.IsNull(fixture.Queue.ClaimNext()); // Repair cannot be misread as folder-list work.
        var repair = fixture.Queue.ClaimRepair()!; fixture.Queue.ValidateCurrent(repair);
        Assert.AreEqual("bad-file-A", repair.FileId); Assert.IsNull(repair.PageToken);
        Assert.AreEqual(parent.Scope, repair.Scope); Assert.AreEqual(parent.Folder, repair.Folder);
        fixture.Jobs.Complete(repair.Lease, "{\"repaired\":true}"u8);
        Assert.IsFalse(fixture.Queue.RecordFailure(parent, "bad-file-A")); // Crash replay cannot reset a successful repair.
        Assert.AreEqual(0, fixture.Jobs.RetryFailed(job)); Assert.IsNull(fixture.Queue.ClaimRepair());
        Assert.AreEqual(2, fixture.Jobs.Summary(job).Ready);
    }

    [TestMethod]
    public void MissingIdentityCapturesTheExactPageWithoutSubstitutingAnotherPhoto()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var parent = fixture.Queue.ClaimNext()!;
        parent = fixture.Queue.Advance(parent, "captured-page-token", 60, 0, false);
        Assert.IsTrue(fixture.Queue.RecordFailure(parent, null)); Assert.IsFalse(fixture.Queue.RecordFailure(parent, null));
        parent = fixture.Queue.Advance(parent, "later-page-token", 1, 1, false);
        Assert.IsTrue(fixture.Queue.RecordFailure(parent, null));
        Assert.AreEqual(2, fixture.Jobs.RetryFailed(job));
        var first = fixture.Queue.ClaimRepair()!; var second = fixture.Queue.ClaimRepair()!;
        Assert.IsNull(first.FileId); Assert.IsNull(second.FileId);
        CollectionAssert.AreEquivalent(CapturedRepairPages, new[] { first.PageToken, second.PageToken });
        Assert.AreNotEqual(first.Lease.ItemId, second.Lease.ItemId);
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.ValidateCurrent(first with { FileId = "different-photo" }));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.ValidateCurrent(first with { PageToken = "another-page" }));
    }

    [TestMethod]
    public void RetryRestartAndPolicyChangesRetainTheCapturedFailureAndItsSource()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); Guid job; SourceDiscoveryRepair repair;
        using (var fixture = new Fixture(directory.Path))
        {
            job = fixture.Create(); var parent = fixture.Queue.ClaimNext()!; fixture.Queue.RecordFailure(parent, "bad-file-A");
            fixture.Jobs.RetryFailed(job); repair = fixture.Queue.ClaimRepair()!;
            fixture.Jobs.Fail(repair.Lease, "invalid-source-metadata", "Still invalid; healthy entries were retained.", true);
        }
        using var database = new ProcessorStateDatabase(directory.Path); var jobs = new DurableJobRepository(database); var queue = new SourceDiscoveryRepository(database);
        Assert.AreEqual(1, jobs.RetryFailed(job)); var retry = queue.ClaimRepair()!;
        Assert.AreEqual(repair.FileId, retry.FileId); Assert.AreEqual(repair.Scope, retry.Scope); Assert.AreEqual(2, retry.Lease.Attempt);
        new DriveAccountRepository(database).Disconnect(retry.Scope.AccountId, retry.Scope.AccountRevision);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => queue.ValidateCurrent(retry));
        Assert.AreEqual("bad-file-A", retry.FileId);
    }

    [TestMethod]
    public void ReplayedFailureKeepsAnAlreadyQueuedRetryAndRejectsMalformedAdmission()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var parent = fixture.Queue.ClaimNext()!;
        fixture.Queue.RecordFailure(parent, "bad-file-A"); Assert.AreEqual(1, fixture.Jobs.RetryFailed(job));
        Assert.IsFalse(fixture.Queue.RecordFailure(parent, "bad-file-A")); Assert.AreEqual(1, fixture.Jobs.Summary(job).Queued);
        Assert.IsNotNull(fixture.Queue.ClaimRepair());
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Queue.RecordFailure(parent, "invalid/file"));
        var ended = fixture.Queue.Advance(parent, null, 59, 1, false);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Queue.RecordFailure(ended, "late-file"));
    }
}
