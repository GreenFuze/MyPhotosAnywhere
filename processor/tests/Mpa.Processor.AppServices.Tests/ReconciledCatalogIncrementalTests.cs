using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task AnAllocatedUncreatedDependencyResumesItsOriginalIdThroughFullReconciliation()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope); var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = (_, _, _, _, _, _) => throw new HttpRequestException("Synthetic failure before actual create.");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => StoreIncrementalDependencyAsync(sink, 1)).ConfigureAwait(false);
        var pending = await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId); Assert.IsNull(pending.VerifiedVersion); Assert.IsFalse(fixture.Cloud.ContainsKey(pending.FileId));
        fixture.Setup.Accounts.Readers.WorkspaceCreate = create; var before = feed.FullInventories;
        var result = await StoreIncrementalDependencyAsync(sink, 1).ConfigureAwait(false);
        Assert.AreEqual(pending.FileId, result.FileId); Assert.AreEqual(3, fixture.Allocations); Assert.AreEqual(3, fixture.Creates);
        Assert.AreEqual(before + 2, feed.FullInventories); Assert.AreEqual(3, owner.Current!.Observation.Documents); Assert.IsNull(owner.Failure);
    }

    [TestMethod]
    public async Task IncrementalNativeByteReuseNeverSubstitutesForNonlocalActualDocumentReads()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Cloud.Add("nonlocal-index", new("permission-A", "nonlocal-index", "metadata", 7, "{}"u8.ToArray()));
        var feed = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope); await StoreIncrementalDependencyAsync(sink, 1).ConfigureAwait(false);
        var before = feed.MetadataReads.Count(id => id == "nonlocal-index"); var inventories = feed.FullInventories;
        await StoreIncrementalDependencyAsync(sink, 2).ConfigureAwait(false);
        Assert.AreEqual(inventories, feed.FullInventories); Assert.AreEqual(before + 2, feed.MetadataReads.Count(id => id == "nonlocal-index"));
        var read = fixture.Setup.Accounts.Readers.MetadataRead!;
        fixture.Setup.Accounts.Readers.MetadataRead = (folder, candidate, token) => candidate.FileId == "nonlocal-index"
            ? throw new HttpRequestException("Synthetic current nonlocal metadata outage.") : read(folder, candidate, token);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => StoreIncrementalDependencyAsync(sink, 3)).ConfigureAwait(false);
        Assert.AreEqual(4, fixture.Creates); Assert.AreEqual(4, fixture.Allocations); Assert.IsNull(owner.Current);
    }

    [TestMethod]
    public async Task UnrelatedNativeWorkDuringActualIncrementalReadCannotBorrowTheFollowingUpload()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); _ = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope); var first = await StoreIncrementalDependencyAsync(sink, 1).ConfigureAwait(false);
        var changes = fixture.Setup.Accounts.Readers.DocumentChanges!; var mutated = false; var foreign = Guid.NewGuid();
        fixture.Setup.Accounts.Readers.DocumentChanges = async (cursor, token) =>
        {
            if (!mutated)
            {
                mutated = true;
                await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspacePublicationRepository(db, fixture.Budget)
                    .Prepare(foreign, generation.Capture.Scope with { GenerationId = Guid.NewGuid() }, "facts-index", "application/json", "{}"u8), token).ConfigureAwait(false);
            }
            return await changes(cursor, token).ConfigureAwait(false);
        };
        await Assert.ThrowsExactlyAsync<CatalogGenerationInProgressException>(() => StoreIncrementalDependencyAsync(sink, 2)).ConfigureAwait(false);
        Assert.IsTrue(mutated); Assert.AreEqual(3, fixture.Creates); Assert.AreEqual(3, fixture.Allocations);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspacePublicationRepository(db, fixture.Budget).Read(foreign), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(retained); Assert.IsNull(retained.FileId); Assert.IsNull(retained.VerifiedVersion);
        Assert.AreEqual(first.FileId, (await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false)).FileId);
    }

    [TestMethod]
    public async Task ActualIncrementalPreparationKeepsFrozenPhotosAndFullyVerifiesThePublishedGraph()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture, generationWorkspace: true);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!; var added = false;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            if (!added && mime == "application/gzip") { added = true; await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false); }
            return result;
        };
        await using var owner = CatalogReconciler(fixture); await using var preparation = await PreparationOwnerAsync(fixture, owner).ConfigureAwait(false);
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, owner);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(added); Assert.AreEqual(1L, prepared.Basis.Snapshot.Assets); Assert.IsNotNull(prepared.Plan);
        var afterPreparation = feed.FullInventories;
        var published = await completion.FinishAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, published.Sequence); Assert.AreEqual(1L, published.Assets); await AssertRetiredCompletionAsync(fixture, published).ConfigureAwait(false);
        Assert.IsTrue(feed.FullInventories > afterPreparation); // Final commit/graph/completion always use actual full inventories.
        var next = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, next.Basis.Snapshot.Assets); Assert.AreEqual(published.Commit, next.Basis.Parent);
        Assert.AreNotEqual(prepared.Capture.Scope.GenerationId, next.Capture.Scope.GenerationId);
    }

    [TestMethod]
    public async Task AnotherCompletedReadWithTheSameCheckpointClosesThePreviousPrivateIncrementalAttempt()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope); await StoreIncrementalDependencyAsync(sink, 1).ConfigureAwait(false);
        var previous = owner.Current!.Observation; await using var other = CatalogReconciler(fixture);
        Assert.AreEqual(previous, (await other.ReconcileAsync(CancellationToken.None).ConfigureAwait(false)).Observation);
        var before = feed.FullInventories; await StoreIncrementalDependencyAsync(sink, 2).ConfigureAwait(false);
        Assert.AreEqual(before + 1, feed.FullInventories); Assert.AreEqual(4, fixture.Creates); Assert.IsNull(owner.Failure);
    }

    [TestMethod]
    public async Task IncrementalDependenciesUseActualChangesAndLinearMetadataDownloads()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope);
        var inventories = feed.FullInventories; var reads = feed.MetadataReads.Count; var timer = Stopwatch.StartNew();
        var references = new List<Mpa.Processor.Protocol.CatalogObjectReference>();
        for (var i = 0; i < 16; i++) references.Add(await StoreIncrementalDependencyAsync(sink, i).ConfigureAwait(false));
        Assert.AreEqual(inventories + 1, feed.FullInventories); // Initial prewrite read is full; every later dependency read uses the actual feed.
        Assert.AreEqual(reads + 2 + references.Count, feed.MetadataReads.Count);
        foreach (var reference in references) Assert.AreEqual(1, feed.MetadataReads.Count(id => id == reference.FileId));
        Assert.AreEqual(18, owner.Current!.Observation.Documents); Assert.AreEqual(18, fixture.Allocations); Assert.AreEqual(18, fixture.Creates);
        Assert.IsNull(owner.Failure); Assert.AreEqual(0L, owner.Current.Sequence); Assert.IsNull(owner.Current.Parent);
        var known = await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspaceObservationRepository(db, fixture.Budget)
            .Known(owner.Current.Observation.Context), CancellationToken.None).ConfigureAwait(false);
        foreach (var reference in references) Assert.AreEqual(reference.Sha256, known.Single(item => item.FileId == reference.FileId).Sha256);
        Console.WriteLine(FormattableString.Invariant($"Actual dependency fixture: count={references.Count}, metadataDownloads={feed.MetadataReads.Count - reads}, fullInventories={feed.FullInventories - inventories}, elapsedMs={timer.Elapsed.TotalMilliseconds:F2}. This is a structural fixture, not a live-Drive/10k/100k benchmark."));
        // Public reads never inherit private incremental ownership, and must refetch all actual bytes.
        var before = feed.MetadataReads.Count; await owner.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(inventories + 2, feed.FullInventories); Assert.AreEqual(before + 18, feed.MetadataReads.Count);
    }

    [TestMethod]
    public async Task IncrementalAncestorChangeIsRetainedBeforeActualFullRefetch()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope); var first = await StoreIncrementalDependencyAsync(sink, 1).ConfigureAwait(false);
        var inventories = feed.FullInventories;
        feed.Events.Add(new("file", "possible-ancestor", false, new("application/vnd.google-apps.folder", new(["outside"]), false, 8, 0, true)));
        var second = await StoreIncrementalDependencyAsync(sink, 2).ConfigureAwait(false);
        Assert.AreEqual(inventories + 1, feed.FullInventories); Assert.AreNotEqual(first.FileId, second.FileId); Assert.AreEqual(4, fixture.Creates);
        var history = await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspaceObservationRepository(db, fixture.Budget)
            .KnownHistory(owner.Current!.Observation.Context), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(history.Any(item => item.Id == "possible-ancestor" && !item.RecoveryRequired));
        Assert.IsNull(owner.Failure);
    }

    [TestMethod]
    public async Task IncrementalUnknownRemovalCannotBeForgottenByAnEmptyLaterFeed()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope); await StoreIncrementalDependencyAsync(sink, 1).ConfigureAwait(false);
        var context = owner.Current!.Observation.Context; feed.Events.Add(new("file", "unclassified-removal", true, null));
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryRecoveryRequiredException>(() => StoreIncrementalDependencyAsync(sink, 2)).ConfigureAwait(false);
        Assert.AreEqual(3, fixture.Creates); Assert.IsNull(owner.Current);
        var history = await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspaceObservationRepository(db, fixture.Budget)
            .KnownHistory(context), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(history.Single(item => item.Id == "unclassified-removal").RecoveryRequired);
        fixture.Setup.Accounts.Readers.DocumentChanges = (cursor, _) => Task.FromResult(new DriveChangePage([], null, cursor));
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryRecoveryRequiredException>(() => owner.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3, fixture.Creates); Assert.IsNull(owner.Current);
    }

    [TestMethod]
    public async Task MissingIncrementalCreateEventRequiresActualFullInventoryWithoutAnotherUpload()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture) { RecordCreates = false };
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var before = feed.FullInventories;
        var result = await StoreIncrementalDependencyAsync(new(owner, generation.Capture.Scope), 1).ConfigureAwait(false);
        Assert.AreEqual(before + 2, feed.FullInventories); Assert.AreEqual(3, fixture.Creates); Assert.AreEqual(3, fixture.Allocations);
        Assert.AreEqual(result.FileId, (await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false)).FileId);
        Assert.AreEqual(3, owner.Current!.Observation.Documents);
    }

    [TestMethod]
    public async Task ReopenedCurrentOwnerStartsWithActualFullReadBeforeResumingOriginalGeneration()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture);
        await using var first = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, first).ConfigureAwait(false);
        var original = await StoreIncrementalDependencyAsync(new(first, generation.Capture.Scope), 1).ConfigureAwait(false);
        await first.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(first.ExitConfirmed);
        var before = feed.FullInventories; await using var second = CatalogReconciler(fixture);
        var next = await StoreIncrementalDependencyAsync(new(second, generation.Capture.Scope), 2).ConfigureAwait(false);
        Assert.AreEqual(before + 1, feed.FullInventories); Assert.AreNotEqual(original.FileId, next.FileId); Assert.AreEqual(4, fixture.Allocations);
        Assert.AreEqual(generation, await fixture.Setup.Accounts.State.ExecuteAsync(db => new CatalogGenerationCaptureRepository(db, fixture.Budget)
            .Publication(generation.Capture.Scope.GenerationId), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ExitJoinsActualIncrementalFeedAndCannotAdmitItsLatePageOrANewUpload()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var feed = new RecordedDependencyFeed(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope); await StoreIncrementalDependencyAsync(sink, 1).ConfigureAwait(false);
        feed.Hold = true; var work = StoreIncrementalDependencyAsync(sink, 2); Task stopping;
        try
        {
            await feed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); stopping = owner.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(work.IsCompleted); Assert.IsFalse(owner.ExitConfirmed);
        }
        finally { feed.Release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => work).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(owner.ExitConfirmed); Assert.IsNull(owner.Current); Assert.AreEqual(3, fixture.Creates); Assert.AreEqual(3, fixture.Allocations);
    }

    private static Task<Mpa.Processor.Protocol.CatalogObjectReference> StoreIncrementalDependencyAsync(ReconciledCatalogGenerationSink sink, int value) =>
        sink.StoreAsync("facts-index", "application/json", Encoding.UTF8.GetBytes("{\"n\":" + value.ToString(CultureInfo.InvariantCulture) + "}"), CancellationToken.None);

    private sealed class RecordedDependencyFeed
    {
        public List<DriveObservedChange> Events { get; } = [];
        public List<string> MetadataReads { get; } = [];
        public int FullInventories { get; private set; }
        public bool RecordCreates { get; set; } = true;
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RecordedDependencyFeed(InitialPublicationFixture fixture, bool generationWorkspace = false)
        {
            if (generationWorkspace) ConfigureGenerationWorkspace(fixture); else ConfigureDependencyWorkspace(fixture);
            var readers = fixture.Setup.Accounts.Readers;
            var read = readers.MetadataRead!; var create = readers.WorkspaceCreate!;
            readers.MetadataInventory = (folder, drive, next, token) =>
            {
                token.ThrowIfCancellationRequested(); FullInventories++; Assert.AreEqual("metadata", folder); Assert.IsNull(drive);
                var offset = next is null ? 0 : int.Parse(next.AsSpan("metadata-".Length), CultureInfo.InvariantCulture);
                var rows = fixture.Cloud.Values.OrderByDescending(item => item.FileId, StringComparer.Ordinal).ToArray();
                Assert.IsTrue(offset >= 0 && offset < Math.Max(1, rows.Length)); var end = Math.Min(rows.Length, offset + 100);
                return Task.FromResult(new DriveWorkspaceMetadataPage(new(rows.Skip(offset).Take(end - offset).Select(item =>
                    new DriveWorkspaceMetadataCandidate(item.FileId, item.DriveVersion, item.Bytes.Length,
                        fixture.MetadataMimeTypes.GetValueOrDefault(item.FileId, "application/json"))).ToList()),
                    end < rows.Length ? "metadata-" + end.ToString(CultureInfo.InvariantCulture) : null, false));
            };
            readers.MetadataRead = (folder, candidate, token) => { MetadataReads.Add(candidate.FileId); return read(folder, candidate, token); };
            readers.DocumentStart = (drive, _) => Task.FromResult(Cursor(drive, Events.Count));
            readers.DocumentChanges = async (cursor, token) =>
            {
                if (Hold) { Entered.TrySetResult(); await Release.Task.ConfigureAwait(false); } // Intentionally ignores cancellation until actual join.
                var offset = cursor.Token == "current" ? 0 : int.Parse(cursor.Token.AsSpan("feed-".Length), CultureInfo.InvariantCulture);
                Assert.IsTrue(offset >= 0 && offset <= Events.Count); var end = Math.Min(Events.Count, offset + 100);
                var rows = Events.Skip(offset).Take(end - offset).ToList().AsReadOnly();
                return new(rows, end < Events.Count ? Cursor(cursor.SharedDriveId, end) : null, end == Events.Count ? Cursor(cursor.SharedDriveId, end) : null);
            };
            readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
            {
                var existed = fixture.Cloud.ContainsKey(file); var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
                if (!existed && RecordCreates) Events.Add(new("file", file, false, new(mime, new([folder]), false, result.DriveVersion, result.Bytes.Length, true)));
                return result;
            };
        }
        private static DriveChangeCursor Cursor(string? drive, int position) => new("permission-A", drive, "feed-" + position.ToString(CultureInfo.InvariantCulture));
    }
}
