using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow("dependency")]
    [DataRow("sealed-without-outbox")]
    [DataRow("uncertain-commit")]
    public async Task ReconnectedActualOwnersFinishOriginalFrozenWorkUnderItsOriginalScopeIdsAndSignature(string interruption)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture,"original-photo").ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        await using (var original = CatalogReconciler(fixture))
        await using (var preparation = await PreparationOwnerAsync(fixture,original).ConfigureAwait(false))
        {
            if (interruption == "dependency")
            {
                fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder,file,mime,bytes,hash,token) =>
                { var actual=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false); if(mime=="application/gzip") throw new HttpRequestException("Synthetic lost dependency reply before reconnect."); return actual; };
                await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
            }
            else
            {
                var prepared=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
                if(interruption=="sealed-without-outbox")
                    _=await new JournaledProtocolSigner(fixture.Setup.Accounts.State,fixture.Setup.Keys,fixture.Budget)
                        .SignCapturedCatalogAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
                else
                {
                    fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
                    { _=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false); throw new HttpRequestException("Synthetic lost signed commit reply before reconnect."); };
                    await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>original.PublishCapturedCommitAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
                }
            }
        }
        fixture.Setup.Accounts.Readers.WorkspaceCreate=create;
        var retained=await fixture.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var capture=new CatalogGenerationCaptureRepository(database,fixture.Budget).Active().Single();
            var outbox=new WorkspacePublicationRepository(database,fixture.Budget);
            var rows=outbox.Identities().Select(id=>outbox.Read(id)!).Where(item=>item.Scope.GenerationId==capture.Scope.GenerationId).ToArray();
            return(Capture:capture,Rows:rows,Intent:new ProtocolSigningJournal(database,fixture.Budget).Read(capture.CommitIntentId));
        },CancellationToken.None).ConfigureAwait(false);
        if(interruption=="sealed-without-outbox") Assert.IsFalse(retained.Rows.Any(item=>item.Id==retained.Capture.CommitIntentId));
        await AddPreparationPhotoAsync(fixture,"later-photo").ConfigureAwait(false);
        var account=await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(retained.Capture.Scope.AccountRevision,account.Revision);
        await using var current=CatalogReconciler(fixture);
        if(interruption=="sealed-without-outbox")
            await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>current.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        var captureAfter=retained.Capture;
        if(captureAfter.Phase is "capturing" or "frozen" or "publication-planned")
        {
            await using var preparation=await PreparationOwnerAsync(fixture,current).ConfigureAwait(false);
            captureAfter=(await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false)).Capture;
        }
        await using var completion=new CatalogGenerationCompletionService(fixture.Setup.Accounts.State,fixture.Budget,current);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var head=await completion.FinishAsync(captureAfter.Scope.GenerationId,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(retained.Capture.Scope.GenerationId,head.GenerationId); Assert.AreEqual(1L,head.Sequence); Assert.AreEqual(1L,head.Assets);
        await fixture.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CatalogGenerationCaptureRepository(database,fixture.Budget); var capture=journal.Read(head.GenerationId)!;
            Assert.AreEqual("retired",capture.Phase); Assert.AreEqual(retained.Capture.Scope,capture.Scope);
            Assert.AreEqual(retained.Capture.SnapshotId,capture.SnapshotId); Assert.AreEqual(retained.Capture.CommitIntentId,capture.CommitIntentId);
            Assert.AreEqual(retained.Capture.Snapshot,capture.Snapshot);
            var attempt=journal.AccessAttempt(head.GenerationId,account.Revision); Assert.IsNotNull(attempt); Assert.AreEqual(capture.Scope,attempt.Scope);
            Assert.AreEqual(account.Revision,journal.Verification(head.GenerationId)!.Observation.Context.AccountRevision);
            var outbox=new WorkspacePublicationRepository(database,fixture.Budget);
            foreach(var original in retained.Rows)
            {
                var actual=outbox.Read(original.Id)!; Assert.AreEqual(original.Scope,actual.Scope); Assert.AreEqual(original.FileId,actual.FileId);
                Assert.AreEqual(original.Sha256,actual.Sha256); CollectionAssert.AreEqual(original.Payload.ToArray(),actual.Payload.ToArray()); Assert.IsNotNull(actual.VerifiedVersion);
            }
            var signed=new ProtocolSigningJournal(database,fixture.Budget).Read(capture.CommitIntentId)!; Assert.AreEqual(1L,signed.Sequence);
            if(retained.Intent?.Envelope is { } originalEnvelope) CollectionAssert.AreEqual(originalEnvelope.ToArray(),signed.Envelope!.Value.ToArray());
            Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>new LibraryAuthorityRepository(database).AssertLocalPublicationScope(capture.Scope));
            return true;
        },CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReconnectedEngineCompletesItsOriginalSnapshotThenCatchesUpLaterImportsWithoutDuplicateGenerations()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture,"original-photo").ConfigureAwait(false); var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
        { var stored=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false); if(mime=="application/gzip") throw new HttpRequestException("Synthetic interrupted old engine."); return stored; };
        await using(var original=CatalogReconciler(fixture))
        await using(var preparation=await PreparationOwnerAsync(fixture,original).ConfigureAwait(false))
            await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var old=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Budget).Active().Single(),CancellationToken.None).ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.WorkspaceCreate=create;
        await AddPreparationPhotoAsync(fixture,"later-photo").ConfigureAwait(false);
        await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        await using var current=CatalogReconciler(fixture);
        await using var engine=await GenerationRuntimeAsync(fixture,current).ConfigureAwait(false);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90)); var head=await engine.RunUntilCurrentAsync(deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(2L,head.Sequence); Assert.AreEqual(2L,head.Assets); var allocations=fixture.Allocations;
        Assert.AreEqual(head,await engine.RunUntilCurrentAsync(deadline.Token).ConfigureAwait(false)); Assert.AreEqual(allocations,fixture.Allocations);
        await fixture.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CatalogGenerationCaptureRepository(database,fixture.Budget); var first=journal.Read(old.Scope.GenerationId)!;
            Assert.AreEqual("retired",first.Phase); Assert.AreEqual(old.Scope,first.Scope); Assert.AreEqual(old.SnapshotId,first.SnapshotId);
            Assert.AreEqual(1L,journal.Verification(old.Scope.GenerationId)!.Assets); Assert.AreEqual(1L,journal.Verification(old.Scope.GenerationId)!.Sequence);
            Assert.IsNull(journal.Active().SingleOrDefault()); return true;
        },CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ReconnectedCommitExitJoinsActualLateIoBeforeRetiringLocalAccessAndRejectsItsReceipt()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current=CatalogReconciler(fixture);
        await using var preparation=await PreparationOwnerAsync(fixture,current).ConfigureAwait(false);
        var prepared=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        _=await current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        var create=fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,hash,token)=>
        { var actual=await create(folder,file,mime,bytes,hash,token).ConfigureAwait(false); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return actual; };
        var pending=current.PublishCapturedCommitAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None); Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping=current.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(current.ExitConfirmed); Assert.AreEqual(prepared.Capture.Scope.LibraryId,fixture.Setup.Keys.PublicIdentity.LibraryId);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(()=>pending).ConfigureAwait(false); await stopping.ConfigureAwait(false); Assert.IsTrue(current.ExitConfirmed);
        await fixture.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CatalogGenerationCaptureRepository(database,fixture.Budget);
            Assert.AreEqual("commit-sealed",journal.Read(prepared.Capture.Scope.GenerationId)!.Phase); Assert.IsNull(journal.PublishedHead());
            var work=new WorkspacePublicationRepository(database,fixture.Budget).Read(prepared.Capture.CommitIntentId)!;
            Assert.IsNotNull(work.FileId); Assert.IsNull(work.VerifiedVersion); Assert.AreEqual(prepared.Capture.Scope,work.Scope); return true;
        },CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RenewedReadRecoveryCannotGrandfatherAnotherNativeSealedIntentWithoutItsOutbox()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current=CatalogReconciler(fixture);
        await using var preparation=await PreparationOwnerAsync(fixture,current).ConfigureAwait(false);
        var prepared=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var foreign=await new JournaledProtocolSigner(fixture.Setup.Accounts.State,fixture.Setup.Keys,fixture.Budget)
            .SignCatalogAsync(Guid.NewGuid(),(_,sequence)=>prepared.Plan!.EncodeBody(prepared.Capture.Scope,sequence),CancellationToken.None).ConfigureAwait(false);
        var account=await fixture.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(current.Current);
        await fixture.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new CatalogGenerationCaptureRepository(database,fixture.Budget);
            Assert.IsNull(journal.AccessAttempt(prepared.Capture.Scope.GenerationId,account.Revision));
            Assert.AreEqual(prepared,journal.Publication(prepared.Capture.Scope.GenerationId));
            Assert.AreEqual(foreign.EnvelopeSha256,new ProtocolSigningJournal(database,fixture.Budget).Read(foreign.Id)!.EnvelopeSha256); return true;
        },CancellationToken.None).ConfigureAwait(false);
    }
}
