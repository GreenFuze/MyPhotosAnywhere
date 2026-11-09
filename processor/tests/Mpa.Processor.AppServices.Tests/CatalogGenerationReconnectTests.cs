using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task SuccessfulHumanReconnectWakesAccessAttentionWhileBackgroundProgressAndForeignConnectionsCannot()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var stored = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            if (mime == "application/gzip") throw new HttpRequestException("Synthetic expired authorization after actual create.", null, System.Net.HttpStatusCode.Unauthorized);
            return stored;
        };
        var current = CatalogReconciler(fixture); var generations = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false);
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, current, generations);
        runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        var failure = runtime.LastAttemptFailure; Assert.IsNotNull(failure); Assert.IsNull(runtime.Failure);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage);
        var original = await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, fixture.Budget).Active().Single(), CancellationToken.None).ConfigureAwait(false);
        var before = (fixture.Allocations, fixture.Creates);
        runtime.NotifyLocalCatalogChanged();
        var account = await fixture.Setup.Accounts.Service.ConnectAsync("permission-A", new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        runtime.NotifyAccountConnected(account with { PermissionId = "unrelated-account" });
        Assert.AreSame(failure, runtime.LastAttemptFailure); Assert.AreEqual(before, (fixture.Allocations, fixture.Creates));
        fixture.Setup.Accounts.Readers.WorkspaceCreate = create;
        runtime.NotifyAccountConnected(account);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        Assert.IsNull(runtime.LastAttemptFailure); Assert.IsNull(runtime.Failure);
        // The old capture completes first. A second generation reports this source's
        // renewed account revision/pending scan without changing its original photos.
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage); Assert.AreEqual(2L, runtime.CurrentCatalog!.Sequence);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new CatalogGenerationCaptureRepository(database, fixture.Budget); var actual = journal.Read(original.Scope.GenerationId)!;
            Assert.AreEqual("retired", actual.Phase); Assert.AreEqual(original.Scope, actual.Scope); Assert.AreEqual(original.SnapshotId, actual.SnapshotId);
            var receipt = journal.Verification(actual.Scope.GenerationId)!;
            Assert.AreEqual(1L, receipt.Sequence); Assert.AreEqual(account.Revision, receipt.Observation.Context.AccountRevision); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        var allocations = fixture.Allocations;
        runtime.NotifyAccountConnected(account); runtime.NotifyAccountConnected(account);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.IsNull(runtime.LastAttemptFailure); Assert.AreEqual(2L, runtime.CurrentCatalog!.Sequence); Assert.AreEqual(allocations, fixture.Allocations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HumanReconnectDoesNotDismissSharingAttentionOrRestartAnInternalInvariant(bool invariant)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        var current = CatalogReconciler(fixture); var generations = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false);
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, current, generations);
        Exception error = invariant ? new InvalidOperationException("Synthetic internal invariant.") : new LibraryWorkspaceApprovalException("Synthetic sharing review required.");
        fixture.Setup.Accounts.Readers.WorkspaceInspect = (_, _) => throw error;
        runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.AreSame(error, runtime.LastAttemptFailure);
        var before = (fixture.Allocations, fixture.Creates);
        var account = await fixture.Setup.Accounts.Service.ConnectAsync("permission-A", new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        runtime.NotifyAccountConnected(account);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention, runtime.Activity.Stage); Assert.AreSame(error, runtime.LastAttemptFailure);
        Assert.AreEqual(invariant, runtime.Failure is not null); Assert.AreEqual(before, (fixture.Allocations, fixture.Creates));
    }

    [TestMethod]
    [DataRow("allocation")]
    [DataRow("create")]
    [DataRow("readback")]
    public async Task AnotherReconnectDuringActualPublicationRetainsOriginalAllocationAndRejectsLateAdmission(string stage)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        var prepared = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var first = await fixture.Setup.Accounts.Service.ConnectAsync("permission-A", new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        _ = await current.ReconcileRenewedAccessAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allocate = fixture.Setup.Accounts.Readers.WorkspaceAllocate!; var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        var read = fixture.Setup.Accounts.Readers.WorkspaceRead!; string? allocated = null;
        fixture.Setup.Accounts.Readers.WorkspaceAllocate = async token =>
        { allocated = await allocate(token).ConfigureAwait(false); if (stage == "allocation") { entered.TrySetResult(); await release.Task.ConfigureAwait(false); } return allocated; };
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        { var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false); if (stage == "create") { entered.TrySetResult(); await release.Task.ConfigureAwait(false); } return result; };
        fixture.Setup.Accounts.Readers.WorkspaceRead = async (folder, file, bytes, hash, token) =>
        {
            var result = await read(folder, file, bytes, hash, token).ConfigureAwait(false);
            if (stage == "readback" && file == allocated) { entered.TrySetResult(); await release.Task.ConfigureAwait(false); }
            return result;
        };
        var pending = current.PublishCapturedCommitAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None); DriveAccountRecord second;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            second = await fixture.Setup.Accounts.Service.ConnectAsync("permission-A", new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(second.Revision > first.Revision); Assert.IsFalse(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => pending).ConfigureAwait(false); Assert.IsNull(current.Current);
        var original = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new CatalogGenerationCaptureRepository(database, fixture.Budget); Assert.IsNull(journal.PublishedHead());
            var work = new WorkspacePublicationRepository(database, fixture.Budget).Read(prepared.Capture.CommitIntentId)!;
            Assert.AreEqual(allocated, work.FileId); Assert.AreEqual(prepared.Capture.Scope, work.Scope);
            Assert.AreEqual(stage == "readback", work.VerifiedVersion is not null);
            Assert.IsNull(journal.AccessAttempt(prepared.Capture.Scope.GenerationId, second.Revision));
            return work;
        }, CancellationToken.None).ConfigureAwait(false);
        fixture.Setup.Accounts.Readers.WorkspaceAllocate = allocate; fixture.Setup.Accounts.Readers.WorkspaceCreate = create; fixture.Setup.Accounts.Readers.WorkspaceRead = read;
        var allocations = fixture.Allocations;
        await using var completion = new CatalogGenerationCompletionService(fixture.Setup.Accounts.State, fixture.Budget, current);
        var head = await completion.FinishAsync(prepared.Capture.Scope.GenerationId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, head.Sequence); Assert.AreEqual(original.FileId, head.Commit.FileId); Assert.AreEqual(allocations, fixture.Allocations);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new CatalogGenerationCaptureRepository(database, fixture.Budget);
            Assert.AreEqual(prepared.Capture.Scope, journal.Read(head.GenerationId)!.Scope);
            Assert.IsNotNull(journal.AccessAttempt(head.GenerationId, first.Revision)); Assert.IsNotNull(journal.AccessAttempt(head.GenerationId, second.Revision));
            CollectionAssert.AreEqual(original.Payload.ToArray(), new WorkspacePublicationRepository(database, fixture.Budget).Read(original.Id)!.Payload.ToArray()); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

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
