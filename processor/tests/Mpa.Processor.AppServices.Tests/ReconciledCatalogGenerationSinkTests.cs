using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentGenerationDependencyUsesActualReconciliationReadbackAndExactRetryIdentity()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var reads = ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope);
        var reference = await sink.StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.AreEqual(3, fixture.Creates); Assert.AreEqual(3, fixture.Allocations); Assert.AreEqual(3, owner.Current!.Observation.Documents);
        var stored = await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false);
        Assert.AreEqual(generation.Capture.Scope, stored.Scope); Assert.AreEqual(reference.FileId, stored.FileId); Assert.IsNotNull(stored.VerifiedVersion);
        var before = reads.Count;
        Assert.AreEqual(reference, await sink.StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(3, fixture.Creates); Assert.AreEqual(3, fixture.Allocations); Assert.IsTrue(reads.Count > before);
        Assert.IsNull(owner.Failure); Assert.AreEqual(generation, await fixture.Setup.Accounts.State.ExecuteAsync(db => new CatalogGenerationCaptureRepository(db, fixture.Budget)
            .Publication(generation.Capture.Scope.GenerationId), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CallerCannotReplaceBytesWhileTheActualCurrentScanIsAwaited()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventory = fixture.Setup.Accounts.Readers.MetadataInventory!;
        fixture.Setup.Accounts.Readers.MetadataInventory = async (folder, drive, page, token) =>
        { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return await inventory(folder, drive, page, token).ConfigureAwait(false); };
        var bytes = "{\"n\":1}"u8.ToArray(); var original = bytes.ToArray();
        var publishing = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope).StoreAsync("facts-index", "application/json", bytes, CancellationToken.None);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); bytes[5] = (byte)'2'; }
        finally { release.TrySetResult(); }
        var result = await publishing.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        CollectionAssert.AreEqual(original, fixture.Cloud[result.FileId].Bytes.ToArray());
    }

    [TestMethod]
    public async Task AnUnrelatedOutboxMutationCannotBeHiddenAmongTheAdmittedAllocationTransitions()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var foreign = Guid.NewGuid(); var allocate = fixture.Setup.Accounts.Readers.WorkspaceAllocate!;
        fixture.Setup.Accounts.Readers.WorkspaceAllocate = async token =>
        {
            await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspacePublicationRepository(db, fixture.Budget)
                .Prepare(foreign, generation.Capture.Scope with { GenerationId = Guid.NewGuid() }, "facts-index", "application/json", "{}"u8), token).ConfigureAwait(false);
            return await allocate(token).ConfigureAwait(false);
        };
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope);
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(() => sink.StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(3, fixture.Allocations); Assert.IsNull(owner.Current);
        var retained = await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false);
        Assert.IsNotNull(retained.FileId); Assert.IsNull(retained.VerifiedVersion); Assert.AreEqual(generation.Capture.Scope, retained.Scope);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate = allocate;
        await Assert.ThrowsExactlyAsync<CatalogGenerationInProgressException>(() => sink.StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3, fixture.Allocations); Assert.AreEqual(retained.FileId, (await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false)).FileId);
        Assert.IsNotNull(await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspacePublicationRepository(db, fixture.Budget).Read(foreign), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LostActualCreateReplyResumesTheOriginalAllocatedObjectWithoutReconciliationDeadlock()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        { _ = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false); throw new HttpRequestException("Synthetic lost dependency completion after actual storage."); };
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => sink.StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        var pending = await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId); Assert.IsNull(pending.VerifiedVersion); Assert.IsTrue(fixture.Cloud.ContainsKey(pending.FileId));
        fixture.Setup.Accounts.Readers.WorkspaceCreate = create;
        var reference = await sink.StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.AreEqual(pending.FileId, reference.FileId); Assert.AreEqual(3, fixture.Allocations); Assert.AreEqual(3, fixture.Cloud.Count);
        Assert.IsNotNull((await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false)).VerifiedVersion);
    }

    [TestMethod]
    public async Task ExitJoinsAnUncooperativeActualDependencyCreateAndCannotAcceptItsLateReply()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        { var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return result; };
        var publishing = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope).StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None);
        Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); stopping = owner.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(owner.ExitConfirmed); Assert.IsNotNull(fixture.Setup.Keys.PublicIdentity);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => publishing).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(owner.ExitConfirmed); Assert.IsNull(owner.Current);
        var pending = await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId); Assert.IsNull(pending.VerifiedVersion); Assert.AreEqual(3, fixture.Cloud.Count);
    }

    [TestMethod]
    public async Task ACallerScopeWithoutItsDurableBasisAndOriginalRolesCannotGrantAnUpload()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var forged = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope with { GenerationId = Guid.NewGuid() });
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(() => forged.StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        var sink = new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => sink.StoreAsync("catalog-commit", "application/json", "{}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => sink.StoreAsync("facts-index", "application/json", "{ \"spaced\" : true }"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(2, fixture.Allocations);
    }

    private static List<string> ConfigureDependencyWorkspace(InitialPublicationFixture fixture)
    {
        var reads = ConfigureCatalogMetadata(fixture);
        fixture.Setup.Accounts.Readers.WorkspaceRead = (folder, file, bytes, hash, token) =>
        {
            token.ThrowIfCancellationRequested(); Assert.AreEqual("metadata", folder); var result = fixture.Cloud[file];
            Assert.AreEqual(bytes, result.Bytes.Length); Assert.AreEqual(hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(result.Bytes.Span)));
            reads.Add(file); return Task.FromResult(result);
        };
        return reads;
    }

    [TestMethod]
    public async Task SourcePolicyChangeDuringAllocationClosesTheGrantWithoutLosingItsCapturedId()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var allocate = fixture.Setup.Accounts.Readers.WorkspaceAllocate!;
        fixture.Setup.Accounts.Readers.WorkspaceAllocate = async token =>
        {
            await fixture.Setup.Accounts.State.ExecuteAsync(db =>
            {
                var sources = new LibrarySourceRepository(db); var selected = sources.Snapshot(); var source = selected.Sources.Single();
                return sources.Update(source.Id, false, source.IncludeDescendants, source.Revision, selected.Revision);
            }, token).ConfigureAwait(false);
            return await allocate(token).ConfigureAwait(false);
        };
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope)
            .StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(3, fixture.Allocations); Assert.IsNull(owner.Current);
        var pending = await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId); Assert.IsNull(pending.VerifiedVersion); Assert.AreEqual(generation.Capture.Scope, pending.Scope);
    }

    [TestMethod]
    public async Task IdenticalSignedHeadCopyCannotRetargetTheOriginallyCapturedParent()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var original = await AddCatalogHeadAsync(fixture, "z-original-head").ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        Assert.AreEqual(original.Reference, generation.Basis.Parent);
        fixture.Cloud.Add("a-identical-head", fixture.Cloud[original.Reference.FileId] with { FileId = "a-identical-head" });
        var result = await new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope)
            .StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.IsNotNull(result); Assert.AreEqual("a-identical-head", owner.Current!.Parent!.FileId);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(db => new CatalogGenerationCaptureRepository(db, fixture.Budget)
            .Publication(generation.Capture.Scope.GenerationId), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(original.Reference, retained!.Basis.Parent); Assert.AreEqual(generation, retained);
    }

    [TestMethod]
    public async Task ActualNewSignedHeadDuringCreateInvalidatesTheOldPrivateAdmission()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var stored = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            _ = await AddCatalogHeadAsync(fixture, "actual-new-head").ConfigureAwait(false); return stored;
        };
        await Assert.ThrowsExactlyAsync<ProtocolSigningInventoryChangedException>(() => new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope)
            .StoreAsync("facts-index", "application/json", "{}"u8.ToArray(), CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(owner.Current);
        var pending = await GenerationPublicationAsync(fixture, generation.Capture.Scope.GenerationId).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId); Assert.IsNull(pending.VerifiedVersion); Assert.IsTrue(fixture.Cloud.ContainsKey(pending.FileId));
    }

    [TestMethod]
    public async Task CompressedDependencyRetainsItsActualEncodingThroughPostWriteReconciliation()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureDependencyWorkspace(fixture);
        await using var owner = CatalogReconciler(fixture); var generation = await CaptureDependencyBasisAsync(fixture, owner).ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.WorkspaceCreate = (folder, file, mime, bytes, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Assert.AreEqual("application/gzip", mime); Assert.AreEqual("metadata", folder); fixture.Creates++;
            var result = new Mpa.Processor.GoogleDrive.DriveWorkspaceObject("permission-A", file, folder, 7, bytes.ToArray());
            fixture.Cloud.Add(file, result); fixture.MetadataMimeTypes.Add(file, mime); return Task.FromResult(result);
        };
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write("{}"u8);
        var result = await new ReconciledCatalogGenerationSink(owner, generation.Capture.Scope)
            .StoreAsync("facts-chunk", "application/gzip", compressed.ToArray(), CancellationToken.None).ConfigureAwait(false);
        var observed = await fixture.Setup.Accounts.State.ExecuteAsync(db => new WorkspaceObservationRepository(db, fixture.Budget)
            .Known(owner.Current!.Observation.Context).Single(item => item.FileId == result.FileId), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("application/gzip", observed.MimeType); Assert.AreEqual(result.Sha256, observed.Sha256); Assert.AreEqual(3, fixture.Creates);
    }

    private static async Task<CapturedCatalogPublication> CaptureDependencyBasisAsync(InitialPublicationFixture fixture, LocalCatalogReconciliationService owner)
    {
        var read = await owner.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        return await fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var authority = read.Authority; var scope = new WorkspacePublicationScope(authority.LibraryId, authority.ActorId, authority.ActorEpoch, Guid.NewGuid(), authority.AccountId,
                authority.AccountRevision, authority.FolderId, authority.ConfigurationRevision, authority.AuthoritySha256);
            var journal = new CatalogGenerationCaptureRepository(db, fixture.Budget); var capture = journal.Begin(scope, Guid.NewGuid(), Guid.NewGuid());
            // Admission fixture only: real native retained-file lifetime is covered by
            // Core tests. This does not pretend to capture/activate a complete generation.
            capture = journal.SealSnapshot(scope.GenerationId, new(capture.SnapshotId, scope.LibraryId, scope.ConfigurationRevision, 1, 0, 8192, new string('a', 64)));
            var approved = new LibraryAuthorityRepository(db).CurrentSourceConfiguration()!;
            return journal.CapturePublicationBasis(scope.GenerationId, new(capture.Snapshot!, read.Parent, read.Sequence, approved.EnvelopeSha256, 0, 1));
        }, CancellationToken.None).ConfigureAwait(false);
    }
    private static Task<WorkspacePublicationRecord> GenerationPublicationAsync(InitialPublicationFixture fixture, Guid generation) =>
        fixture.Setup.Accounts.State.ExecuteAsync(db =>
        {
            var outbox = new WorkspacePublicationRepository(db, fixture.Budget);
            return outbox.Identities().Select(id => outbox.Read(id)!).Single(work => work.Scope.GenerationId == generation);
        }, CancellationToken.None);
}
