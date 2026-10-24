using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed partial class SourceDiscoveryRepositoryTests
{
    [TestMethod]
    public void FolderCheckpointsSurviveRestartAndLatePagesCannotReplaceTheLatestCheckpoint()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); SourceDiscoveryWork advanced;
        using (var fixture = new Fixture(directory.Path))
        {
            fixture.Create(); var root = fixture.Queue.ClaimNext()!; Assert.AreEqual(0, root.Folder.Depth);
            Assert.AreEqual("folder-A", root.Scope.RootFolderId); Assert.AreEqual("folder-A", root.Folder.FolderId);
            Assert.IsFalse(fixture.Queue.Checkpoint(root).Started);
            advanced = fixture.Queue.Advance(root, "next-page", 40, 1, true);
            Assert.ThrowsExactly<StaleStageLeaseException>(() => fixture.Queue.Advance(root, "late-page", 5, 0, false));
        }
        using var reopened = new ProcessorStateDatabase(directory.Path); var queue = new SourceDiscoveryRepository(reopened);
        var checkpoint = queue.Checkpoint(advanced); Assert.AreEqual("next-page", checkpoint.NextPageToken);
        Assert.AreEqual(40L, checkpoint.ObservedItems); Assert.AreEqual(1L, checkpoint.FailedItems); Assert.IsTrue(checkpoint.IncompleteSearch);
        var ended = queue.Advance(advanced, null, 19, 0, false); var final = queue.Checkpoint(ended);
        Assert.AreEqual(59L, final.ObservedItems); Assert.AreEqual(1L, final.FailedItems); Assert.IsTrue(final.IncompleteSearch);
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.Advance(ended, "unexpected-more", 1, 0, false));
    }

    [TestMethod]
    public void DuplicateChildDiscoveryPreservesItsCompletedWorkAndCyclesFailBeforeAdmission()
    {
        using var fixture = new Fixture(); fixture.Create(); var parent = fixture.Queue.ClaimNext()!;
        Assert.IsTrue(fixture.Queue.EnqueueChild(parent, "child-B", null));
        var child = fixture.Queue.ClaimNext()!; Assert.AreEqual(1, child.Folder.Depth); Assert.AreEqual("child-B", child.Folder.FolderId);
        fixture.Jobs.Complete(child.Lease, "{\"verifiedMetadata\":true}"u8);
        Assert.IsFalse(fixture.Queue.EnqueueChild(parent, "child-B", null)); Assert.IsNull(fixture.Queue.ClaimNext());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.EnqueueChild(parent, "folder-A", null));
        Assert.AreEqual(1, fixture.Jobs.PendingReceiptCount());
    }

    [TestMethod]
    public void SourceAndAccountChangesFenceCapturedDiscoveryWithoutChangingItsDestination()
    {
        using var fixture = new Fixture(); fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        var source = fixture.Sources.Snapshot(); fixture.Sources.Update(fixture.Source, true, false, 1, source.Revision);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.Advance(work, "new-page", 1, 0, false));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.EnqueueChild(work, "child-B", null));
        Assert.AreEqual("folder-A", work.Scope.RootFolderId); Assert.IsNull(work.Lease.Checkpoint);
        var rejected = Guid.NewGuid();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.Create(rejected, fixture.Source, 1, 1, "folder-A", null));
        Assert.ThrowsExactly<KeyNotFoundException>(() => fixture.Jobs.Summary(rejected));
        fixture.Queue.Create(Guid.NewGuid(), fixture.Source, 2, 1, "folder-A", null); var current = fixture.Queue.ClaimNext()!;
        Assert.IsFalse(current.Scope.IncludeDescendants); Assert.IsFalse(fixture.Queue.EnqueueChild(current, "ignored-child", null));
        new DriveAccountRepository(fixture.Database).Disconnect("account-A", 1);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Queue.ValidateCurrent(current));
    }

    [TestMethod]
    public void PausedReadMustBeJoinedBeforeItsCheckpointCanResumeUnderANewAttempt()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); var work = fixture.Queue.ClaimNext()!;
        var stopping = fixture.Jobs.Pause(job).Single();
        Assert.ThrowsExactly<StaleStageLeaseException>(() => fixture.Queue.Advance(work, "late-page", 1, 0, false));
        Assert.IsNull(fixture.Queue.ClaimNext()); fixture.Jobs.ReleaseStoppedAttempt(stopping); fixture.Jobs.Resume(job);
        var resumed = fixture.Queue.ClaimNext()!; Assert.AreEqual(2, resumed.Lease.Attempt);
        Assert.AreEqual(work.Scope, resumed.Scope); Assert.AreEqual(work.Folder, resumed.Folder);
        Assert.AreNotEqual(work.Lease.LeaseId, resumed.Lease.LeaseId);
    }

    [TestMethod]
    public void SharedDriveScopeMalformedWorkAndRepeatedTokensCannotRetargetTheQueue()
    {
        using var fixture = new Fixture(); fixture.Create(shared: "drive-A"); var work = fixture.Queue.ClaimNext()!;
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.EnqueueChild(work, "child-B", "another-drive"));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.ValidateCurrent(work with { Scope = work.Scope with { RootFolderId = "another-root" } }));
        var next = fixture.Queue.Advance(work, "next-page", 1, 0, false);
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Queue.Advance(next, "next-page", 1, 0, false));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Queue.Advance(next, null, 60, 1, false));
        Assert.AreEqual(1L, fixture.Queue.Checkpoint(next).ObservedItems);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly WorkerProcessTests.WorkerTestDirectory? _directory;
        public ProcessorStateDatabase Database { get; }
        public DurableJobRepository Jobs { get; }
        public LibrarySourceRepository Sources { get; }
        public SourceDiscoveryRepository Queue { get; }
        public Guid Source { get; } = Guid.NewGuid();
        public Fixture(string? path = null)
        {
            if (path is null) { _directory = new(); path = _directory.Path; }
            Database = new(path); Jobs = new(Database); Sources = new(Database); Queue = new(Database);
            var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account-A", "Synthetic account", null);
            Sources.Add(Source, "account-A", 1, "folder-A", "Synthetic folder", true, 1);
        }
        public Guid Create(string? shared = null) { var job = Guid.NewGuid(); Queue.Create(job, Source, 1, 1, "folder-A", shared); return job; }
        public void Dispose() { Database.Dispose(); _directory?.Dispose(); }
    }
}
