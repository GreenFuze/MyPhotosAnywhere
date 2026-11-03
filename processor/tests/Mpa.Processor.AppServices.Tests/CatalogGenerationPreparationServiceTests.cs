using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task ActualGenerationPreparationCapturesBuildsAndRereadsBeforeResumingTheSamePlan(int count)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var reads = ConfigureGenerationWorkspace(fixture);
        if (count != 0) await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        CapturedCatalogPublication first;
        await using (var current = CatalogReconciler(fixture))
        await using (var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false))
            first = await preparation.PrepareAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Assert.AreEqual("dependencies-ready", first.Capture.Phase); Assert.IsNotNull(first.Plan);
        Assert.AreEqual((long)count, first.Basis.Snapshot.Assets);
        Assert.IsTrue(reads.Count(file => file == first.Plan.RootIndex.FileId) >= 3);
        var before = (fixture.Creates, fixture.Allocations, Reads: reads.Count);
        await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false);
        await using var nextCurrent = CatalogReconciler(fixture);
        await using var next = await PreparationOwnerAsync(fixture, nextCurrent).ConfigureAwait(false);
        Assert.AreEqual(first, await next.PrepareAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(before.Creates, fixture.Creates); Assert.AreEqual(before.Allocations, fixture.Allocations); Assert.IsTrue(reads.Count > before.Reads);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            Assert.IsNull(new ProtocolSigningJournal(database, fixture.Budget).Read(first.Capture.CommitIntentId));
            Assert.AreEqual(first.Capture, new CatalogGenerationCaptureRepository(database, fixture.Budget).Active().Single());
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LostDependencyReplyResumesOriginalFrozenPhotoSetAcrossNewActualPreparationOwners()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!; var lost = false;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var stored = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            if (mime == "application/gzip" && !lost) { lost = true; throw new HttpRequestException("Synthetic lost actual compressed dependency reply."); }
            return stored;
        };
        await using (var current = CatalogReconciler(fixture))
        await using (var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false))
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var claim = new CatalogGenerationCaptureRepository(database, fixture.Budget).Active().Single();
            var pending = new WorkspacePublicationRepository(database, fixture.Budget); var record = pending.Read(pending.Pending().Single())!;
            return (Claim: claim, Record: record);
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("publication-planned", retained.Claim.Phase); Assert.IsNotNull(retained.Record.FileId); Assert.IsNull(retained.Record.VerifiedVersion);
        Assert.IsTrue(fixture.Cloud.ContainsKey(retained.Record.FileId));
        await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.WorkspaceCreate = create;
        await using var resumedCurrent = CatalogReconciler(fixture);
        await using var resumed = await PreparationOwnerAsync(fixture, resumedCurrent).ConfigureAwait(false);
        var result = await resumed.PrepareAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Assert.AreEqual(retained.Claim.Scope, result.Capture.Scope); Assert.AreEqual(retained.Claim.SnapshotId, result.Capture.SnapshotId);
        Assert.AreEqual(retained.Claim.CommitIntentId, result.Capture.CommitIntentId); Assert.AreEqual(1L, result.Basis.Snapshot.Assets);
        var actual = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Read(retained.Record.Id)!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(retained.Record.FileId, actual.FileId); Assert.IsNotNull(actual.VerifiedVersion); Assert.AreEqual(fixture.Allocations, fixture.Cloud.Count);
    }

    [TestMethod]
    public async Task PreviouslyPreparedPlanCannotPassAfterAnActualDependencyDisappears()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var first = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false); var before = fixture.Allocations;
        Assert.IsNotNull(first.Plan); fixture.Cloud.Remove(first.Plan.RootIndex.FileId);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before, fixture.Allocations); Assert.IsNull(current.Current);
        Assert.AreEqual(first, await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, fixture.Budget)
            .Publication(first.Capture.Scope.GenerationId), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ChangesDuringActualPreparedGraphReadsCannotSealItsPlanAndRetryKeepsTheOriginalCapture()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        var metadata = fixture.Setup.Accounts.Readers.MetadataRead!; var changes = fixture.Setup.Accounts.Readers.DocumentChanges!;
        var rootReads = 0; var graphRead = false; Mpa.Processor.GoogleDrive.DriveWorkspaceObject? changed = null;
        fixture.Setup.Accounts.Readers.MetadataRead = async (folder, candidate, token) =>
        {
            var result = await metadata(folder, candidate, token).ConfigureAwait(false);
            if (candidate.MimeType == "application/json" && System.Text.Encoding.UTF8.GetString(result.Bytes.Span).Contains("mpa-catalog-index", StringComparison.Ordinal) && ++rootReads == 3)
            {
                graphRead = true; changed = result with { DriveVersion = result.DriveVersion + 1 };
                fixture.Cloud[result.FileId] = changed;
            }
            return result;
        };
        fixture.Setup.Accounts.Readers.DocumentChanges = (cursor, token) => graphRead
            ? Task.FromResult(new Mpa.Processor.GoogleDrive.DriveChangePage(new([new("file", changed!.FileId, false,
                new("application/json", new(["metadata"]), false, changed.DriveVersion, changed.Bytes.Length, true))]), null, cursor))
            : changes(cursor, token);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(graphRead); Assert.IsNull(current.Current);
        var claim = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var active = new CatalogGenerationCaptureRepository(database, fixture.Budget).Active().Single();
            var saved = new CatalogGenerationCaptureRepository(database, fixture.Budget).Publication(active.Scope.GenerationId)!;
            Assert.AreEqual("publication-planned", active.Phase); Assert.IsNull(saved.Plan);
            Assert.IsNull(new ProtocolSigningJournal(database, fixture.Budget).Read(active.CommitIntentId)); return active;
        }, CancellationToken.None).ConfigureAwait(false);
        var allocated = fixture.Allocations; fixture.Setup.Accounts.Readers.DocumentChanges = changes; fixture.Setup.Accounts.Readers.MetadataRead = metadata;
        var resumed = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(claim.Scope, resumed.Capture.Scope); Assert.AreEqual(claim.SnapshotId, resumed.Capture.SnapshotId);
        Assert.AreEqual(allocated, fixture.Allocations); Assert.AreEqual("dependencies-ready", resumed.Capture.Phase);
    }

    [TestMethod]
    public async Task PreparationExitJoinsActualUploadAndReleasesRetainedSqliteWithoutDeletingItsClaim()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        { var stored = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return stored; };
        var pending = preparation.PrepareAsync(CancellationToken.None); Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = preparation.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(preparation.ExitConfirmed); Assert.IsFalse(current.ExitConfirmed);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(preparation.ExitConfirmed);
        var path = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var claim = new CatalogGenerationCaptureRepository(database, fixture.Budget).Active().Single();
            Assert.AreEqual("publication-planned", claim.Phase); Assert.IsNotNull(claim.Snapshot);
            return Path.Combine(database.StateDirectory, "catalog-generation-snapshots", "snapshot-" + claim.SnapshotId.ToString("N") + ".sqlite");
        }, CancellationToken.None).ConfigureAwait(false);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); Assert.IsTrue(exclusive.Length > 0);
    }

    private static async Task<CatalogGenerationPreparationService> PreparationOwnerAsync(InitialPublicationFixture fixture, LocalCatalogReconciliationService current)
    {
        var owner = await fixture.Setup.Accounts.State.ExecuteAsync(database => (database.StateDirectory, database.InstallationId), CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Setup.Accounts.State, owner.StateDirectory, owner.InstallationId, fixture.Budget, current, fixture.Setup.Keys);
    }
    private static Task<int> AddPreparationPhotoAsync(InitialPublicationFixture fixture, string file) => fixture.Setup.Accounts.State.ExecuteAsync(database =>
    {
        var source = new LibrarySourceRepository(database).Snapshot().Sources.Single();
        return new DiscoveredCatalogRepository(database).ApplyBatch(source.Id, source.Revision, 1, [new(file, 1, "Photo.jpg", "image/jpeg", 40, 30, 100, null, null, 0)]);
    }, CancellationToken.None);
    private static List<string> ConfigureGenerationWorkspace(InitialPublicationFixture fixture)
    {
        var reads = ConfigureDependencyWorkspace(fixture);
        fixture.Setup.Accounts.Readers.WorkspaceCreate = (folder, file, mime, bytes, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Assert.AreEqual("metadata", folder); fixture.Creates++;
            if (!fixture.Cloud.TryGetValue(file, out var actual))
            {
                actual = new("permission-A", file, folder, 7, bytes.ToArray()); fixture.Cloud.Add(file, actual); fixture.MetadataMimeTypes.Add(file, mime);
            }
            CollectionAssert.AreEqual(bytes.ToArray(), actual.Bytes.ToArray()); Assert.AreEqual(mime, fixture.MetadataMimeTypes[file]);
            return Task.FromResult(actual);
        };
        return reads;
    }
}
