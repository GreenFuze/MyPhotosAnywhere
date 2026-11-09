using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class SourceDiscoveryRepositoryTests
{
    [TestMethod]
    public void AutomaticSchedulingIsIdempotentAndCannotResetCompletedOrFailedWork()
    {
        using var fixture = new Fixture(); var initial = fixture.Queue.EnsureScheduled(fixture.Source, 1, 1);
        Assert.IsTrue(initial.Created); Assert.IsFalse(fixture.Queue.EnsureScheduled(fixture.Source, 1, 1).Created);
        Assert.IsNull(fixture.Queue.ClaimNext()); Assert.IsNull(fixture.Queue.ClaimRepair());
        var verifying = fixture.Queue.ClaimVerification()!;
        fixture.Jobs.Fail(verifying.Lease, "drive-authorization", "Synthetic failed root check", true);
        Assert.IsFalse(fixture.Queue.EnsureScheduled(fixture.Source, 1, 1).Created); Assert.IsNull(fixture.Queue.ClaimVerification());
        Assert.AreEqual(1, fixture.Jobs.RetryFailed(initial.JobId));
        var retried = fixture.Queue.ClaimVerification()!; Assert.AreEqual(2, retried.Lease.Attempt);
        var accepted = fixture.Queue.SaveVerifiedRoot(retried, "folder-A", "drive-A"); fixture.Queue.CompleteVerification(accepted);
        Assert.IsFalse(fixture.Queue.EnsureScheduled(fixture.Source, 1, 1).Created); Assert.IsNull(fixture.Queue.ClaimVerification());
        var root = fixture.Queue.ClaimNext()!; Assert.AreEqual(initial.JobId, root.Lease.JobId); Assert.AreEqual("drive-A", root.Folder.SharedDriveId);
    }

    [TestMethod]
    public void AcceptedVerificationResumesAfterRestartWithoutReplacingTheCapturedRoot()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); Guid job;
        using (var fixture = new Fixture(directory.Path))
        {
            job = fixture.Queue.EnsureScheduled(fixture.Source, 1, 1).JobId;
            var accepted = fixture.Queue.SaveVerifiedRoot(fixture.Queue.ClaimVerification()!, "folder-A", "drive-A");
            fixture.Jobs.ReturnJoinedRead(accepted.Lease);
        }
        using var database = new ProcessorStateDatabase(directory.Path); var queue = new SourceDiscoveryRepository(database);
        var resumed = queue.ClaimVerification()!; Assert.AreEqual(job, resumed.Lease.JobId);
        Assert.AreEqual(new VerifiedDiscoveryRoot("folder-A", "drive-A"), queue.VerifiedRoot(resumed));
        Assert.ThrowsExactly<InvalidDataException>(() => queue.SaveVerifiedRoot(resumed, "folder-A", "another-drive"));
        Assert.ThrowsExactly<InvalidDataException>(() => queue.SaveVerifiedRoot(resumed, "different-root", "drive-A"));
        queue.CompleteVerification(resumed); Assert.AreEqual("folder-A", queue.ClaimNext()!.Folder.FolderId);
    }

    [TestMethod]
    public void APolicyRevisionCreatesANewRequestAndFencesTheEarlierVerification()
    {
        using var fixture = new Fixture(); var earlier = fixture.Queue.EnsureScheduled(fixture.Source, 1, 1); var verifying = fixture.Queue.ClaimVerification()!;
        var sources = fixture.Sources.Snapshot(); fixture.Sources.Update(fixture.Source, true, false, 1, sources.Revision);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.SaveVerifiedRoot(verifying, "folder-A", null));
        var replacement = fixture.Queue.EnsureScheduled(fixture.Source, 2, 1); Assert.IsTrue(replacement.Created); Assert.AreNotEqual(earlier.JobId, replacement.JobId);
        var next = fixture.Queue.ClaimVerification()!; Assert.IsFalse(next.Scope.IncludeDescendants);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Queue.CompleteVerification(next)); Assert.IsNull(fixture.Queue.ClaimNext());
    }
}
