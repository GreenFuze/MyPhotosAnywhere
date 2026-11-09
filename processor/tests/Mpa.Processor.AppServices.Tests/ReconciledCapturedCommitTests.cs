using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CapturedCommitPublishesAnActualSignedCoveredHeadAndRetainsItsIdentityOnRetry(bool hasPredecessor)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        if (hasPredecessor) _ = await AddCatalogHeadAsync(fixture, "original-predecessor").ConfigureAwait(false);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var reference = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Assert.AreEqual(reference, current.Current!.Parent); Assert.AreEqual(hasPredecessor ? 2L : 1L, current.Current.Sequence); Assert.AreEqual(1L, current.Current.Assets);
        var before = (fixture.Creates, fixture.Allocations);
        Assert.AreEqual(reference, await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(before.Creates, fixture.Creates); Assert.AreEqual(before.Allocations, fixture.Allocations);
        await AssertCapturedCommitJournalAsync(fixture, ready, reference, hasPredecessor ? 2 : 1).ConfigureAwait(false); Assert.IsNull(current.Failure);
    }

    [TestMethod]
    public async Task SealedCommitWithoutItsOutboxCheckpointRecoversTheOriginalEnvelopeAndCounter()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var sealedIntent = await new JournaledProtocolSigner(fixture.Setup.Accounts.State, fixture.Setup.Keys, fixture.Budget)
            .SignCapturedCatalogAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Read(ready.Capture.CommitIntentId), CancellationToken.None).ConfigureAwait(false));
        var reference = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var work = await AssertCapturedCommitJournalAsync(fixture, ready, reference).ConfigureAwait(false);
        CollectionAssert.AreEqual(sealedIntent.Envelope!.Value.ToArray(), work.Payload.ToArray()); Assert.AreEqual(1L, current.Current!.Sequence);
    }

    [TestMethod]
    public async Task LostActualCommitReplyResumesOnlyItsOriginalAllocatedObjectAndOwnVisibleHead()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        { _ = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false); throw new HttpRequestException("Synthetic lost commit reply after actual storage."); };
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        var pending = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Read(ready.Capture.CommitIntentId)!, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId); Assert.IsNull(pending.VerifiedVersion); Assert.IsTrue(fixture.Cloud.ContainsKey(pending.FileId)); Assert.IsNull(current.Current);
        var allocated = fixture.Allocations; fixture.Setup.Accounts.Readers.WorkspaceCreate = create;
        var reference = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Assert.AreEqual(pending.FileId, reference.FileId); Assert.AreEqual(allocated, fixture.Allocations); Assert.AreEqual(fixture.Allocations, fixture.Cloud.Count);
        await AssertCapturedCommitJournalAsync(fixture, ready, reference).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnsealedOriginalCommitIntentIsReconciledThenResumedWithoutAnotherReservation()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var reserved = await fixture.Setup.Accounts.State.ExecuteAsync(database => new ProtocolSigningJournal(database, fixture.Budget)
            .Reserve(ready.Capture.CommitIntentId, ready.Capture.Scope.ActorId, ready.Capture.Scope.ActorEpoch, "catalog-commit",
                sequence => ready.Plan!.EncodeBody(ready.Capture.Scope, sequence)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(reserved.Envelope);
        var reference = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        await AssertCapturedCommitJournalAsync(fixture, ready, reference).ConfigureAwait(false);
        var actual = await fixture.Setup.Accounts.State.ExecuteAsync(database => new ProtocolSigningJournal(database, fixture.Budget)
            .Read(reserved.Id)!, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(reserved.Body.ToArray(), actual.Body.ToArray()); Assert.AreEqual(reserved.Sequence, actual.Sequence);
    }

    [TestMethod]
    public async Task AnIdenticalVisibleCommitCopyCannotRetargetTheOriginalAllocatedCommitIdentity()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var original = await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        fixture.Cloud.Add("a-identical-commit", fixture.Cloud[original.FileId] with { FileId = "a-identical-commit" });
        fixture.MetadataMimeTypes.Add("a-identical-commit", "application/json"); var allocated = fixture.Allocations;
        Assert.AreEqual(original, await current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("a-identical-commit", current.Current!.Parent!.FileId); Assert.AreEqual(original.Sha256, current.Current.Parent.Sha256);
        Assert.AreEqual(allocated, fixture.Allocations); await AssertCapturedCommitJournalAsync(fixture, ready, original).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AVisibleEnvelopeCopyCannotReplaceALostOriginalAllocatedCommitFile()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        { _ = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false); throw new HttpRequestException("Synthetic lost original commit receipt."); };
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        var pending = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Read(ready.Capture.CommitIntentId)!, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId);
        var stored = fixture.Cloud[pending.FileId]; fixture.Cloud.Remove(pending.FileId);
        fixture.Cloud.Add("identical-copy-only", stored with { FileId = "identical-copy-only" }); fixture.MetadataMimeTypes.Add("identical-copy-only", "application/json");
        fixture.Setup.Accounts.Readers.WorkspaceCreate = create; var allocated = fixture.Allocations;
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(allocated, fixture.Allocations); Assert.IsNull(current.Current);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, fixture.Budget)
            .Read(pending.Id)!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.FileId, retained.FileId); Assert.IsNull(retained.VerifiedVersion); Assert.AreEqual(pending.Scope, retained.Scope);
    }

    [TestMethod]
    public async Task CommitExitJoinsTheActualUncooperativeReadbackBeforeRetiringNativeAndStateAccess()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var read = fixture.Setup.Accounts.Readers.WorkspaceRead!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceRead = async (folder, file, bytes, hash, token) =>
        { var actual = await read(folder, file, bytes, hash, token).ConfigureAwait(false); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return actual; };
        var publishing = current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None); Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = current.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(current.ExitConfirmed); Assert.IsNotNull(fixture.Setup.Keys.PublicIdentity);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => publishing).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(current.ExitConfirmed); Assert.IsNull(current.Current);
        var capture = await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, fixture.Budget)
            .Read(ready.Capture.Scope.GenerationId)!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("commit-sealed", capture.Phase); Assert.AreEqual(ready.Capture.SnapshotId, capture.SnapshotId);
    }

    [TestMethod]
    public async Task ANewActualHeadCannotReplaceTheCapturedParentOrBurnAnotherSigningSequence()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var ready = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await AddCatalogHeadAsync(fixture, "another-actual-head").ConfigureAwait(false); var allocated = fixture.Allocations;
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(() => current.PublishCapturedCommitAsync(ready.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(allocated, fixture.Allocations); Assert.IsNull(current.Current);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var signing = new ProtocolSigningJournal(database, fixture.Budget); Assert.IsNull(signing.Read(ready.Capture.CommitIntentId));
            Assert.AreEqual(1L, signing.Inventory(ready.Capture.Scope.LibraryId).Actors.Single(actor => actor.ActorId == ready.Capture.Scope.ActorId).Sequence);
            Assert.AreEqual(ready, new CatalogGenerationCaptureRepository(database, fixture.Budget).Publication(ready.Capture.Scope.GenerationId)); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task<WorkspacePublicationRecord> AssertCapturedCommitJournalAsync(InitialPublicationFixture fixture, CapturedCatalogPublication ready, CatalogObjectReference reference, long expectedSequence = 1) =>
        fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var publication = new CatalogGenerationCaptureRepository(database, fixture.Budget).Publication(ready.Capture.Scope.GenerationId)!;
            Assert.AreEqual("commit-sealed", publication.Capture.Phase); Assert.AreEqual(ready.Plan, publication.Plan);
            var intent = new ProtocolSigningJournal(database, fixture.Budget).Read(ready.Capture.CommitIntentId)!;
            Assert.AreEqual(ready.Capture.CommitIntentId, intent.Id); Assert.AreEqual(expectedSequence, intent.Sequence);
            var work = new WorkspacePublicationRepository(database, fixture.Budget).Read(intent.Id)!;
            Assert.AreEqual(ready.Capture.Scope, work.Scope); Assert.AreEqual("catalog-commit", work.Role); Assert.AreEqual(reference.FileId, work.FileId);
            Assert.IsNotNull(work.VerifiedVersion); Assert.AreEqual(reference.Sha256, work.Sha256); CollectionAssert.AreEqual(intent.Envelope!.Value.ToArray(), work.Payload.ToArray());
            return work;
        }, CancellationToken.None);
}
