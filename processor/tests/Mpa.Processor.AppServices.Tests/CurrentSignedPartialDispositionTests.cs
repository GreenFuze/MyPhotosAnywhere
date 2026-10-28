using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentSignedPartialKeepsEveryPendingIdentityAndRetiresOnlyItsPrivateSnapshot(bool allocated)
    {
        await using var owned=await SignedPartialFixture.CreateAsync(allocated).ConfigureAwait(false);
        var fixture=owned.Header.Fixture;var generation=owned.Header.Pending.Capture.Scope.GenerationId;
        var before=await File.ReadAllBytesAsync(owned.Header.Pending.Path).ConfigureAwait(false);
        var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;var puts=fixture.Http.Puts;
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>fixture.Reader.InspectMaterializedPredecessorAsync(
            owned.Inventory,generation,CancellationToken.None)).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeSignedPartialGenerationAsync(owned.Inventory,generation,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Header.Head.Commit,receipt.SelectedAnchor);Assert.AreEqual(2L,receipt.Sequence);
        Assert.AreEqual(owned.Header.Pending.Capture.Scope,receipt.Scope);
        Assert.AreEqual("mpa-current-signed-partial-disposition",receipt.Format);
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Supersession(generation),CancellationToken.None).ConfigureAwait(false));
        CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(owned.Header.Pending.Path).ConfigureAwait(false));
        await AssertSignedPartialOriginalAsync(owned).ConfigureAwait(false);
        var pending=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget).Pending(),CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(pending.Contains(owned.Extra.Id));
        await EnableSignedPartialSourceAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {new WorkspacePublicationRepository(database,fixture.Data.Budget).AssertCurrent(owned.Extra.Id);return true;},CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogGenerationCaptureConflictException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            new WorkspacePublicationRepository(database,fixture.Data.Budget).AssignFileId(owned.Extra.Id,owned.Extra.Sha256,owned.Extra.FileId ?? "late-partial-id"),CancellationToken.None)).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.Service.DisconnectAsync("permission-A",owned.Inventory.Checkpoint.Context.AccountRevision,CancellationToken.None).ConfigureAwait(false);
        await using var retirement=new CurrentSignedPartialRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(receipt,await retirement.FinishAsync(generation,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(receipt,await retirement.FinishAsync(generation,CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(owned.Header.Pending.Path));Assert.AreEqual(owned.Header.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        await AssertSignedPartialOriginalAsync(owned).ConfigureAwait(false);
        Assert.AreEqual(creates,fixture.Data.Creates);Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CurrentSignedPartialRollsBackItsPairedReceiptOnPhaseRecordOrHumanCorruption(int mode)
    {
        var dropSql=mode switch
        {
            0=>"DROP TRIGGER reject_signed_partial",1=>"DROP TRIGGER corrupt_signed_partial",2=>"DROP TRIGGER change_signed_partial_human",
            _=>throw new ArgumentOutOfRangeException(nameof(mode))
        };
        await using var owned=await SignedPartialFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Header.Fixture;
        var generation=owned.Header.Pending.Capture.Scope.GenerationId;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,mode switch
            {
                0=>"CREATE TRIGGER reject_signed_partial BEFORE UPDATE OF phase ON catalog_generation_capture WHEN NEW.phase='complete' BEGIN SELECT RAISE(ABORT,'partial phase atomicity'); END",
                1=>"CREATE TRIGGER corrupt_signed_partial AFTER INSERT ON processor_meta WHEN NEW.key GLOB 'current_signed_partial_disposition_v1:*' AND NEW.key NOT GLOB '*:sha256' BEGIN UPDATE processor_meta SET value='{}' WHERE key=NEW.key; END",
                2=>"CREATE TRIGGER change_signed_partial_human AFTER UPDATE OF phase ON catalog_generation_capture WHEN NEW.phase='complete' BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END",
                _=>throw new InvalidOperationException("Unknown partial rollback fixture case.")
            });return true;
        },CancellationToken.None).ConfigureAwait(false);
        try
        {
            if(mode==1) await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Reader.DisposeSignedPartialGenerationAsync(owned.Inventory,generation,CancellationToken.None)).ConfigureAwait(false);
            else if(mode==2) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.DisposeSignedPartialGenerationAsync(owned.Inventory,generation,CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.DisposeSignedPartialGenerationAsync(owned.Inventory,generation,CancellationToken.None)).ConfigureAwait(false);
            Assert.IsNull(await ReadSignedPartialAsync(owned).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Header.Pending.Path));
            Assert.AreEqual("commit-sealed",(await ReadCompletionCaptureAsync(fixture.Data,generation).ConfigureAwait(false)).Phase);
            await AssertSignedPartialOriginalAsync(owned).ConfigureAwait(false);
        }
        finally
        {
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            {CurrentStageSql(database,dropSql);return true;},CancellationToken.None).ConfigureAwait(false);
        }
        _=await fixture.Reader.DisposeSignedPartialGenerationAsync(owned.Inventory,generation,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Header.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentSignedPartialRejectsAnotherReaderAndAnInvalidatedInventoryWithoutChangingWork(bool foreign)
    {
        await using var owned=await SignedPartialFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Header.Fixture;
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var target=foreign ? other : fixture.Reader;
        if(!foreign) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>target.DisposeSignedPartialGenerationAsync(
            owned.Inventory,owned.Header.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ReadSignedPartialAsync(owned).ConfigureAwait(false));await AssertSignedPartialOriginalAsync(owned).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(owned.Header.Pending.Path));Assert.AreEqual(owned.Header.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentSignedPartialJoinsTheActualAccountBeforeFreezingAnEnteredAllocationReceipt()
    {
        await using var owned=await SignedPartialFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Header.Fixture;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var account=fixture.Data.Setup.Accounts.Service.ReadDriveAsync("permission-A",async(_,_,token)=>
        {
            token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);
            return await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget)
                .AssignFileId(owned.Extra.Id,owned.Extra.Sha256,"entered-partial-id"),CancellationToken.None).ConfigureAwait(false);
        },CancellationToken.None);
        Task<CurrentSignedPartialDisposition>? pending=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            pending=fixture.Reader.DisposeSignedPartialGenerationAsync(owned.Inventory,owned.Header.Pending.Capture.Scope.GenerationId,CancellationToken.None);
            Assert.IsFalse(pending.IsCompleted);
        }
        finally{release.TrySetResult();await account.ConfigureAwait(false);}
        var operation=pending ?? throw new InvalidOperationException("Fixture did not start its actual partial disposition.");
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>operation).ConfigureAwait(false);
        Assert.IsNull(await ReadSignedPartialAsync(owned).ConfigureAwait(false));
        var row=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget).Read(owned.Extra.Id)!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("entered-partial-id",row.FileId);Assert.IsNull(row.VerifiedVersion);Assert.IsTrue(File.Exists(owned.Header.Pending.Path));
        var fresh=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        _=await fixture.Reader.DisposeSignedPartialGenerationAsync(fresh,owned.Header.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentSignedPartialKeepsActualSelectedAuthoritySeparateWhenARealNextGenerationUsesItsRetiredHeader()
    {
        await using var owned=await SignedPartialFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Header.Fixture;
        var receipt=await fixture.Reader.DisposeSignedPartialGenerationAsync(owned.Inventory,owned.Header.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await using(var retirement=new CurrentSignedPartialRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget))
            _=await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        await EnableSignedPartialSourceAsync(fixture).ConfigureAwait(false);
        var sources=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
        var next=await CreateLineageFrozenCaptureAsync(fixture).ConfigureAwait(false);
        var basis=new CapturedCatalogBasis(next.Capture.Snapshot!,receipt.OriginalCommit,receipt.Sequence,next.Approved.EnvelopeSha256,0,1);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        _=await fixture.Reader.CaptureCurrentPublicationBasisAsync(inventory,next.Capture.Scope.GenerationId,basis,CancellationToken.None).ConfigureAwait(false);
        var intent=await BuildLineagePublicationAsync(fixture,next,basis).ConfigureAwait(false);Assert.AreEqual(3L,intent.Sequence);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var target=new CurrentControlTransitionTarget(capture.Head.Publishers,sources,intent.Id);
        inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var root=await fixture.Reader.PrepareCurrentCatalogTransitionAsync(Guid.NewGuid(),inventory,target,CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(root.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Header.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        var prepared=await PrepareReconciledGraphAsync(fixture,owned.Header.Selected.Pool).ConfigureAwait(false);
        await using var stage=prepared.Stage;
        var observed=await fixture.Reader.ObserveSelectedInventoryAsync(prepared.Graph,CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.ActivateReconciledAsync(observed,stage,CancellationToken.None).ConfigureAwait(false);
        await using var completion=new CurrentCatalogGenerationCompletionService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var head=await completion.FinishAsync(next.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3L,head.Sequence);Assert.AreEqual(next.Capture.Scope.GenerationId,head.GenerationId);
        Assert.IsFalse(File.Exists(next.Path));await AssertSignedPartialOriginalAsync(owned).ConfigureAwait(false);
        Assert.AreEqual(receipt,await ReadSignedPartialAsync(owned).ConfigureAwait(false));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentSignedPartialRejectsAnUnwithdrawnRootAndPreservesTheActualNullSelectedHead()
    {
        await using var owned=await CurrentCompletionFixture.CreateAsync(activate:false).ConfigureAwait(false);var fixture=owned.Fixture;
        var extra=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var scope=owned.Capture.Scope;
            var bytes=new CatalogCoverageDocumentWriter(new(scope.AccountId,scope.FolderId)).EncodeRoot(scope.LibraryId,scope.ConfigurationRevision,1,0,[]);
            return new WorkspacePublicationRepository(database,fixture.Data.Budget).Prepare(Guid.NewGuid(),scope,"coverage-root","application/json",bytes);
        },CancellationToken.None).ConfigureAwait(false);
        var inventory=await MaterializedInspectionInventoryAsync(owned,withdraw:false).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlTransitionInProgressException>(()=>fixture.Reader.DisposeSignedPartialGenerationAsync(
            inventory,owned.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.SnapshotPath));
        var root=await MaterializedOriginalRootPlanAsync(owned).ConfigureAwait(false);
        _=await fixture.Reader.WithdrawTransitionAsync(root.Id,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
        inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var receipt=await fixture.Reader.DisposeSignedPartialGenerationAsync(inventory,owned.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(receipt.SelectedAnchor);Assert.AreEqual(1,receipt.WithdrawnRootPlans);
        await using var retirement=new CurrentSignedPartialRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        _=await retirement.FinishAsync(receipt.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(owned.SnapshotPath));Assert.IsNull(await LineageHeadAsync(fixture).ConfigureAwait(false));
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,fixture.Data.Budget).Read(extra.Id)!,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(retained.FileId);Assert.IsNull(retained.VerifiedVersion);Assert.AreEqual(extra.Sha256,retained.Sha256);
    }

    private static Task<CurrentSignedPartialDisposition?> ReadSignedPartialAsync(SignedPartialFixture owned)=>owned.Header.Fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        new CatalogGenerationCaptureRepository(database,owned.Header.Fixture.Data.Budget).SignedPartialDisposition(owned.Header.Pending.Capture.Scope.GenerationId),CancellationToken.None);
    private static async Task AssertSignedPartialOriginalAsync(SignedPartialFixture owned)
    {
        var data=owned.Header.Fixture.Data;
        var row=await data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspacePublicationRepository(database,data.Budget).Read(owned.Extra.Id)!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Extra.Id,row.Id);Assert.AreEqual(owned.Extra.Scope,row.Scope);Assert.AreEqual(owned.Extra.FileId,row.FileId);
        Assert.AreEqual(owned.Extra.VerifiedVersion,row.VerifiedVersion);Assert.AreEqual(owned.Extra.Sha256,row.Sha256);
        CollectionAssert.AreEqual(owned.Extra.Payload.ToArray(),row.Payload.ToArray());
    }
    private static Task<bool> EnableSignedPartialSourceAsync(CurrentReaderFixture fixture)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
    {
        var sources=new LibrarySourceRepository(database);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();
        sources.Update(source.Id,true,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;
    },CancellationToken.None);

    private sealed class SignedPartialFixture(HeaderRecoveryFixture header,WorkspacePublicationRecord extra,LocalCurrentWorkspaceInventory inventory) : IAsyncDisposable
    {
        internal HeaderRecoveryFixture Header {get;}=header;
        internal WorkspacePublicationRecord Extra {get;}=extra;
        internal LocalCurrentWorkspaceInventory Inventory {get;}=inventory;
        internal static async Task<SignedPartialFixture> CreateAsync(bool allocated)
        {
            WorkspacePublicationRecord? extra=null;
            var owned=await HeaderRecoveryFixture.CreateAsync(true,(database,budget,scope)=>
            {
                var outbox=new WorkspacePublicationRepository(database,budget);
                var bytes=new CatalogCoverageDocumentWriter(new(scope.AccountId,scope.FolderId)).EncodeRoot(scope.LibraryId,scope.ConfigurationRevision,1,0,[]);
                extra=outbox.Prepare(Guid.NewGuid(),scope,"coverage-root","application/json",bytes);
                if(allocated) extra=outbox.AssignFileId(extra.Id,extra.Sha256,"partial-extra-"+extra.Id.ToString("N"));
            }).ConfigureAwait(false);
            try
            {
                var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
                await using(var uploader=HeaderUploader(owned)) _=await uploader.UploadAsync(p1,CancellationToken.None).ConfigureAwait(false);
                return new(owned,extra ?? throw new InvalidOperationException("Fixture lost its actual prepared extra row."),
                    await FreshLineageInventoryAsync(owned.Fixture).ConfigureAwait(false));
            }
            catch{await owned.DisposeAsync().ConfigureAwait(false);throw;}
        }
        public ValueTask DisposeAsync()=>Header.DisposeAsync();
    }
}
