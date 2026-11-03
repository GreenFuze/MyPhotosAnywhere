using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task ActualCompletionPublishesTheExactHeadRetiresOnlyPrivateSnapshotAndAdmitsTheNextGeneration(int count)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        if (count != 0) await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var path = await CompletionSnapshotPathAsync(fixture, ready.Capture).ConfigureAwait(false); Assert.IsTrue(File.Exists(path));
        var first = await completion.FinishAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Assert.AreEqual(1L, first.Sequence); Assert.AreEqual((long)count, first.Assets); Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(first, await completion.FinishAsync(first.GenerationId, CancellationToken.None).ConfigureAwait(false));
        await AssertRetiredCompletionAsync(fixture, first).ConfigureAwait(false);
        await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false);
        var nextReady = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(ready.Capture.Scope.GenerationId, nextReady.Capture.Scope.GenerationId);
        Assert.AreNotEqual(ready.Capture.SnapshotId, nextReady.Capture.SnapshotId); Assert.AreEqual(first.Commit, nextReady.Basis.Parent);
        var next = await completion.FinishAsync(nextReady.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, next.Sequence); Assert.AreEqual((long)count + 1, next.Assets); await AssertRetiredCompletionAsync(fixture, next).ConfigureAwait(false);
        Assert.AreEqual(first, await completion.FinishAsync(first.GenerationId, CancellationToken.None).ConfigureAwait(false)); // Historical result cannot roll back the newer head.
        await AssertRetiredCompletionAsync(fixture, next).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CompleteSnapshotCleanupDoesNotRequireGoogleReconnectionAndCannotDeleteACorruptReplacement()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var published = await current.CompleteCapturedGenerationAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var path = await CompletionSnapshotPathAsync(fixture, ready.Capture).ConfigureAwait(false); var original = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var replacement = original.ToArray(); replacement[^1] ^= 1; await File.WriteAllBytesAsync(path, replacement).ConfigureAwait(false);
        var accountRevision = await fixture.Setup.Accounts.State.ExecuteAsync(database => new DriveAccountRepository(database).Accounts()
            .Single(account => account.PermissionId == "permission-A").Revision, CancellationToken.None).ConfigureAwait(false);
        await fixture.Setup.Accounts.Service.DisconnectAsync("permission-A", accountRevision, CancellationToken.None).ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.MetadataInventory = (_, _, _, _) => throw new AssertFailedException("Completed private cleanup must not require a live Drive grant.");
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => completion.FinishAsync(published.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        Assert.AreEqual("snapshot-retiring", (await ReadCompletionCaptureAsync(fixture, published.GenerationId).ConfigureAwait(false)).Phase);
        await File.WriteAllBytesAsync(path, original).ConfigureAwait(false);
        Assert.AreEqual(published, await completion.FinishAsync(published.GenerationId, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(path)); await AssertRetiredCompletionAsync(fixture, published).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LostRetirementCheckpointRecoversOnlyItsPersistedMissingSnapshotClaim()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var published = await current.CompleteCapturedGenerationAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var claimed = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var capture = new CatalogGenerationCaptureRepository(database, fixture.Budget).BeginSnapshotRetirement(published.GenerationId);
            return (Capture: capture, Store: new CatalogGenerationSnapshotStore(database.StateDirectory, database, fixture.Budget));
        }, CancellationToken.None).ConfigureAwait(false);
        using (var store = claimed.Store)
        {
            using var actual = await Task.Run(() => store.RetireCompletedSnapshot(claimed.Capture, CancellationToken.None)).ConfigureAwait(false);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Dispose()); // Native-use owner must outlive the opaque receipt.
            var noSpace = new StorageBudgetManager(new RetirementNoSpace());
            await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(() => fixture.Setup.Accounts.State.ExecuteAsync(database =>
                new CatalogGenerationCaptureRepository(database, noSpace).ConfirmSnapshotRetired(actual), CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("snapshot-retiring", (await ReadCompletionCaptureAsync(fixture, published.GenerationId).ConfigureAwait(false)).Phase);
            // Deliberately lose the DB confirmation; disposing the native token joins
            // ownership but does not manufacture a retired state or clear its claim.
        }
        var path = await CompletionSnapshotPathAsync(fixture, ready.Capture).ConfigureAwait(false); Assert.IsFalse(File.Exists(path));
        Assert.AreEqual("snapshot-retiring", (await ReadCompletionCaptureAsync(fixture, published.GenerationId).ConfigureAwait(false)).Phase);
        fixture.Setup.Accounts.Readers.MetadataInventory = (_, _, _, _) => throw new AssertFailedException("Retirement recovery cannot need new Drive work.");
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        Assert.AreEqual(published, await completion.FinishAsync(published.GenerationId, CancellationToken.None).ConfigureAwait(false));
        await AssertRetiredCompletionAsync(fixture, published).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FailedActualHeadCheckpointResumesVerifiedGenerationWithNewOwnersAndNoReplacementUpload()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false); CapturedCatalogPublication ready;
        await using (var current = CatalogReconciler(fixture))
        await using (var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false))
        await using (var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current))
        {
            ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            await CompletionSqlAsync(fixture, "CREATE TRIGGER fail_actual_completed_head BEFORE INSERT ON processor_meta WHEN NEW.key='catalog_published_head_v1' BEGIN SELECT RAISE(ABORT,'synthetic actual head checkpoint failure'); END").ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => completion.FinishAsync(ready.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("commit-verified", (await ReadCompletionCaptureAsync(fixture, ready.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
            Assert.IsNull(current.Current); Assert.IsNull(completion.Failure);
        }
        var before = (fixture.Creates, fixture.Allocations);
        await CompletionSqlAsync(fixture, "DROP TRIGGER fail_actual_completed_head").ConfigureAwait(false);
        await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false);
        await using var resumedCurrent = CatalogReconciler(fixture);
        await using var resumed = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, resumedCurrent);
        var published = await resumed.FinishAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, published.Assets); Assert.AreEqual(1L, published.Sequence);
        Assert.AreEqual(before.Creates, fixture.Creates); Assert.AreEqual(before.Allocations, fixture.Allocations);
        await AssertRetiredCompletionAsync(fixture, published).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CompletionExitJoinsTheActualAccountReadWithoutRetiringItsBorrowedReconciler()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var read = fixture.Setup.Accounts.Readers.WorkspaceRead!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceRead = async (folder, file, bytes, hash, token) =>
        { var result = await read(folder, file, bytes, hash, token).ConfigureAwait(false); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return result; };
        var finishing = completion.FinishAsync(ready.Capture.Scope.GenerationId, CancellationToken.None); Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = completion.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(completion.ExitConfirmed); Assert.IsFalse(current.ExitConfirmed);
            Assert.IsNotNull(fixture.Setup.Keys.PublicIdentity);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => finishing).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(completion.ExitConfirmed); Assert.IsFalse(current.ExitConfirmed);
        Assert.AreEqual("commit-sealed", (await ReadCompletionCaptureAsync(fixture, ready.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        Assert.IsTrue(File.Exists(await CompletionSnapshotPathAsync(fixture, ready.Capture).ConfigureAwait(false)));
    }

    [TestMethod]
    public async Task AnActualBorrowedSnapshotReaderPreventsRetirementUntilItsNativeOwnerCloses()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var sealedClaim = await ReadCompletionCaptureAsync(fixture, ready.Capture.Scope.GenerationId).ConfigureAwait(false);
        var store = await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationSnapshotStore(database.StateDirectory, database, fixture.Budget), CancellationToken.None).ConfigureAwait(false);
        PublishedCatalogHead published;
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        using (store)
        using (var reader = store.Open(sealedClaim, CancellationToken.None))
        {
            published = await current.CompleteCapturedGenerationAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsAsync<IOException>(() => completion.FinishAsync(published.GenerationId, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1L, reader.Count); Assert.AreEqual("original-photo", reader.ReadPage("").Assets.Single().FileId);
            Assert.IsNull(completion.Failure);
        }
        Assert.AreEqual(published, await completion.FinishAsync(published.GenerationId, CancellationToken.None).ConfigureAwait(false));
        await AssertRetiredCompletionAsync(fixture, published).ConfigureAwait(false);
    }

    private static Task<CatalogGenerationCapture> ReadCompletionCaptureAsync(InitialPublicationFixture fixture, Guid generationId) =>
        fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, fixture.Budget).Read(generationId)
            ?? throw new AssertFailedException("The original captured generation disappeared."), CancellationToken.None);
    private static Task<string> CompletionSnapshotPathAsync(InitialPublicationFixture fixture, CatalogGenerationCapture capture) =>
        fixture.Setup.Accounts.State.ExecuteAsync(database => Path.Combine(database.StateDirectory, "catalog-generation-snapshots", "snapshot-" + capture.SnapshotId.ToString("N") + ".sqlite"), CancellationToken.None);
    private static Task<bool> AssertRetiredCompletionAsync(InitialPublicationFixture fixture, PublishedCatalogHead expected) =>
        fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new CatalogGenerationCaptureRepository(database, fixture.Budget);
            Assert.AreEqual(expected, journal.PublishedHead()); Assert.AreEqual("retired", journal.Read(expected.GenerationId)!.Phase); Assert.HasCount(0, journal.Active());
            var receipt = journal.Verification(expected.GenerationId)!; Assert.AreEqual(expected.Sequence, receipt.Sequence); Assert.AreEqual(expected.Assets, receipt.Assets);
            Assert.AreEqual(expected.Commit.FileId, receipt.Commit.FileId); return true;
        }, CancellationToken.None);
    private sealed class RetirementNoSpace : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 1, 0); }
    private static Task<int> CompletionSqlAsync(InitialPublicationFixture fixture, string sql) => fixture.Setup.Accounts.State.ExecuteAsync(database =>
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(database.StateDirectory, "processor.db"), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteNonQuery();
    }, CancellationToken.None);
}
