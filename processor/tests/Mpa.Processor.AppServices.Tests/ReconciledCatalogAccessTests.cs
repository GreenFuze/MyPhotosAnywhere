using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task ActualRenewedAccessJournalsOriginalPreparedWorkWithoutRemintingOrGrantingAnUpload()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false); var before = (fixture.Creates, fixture.Allocations);
        var account = await fixture.Setup.Accounts.Service.ConnectAsync("permission-A", new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        var attempt = await current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared.Capture.Scope, attempt.Scope); Assert.AreEqual(account.Revision, attempt.AccountRevision);
        Assert.AreEqual(prepared.Capture.SnapshotId, attempt.SnapshotId); Assert.AreEqual(prepared.Capture.CommitIntentId, attempt.CommitIntentId);
        Assert.IsNull(current.Current); Assert.AreEqual(before.Creates, fixture.Creates); Assert.AreEqual(before.Allocations, fixture.Allocations);
        Assert.AreEqual(attempt, await current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false));
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new CatalogGenerationCaptureRepository(database, fixture.Budget);
            Assert.AreEqual(prepared, journal.Publication(prepared.Capture.Scope.GenerationId));
            Assert.AreEqual(attempt, journal.AccessAttempt(prepared.Capture.Scope.GenerationId, account.Revision));
            Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => new LibraryAuthorityRepository(database).AssertLocalPublicationScope(prepared.Capture.Scope));
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var metadata = fixture.Setup.Accounts.Readers.MetadataRead!;
        fixture.Setup.Accounts.Readers.MetadataRead = (_, _, _) => throw new HttpRequestException("Synthetic actual renewed-read outage.");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(current.Current); fixture.Setup.Accounts.Readers.MetadataRead = metadata;
        Assert.AreEqual(attempt, await current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RenewedAccessRetainsAnUncertainOriginalCommitAndItsExactOwnVisibleHead()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder,file,mime,bytes,hash,token) =>
        { _ = await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false); throw new HttpRequestException("Synthetic uncertain actual commit."); };
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => current.PublishCapturedCommitAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.WorkspaceCreate = create; var before = (fixture.Creates,fixture.Allocations);
        var pending = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database,fixture.Budget)
            .Read(prepared.Capture.CommitIntentId)!,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pending.FileId); Assert.IsNull(pending.VerifiedVersion);
        await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        var attempt = await current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared.Capture.Scope,attempt.Scope); Assert.AreEqual(before.Creates,fixture.Creates); Assert.AreEqual(before.Allocations,fixture.Allocations);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspacePublicationRepository(database,fixture.Budget)
            .Read(prepared.Capture.CommitIntentId)!,CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(pending.Payload.ToArray(),retained.Payload.ToArray());
        Assert.AreEqual(pending with { Payload = retained.Payload },retained);
    }

    [TestMethod]
    public async Task RenewedAccessCannotAdoptAForeignHeadOverTheOriginalCapturedParent()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture,current).ConfigureAwait(false);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await AddCatalogHeadAsync(fixture,"foreign-later-head").ConfigureAwait(false);
        await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(() => current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(current.Current);
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database,fixture.Budget)
            .AccessAttempt(prepared.Capture.Scope.GenerationId,2),CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RenewedAccessCannotReplaceAMissingOriginalWithAnIdenticalVisibleCopy(bool committed)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture,current).ConfigureAwait(false);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var original = committed ? await current.PublishCapturedCommitAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false) : prepared.Plan!.RootIndex;
        fixture.Cloud.Add("a-identical-access-copy",fixture.Cloud[original.FileId] with { FileId="a-identical-access-copy" });
        fixture.MetadataMimeTypes.Add("a-identical-access-copy",fixture.MetadataMimeTypes[original.FileId]); fixture.Cloud.Remove(original.FileId);
        var account = await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(() => current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(current.Current);
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database,fixture.Budget)
            .AccessAttempt(prepared.Capture.Scope.GenerationId,account.Revision),CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RenewedAccessExitJoinsTheActualUncooperativeReadAndRejectsItsLateAttemptCheckpoint()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture,current).ConfigureAwait(false);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var account = await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        var metadata = fixture.Setup.Accounts.Readers.MetadataRead!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.MetadataRead = async (folder,candidate,token) =>
        { var result = await metadata(folder,candidate,token).ConfigureAwait(false); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return result; };
        var pending = current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None); Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = current.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(current.ExitConfirmed);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false); await stopping.ConfigureAwait(false);
        Assert.IsTrue(current.ExitConfirmed);
        Assert.IsNull(await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database,fixture.Budget)
            .AccessAttempt(prepared.Capture.Scope.GenerationId,account.Revision),CancellationToken.None).ConfigureAwait(false));
    }
}
