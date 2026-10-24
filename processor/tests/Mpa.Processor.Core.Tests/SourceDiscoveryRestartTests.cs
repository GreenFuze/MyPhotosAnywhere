using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class SourceDiscoveryRepositoryTests
{
    [TestMethod]
    public void ExclusiveRestartRecoversOnlyMetadataReadsAndPreservesCheckpointsAndUnrelatedWorkerFences()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); Guid source; Guid upload; StageLease old;
        using (var fixture = new Fixture(directory.Path))
        {
            source = fixture.Create(); var work = fixture.Queue.ClaimNext()!; old = work.Lease;
            fixture.Queue.Advance(work, "captured-next-page", 50, 1, false);
            upload = Guid.NewGuid(); fixture.Jobs.CreateWithInitialItem(upload, "takeout-import", "{\"destination\":\"keep\"}"u8,
                new(Guid.NewGuid(), new string('a', 64), [new("upload", new string('b', 64), [])]));
            Assert.AreEqual(upload, fixture.Jobs.ClaimNext("takeout-import")!.JobId);
        }
        using var database = new ProcessorStateDatabase(directory.Path); var repository = new SourceDiscoveryRepository(database); var jobs = new DurableJobRepository(database);
        Assert.AreEqual(1, repository.RecoverInterruptedReadsAfterExclusiveRestart()); Assert.AreEqual(0, repository.RecoverInterruptedReadsAfterExclusiveRestart());
        Assert.AreEqual(1, jobs.Summary(upload).Running); Assert.AreEqual(1, jobs.Summary(source).Queued);
        Assert.ThrowsExactly<StaleStageLeaseException>(() => jobs.SaveCheckpoint(old, "{\"late\":true}"u8));
        var resumed = repository.ClaimNext()!; Assert.AreEqual(2, resumed.Lease.Attempt); Assert.AreEqual("captured-next-page", repository.Checkpoint(resumed).NextPageToken);
    }

    [TestMethod]
    public void ExclusiveMetadataRecoveryPreservesPausedAndCanceledJobPolicies()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); Guid paused; Guid canceled;
        using (var fixture = new Fixture(directory.Path))
        {
            paused = fixture.Create(); fixture.Queue.ClaimNext(); fixture.Jobs.Pause(paused);
            canceled = fixture.Create(); fixture.Queue.ClaimNext(); fixture.Jobs.Pause(canceled, cancelJob: true);
        }
        using var database = new ProcessorStateDatabase(directory.Path); var repository = new SourceDiscoveryRepository(database); var jobs = new DurableJobRepository(database);
        Assert.AreEqual(2, repository.RecoverInterruptedReadsAfterExclusiveRestart());
        Assert.IsTrue(jobs.Summary(paused).Paused); Assert.AreEqual(1, jobs.Summary(paused).Queued);
        Assert.IsTrue(jobs.Summary(canceled).IsCanceled); Assert.AreEqual(1, jobs.Summary(canceled).Canceled); Assert.IsNull(repository.ClaimNext());
        jobs.Resume(paused); Assert.AreEqual(paused, repository.ClaimNext()!.Lease.JobId);
    }
}
