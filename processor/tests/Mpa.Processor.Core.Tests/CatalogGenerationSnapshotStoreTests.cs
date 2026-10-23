using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    private static readonly string[] RetainedFixtureFiles = ["old-photo", "unicode-photo"];
    [TestMethod]
    public void RetainedCaptureSurvivesProcessOwnersAndOriginalLibraryChangesWithExactFactsAndCoverage()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); CatalogGenerationCapture sealedCapture;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.PrepareLocalPublication(); AddRetainedFacts(fixture, RetainedFixtureFiles);
            var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
            var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
            using (var directory = new CatalogPreparationDirectory(root.Path, fixture.Database.InstallationId))
            using (var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), directory, fixture.Budget))
            using (var snapshots = new CatalogGenerationSnapshotStore(root.Path, fixture.Database, fixture.Budget))
            {
                var proof = snapshots.Save(frozen, capture, CancellationToken.None); Assert.AreEqual(2L, proof.Assets);
                sealedCapture = journal.SealSnapshot(capture.Scope.GenerationId, proof);
                Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => snapshots.Open(sealedCapture, CancellationToken.None)); // Refresh durable claims before reopening.
            }
            AddRetainedFacts(fixture, "later-photo"); fixture.DisablePublicationSource();
            Assert.IsTrue(CatalogGenerationSnapshotStore.MeasureOwnedBytes(root.Path) >= sealedCapture.Snapshot!.Bytes);
        }
        using var database = new ProcessorStateDatabase(root.Path); var budget = new StorageBudgetManager(new Space());
        var recovered = new CatalogGenerationCaptureRepository(database, budget).Read(sealedCapture.Scope.GenerationId)!; Assert.AreEqual(sealedCapture, recovered);
        using var store = new CatalogGenerationSnapshotStore(root.Path, database, budget); using var original = store.Open(recovered, CancellationToken.None);
        Assert.AreEqual(2L, original.Count); Assert.IsTrue(original.Coverage.Single().Enabled);
        CollectionAssert.AreEquivalent(RetainedFixtureFiles, original.ReadPage("").Assets.Select(asset => asset.FileId).ToArray());
        StringAssert.Contains(original.ReadPage("").Assets.Single(asset => asset.FileId == "unicode-photo").Name, "שלום");
        Assert.AreEqual(recovered.Snapshot!.ConfigurationRevision, original.ConfigurationRevision);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Inspect(recovered, CancellationToken.None)); original.Dispose();
        Assert.AreEqual(recovered.Snapshot, store.Inspect(recovered, CancellationToken.None));
    }

    [TestMethod]
    public void RetainedStoreCannotCloseWhileNativeReaderOwnsFileAndDoesNotDeleteItOnExit()
    {
        using var fixture = new Fixture(); var capture = SaveRetentionFixture(fixture);
        var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget);
        var frozen = store.Open(capture, CancellationToken.None);
        Assert.ThrowsExactly<InvalidOperationException>(store.Dispose);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Open(capture, CancellationToken.None));
        Assert.AreEqual(1, frozen.ReadPage("").Assets.Count); frozen.Dispose(); frozen.Dispose(); store.Dispose(); store.Dispose();
        Assert.IsTrue(File.Exists(RetainedPath(fixture, capture.SnapshotId))); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        using var reopened = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget);
        using var snapshot = reopened.Open(capture, CancellationToken.None); Assert.AreEqual(1L, snapshot.Count);
    }

    [TestMethod]
    public void CorruptFrozenFileIsPreservedAndNeverRecapturedOrAcceptedAsAnotherPhoto()
    {
        using var fixture = new Fixture(); var capture = SaveRetentionFixture(fixture); var path = RetainedPath(fixture, capture.SnapshotId);
        using (var edit = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        { edit.Open(); using var command = edit.CreateCommand(); command.CommandText = "UPDATE fact SET payload=X'7b7d'"; command.ExecuteNonQuery(); }
        using var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Open(capture, CancellationToken.None)); Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(capture, new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget).Active().Single());
    }

    [TestMethod]
    public void RecoveryInspectsTheWholeInventoryBeforeRemovingOnlyUnclaimedGeneratedFiles()
    {
        using var fixture = new Fixture(); var capture = SaveRetentionFixture(fixture); var root = Path.GetDirectoryName(RetainedPath(fixture, capture.SnapshotId))!;
        var captures = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget); captures.AbandonUnpublished(capture.Scope.GenerationId);
        var active = captures.Begin(capture.Scope with { GenerationId = Guid.NewGuid() }, Guid.NewGuid(), Guid.NewGuid());
        var orphan = RetainedPath(fixture, capture.SnapshotId);
        var unexpected = Path.Combine(root, "Human-notes.txt"); File.WriteAllText(unexpected, "must remain");
        Assert.ThrowsExactly<InvalidDataException>(() => new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget));
        Assert.IsTrue(File.Exists(orphan)); Assert.AreEqual("must remain", File.ReadAllText(unexpected));
        File.Delete(unexpected); // Explicit test-fixture cleanup only.
        var unknown = RetainedPath(fixture, Guid.NewGuid()); File.WriteAllText(unknown, "No matching creation history: preserve even with a generated-looking name");
        Assert.ThrowsExactly<InvalidDataException>(() => new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget));
        Assert.IsTrue(File.Exists(orphan)); Assert.IsTrue(File.Exists(unknown)); File.Delete(unknown); // Remove only this test's injected unknown file.
        using var recovered = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget);
        Assert.IsFalse(File.Exists(orphan)); Assert.AreEqual(active, captures.Active().Single());
    }

    [TestMethod]
    public void MissingClaimedFrozenFileBlocksRecoveryBeforeAnyOrphanIsDeleted()
    {
        using var fixture = new Fixture(); var capture = SaveRetentionFixture(fixture); var path = RetainedPath(fixture, capture.SnapshotId); File.Delete(path);
        var orphan = RetainedPath(fixture, Guid.NewGuid()); File.WriteAllText(orphan, "preserved recovery evidence");
        Assert.ThrowsExactly<InvalidDataException>(() => new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget)); Assert.IsTrue(File.Exists(orphan));
        Assert.AreEqual(capture, new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget).Read(capture.Scope.GenerationId));
    }

    [TestMethod]
    public async Task CanceledUnsealedCopyRetainsItsClaimAndRequiresInspectionOrExplicitUnpublishedAbandonment()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); AddRetainedFacts(fixture, "retained");
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
        using var directory = new CatalogPreparationDirectory(fixture.StatePath, fixture.Database.InstallationId);
        using var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), directory, fixture.Budget);
        using (var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget))
        using (var cancel = new CancellationTokenSource())
        {
            var reads = 0; fixture.Space.OnRead = () => { if (++reads == 2) cancel.Cancel(); };
            await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => store.Save(frozen, capture, cancel.Token))).ConfigureAwait(false); fixture.Space.OnRead = null;
            Assert.IsTrue(File.Exists(RetainedPath(fixture, capture.SnapshotId))); Assert.AreEqual(capture, journal.Active().Single());
            Assert.ThrowsExactly<InvalidDataException>(() => store.Inspect(capture, CancellationToken.None));
            Assert.ThrowsExactly<IOException>(() => store.Save(frozen, capture, CancellationToken.None));
        }
        journal.AbandonUnpublished(capture.Scope.GenerationId);
        using var recovered = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget);
        Assert.IsFalse(File.Exists(RetainedPath(fixture, capture.SnapshotId))); Assert.AreEqual(1L, frozen.Count);
    }

    [TestMethod]
    public void CompletedButUnsealedCopyCanBeInspectedWithoutReplacingBytesAfterLostLocalCheckpoint()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); AddRetainedFacts(fixture, "retained");
        var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid()); CatalogSnapshotDescriptor saved;
        using (var directory = new CatalogPreparationDirectory(fixture.StatePath, fixture.Database.InstallationId))
        using (var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), directory, fixture.Budget))
        using (var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget)) saved = store.Save(frozen, capture, CancellationToken.None);
        using (var resumed = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget))
            Assert.AreEqual(saved, resumed.Inspect(capture, CancellationToken.None));
        var sealedCapture = journal.SealSnapshot(capture.Scope.GenerationId, saved);
        using var final = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget); using var source = final.Open(sealedCapture, CancellationToken.None);
        Assert.AreEqual(1L, source.Count);
    }

    [TestMethod]
    public void LowStorageAndUnmarkedOrForeignDirectoriesNeverConsumeOrRetargetASnapshotClaim()
    {
        using var fixture = new Fixture(); fixture.PrepareLocalPublication(); AddRetainedFacts(fixture, "retained");
        var captures = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = captures.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
        using var directory = new CatalogPreparationDirectory(fixture.StatePath, fixture.Database.InstallationId);
        using var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), directory, fixture.Budget);
        using (var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget))
        {
            fixture.Space.Available = 1;
            Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => store.Save(frozen, capture, CancellationToken.None));
            Assert.IsFalse(File.Exists(RetainedPath(fixture, capture.SnapshotId))); Assert.AreEqual(capture, captures.Active().Single());
            fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
            Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(() => store.Inspect(capture with { Scope = capture.Scope with { FolderId = "other" } }, CancellationToken.None));
        }
        var retainedRoot = Path.GetDirectoryName(RetainedPath(fixture, capture.SnapshotId))!;
        File.Delete(Path.Combine(retainedRoot, "owner.json"));
        var evidence = Path.Combine(retainedRoot, "notes.txt"); File.WriteAllText(evidence, "do not claim this directory");
        Assert.ThrowsExactly<InvalidDataException>(() => new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget));
        Assert.AreEqual("do not claim this directory", File.ReadAllText(evidence));
        using var other = new WorkerProcessTests.WorkerTestDirectory(); Directory.CreateDirectory(other.Path);
        File.Copy(Path.Combine(fixture.StatePath, "mpa-processor-state.json"), Path.Combine(other.Path, "mpa-processor-state.json"));
        Assert.ThrowsExactly<ArgumentException>(() => new CatalogGenerationSnapshotStore(other.Path, fixture.Database, fixture.Budget));
        Assert.IsFalse(Directory.Exists(Path.Combine(other.Path, "catalog-generation-snapshots")));
    }

    [TestMethod]
    public async Task StorageAccountingRemainsValidWhileGeneratedTransactionJournalAppearsAndDisappears()
    {
        using var fixture = new Fixture(); var capture = SaveRetentionFixture(fixture);
        using var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget);
        var journal = RetainedPath(fixture, capture.SnapshotId) + "-journal";
        using var cancel = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = Task.Run(() =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    using (var output = new FileStream(journal, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) output.WriteByte(1);
                    entered.TrySetResult(); File.Delete(journal); Thread.Yield();
                }
            }
            finally { if (File.Exists(journal)) File.Delete(journal); }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            for (var i = 0; i < 64; i++) Assert.IsTrue(CatalogGenerationSnapshotStore.MeasureOwnedBytes(fixture.StatePath) >= capture.Snapshot!.Bytes);
        }
        finally { await cancel.CancelAsync().ConfigureAwait(false); await writing.ConfigureAwait(false); }
        using var frozen = store.Open(capture, CancellationToken.None); Assert.AreEqual(1L, frozen.Count);
    }

    private static CatalogGenerationCapture SaveRetentionFixture(Fixture fixture)
    {
        fixture.PrepareLocalPublication(); AddRetainedFacts(fixture, "retained-photo"); var journal = new CatalogGenerationCaptureRepository(fixture.Database, fixture.Budget);
        var capture = journal.Begin(Scope(fixture.Store.CaptureLocalPublicationAuthority()), Guid.NewGuid(), Guid.NewGuid());
        using var directory = new CatalogPreparationDirectory(fixture.StatePath, fixture.Database.InstallationId);
        using var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), directory, fixture.Budget);
        using var store = new CatalogGenerationSnapshotStore(fixture.StatePath, fixture.Database, fixture.Budget);
        return journal.SealSnapshot(capture.Scope.GenerationId, store.Save(frozen, capture, CancellationToken.None));
    }
    private static void AddRetainedFacts(Fixture fixture, params string[] files) => new DiscoveredCatalogRepository(fixture.Database).ApplyBatch(new LibrarySourceRepository(fixture.Database).Snapshot().Sources.Single().Id, 1, 1,
        files.Select(file => new DiscoveredDriveAsset(file, 1, file + "-שלום.jpg", "image/jpeg", 80, 40, 1, null, null, 0)).ToArray());
    private static string RetainedPath(Fixture fixture, Guid id) => Path.Combine(fixture.StatePath, "catalog-generation-snapshots", "snapshot-" + id.ToString("N") + ".sqlite");
    private sealed partial class Fixture { public string StatePath => _path; }
}
