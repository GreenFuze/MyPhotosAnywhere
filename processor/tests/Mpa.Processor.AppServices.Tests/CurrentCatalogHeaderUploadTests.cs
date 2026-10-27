using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentHeaderUploadUsesRealP1AndP2ToRetireItsOriginalHeaderWithoutSelectingItOrUploadingOldFacts()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
        var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;var puts=fixture.Http.Puts;
        await using var owner=HeaderUploader(owned);
        var saved=await owner.UploadAsync(p1,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Id,saved.Id);Assert.AreEqual(owned.Pending.Capture.Scope,saved.Scope);
        Assert.AreEqual(p1.Original.Header.FileId,saved.FileId);Assert.IsNotNull(saved.VerifiedVersion);
        CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),saved.Payload.ToArray());
        Assert.AreEqual(creates+1,fixture.Data.Creates);Assert.AreEqual(allocations,fixture.Data.Allocations);
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.AssertCatalogHeaderCloudAdmissionAsync(p1,CancellationToken.None)).ConfigureAwait(false);
        var p2=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var superseded=await fixture.Reader.SupersedeMaterializedGenerationAsync(p2,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Head.Commit,superseded.SelectedAnchor);Assert.AreEqual(2L,superseded.Sequence);
        Assert.AreEqual(saved.FileId,superseded.OriginalCommit.FileId);
        await using var retirement=new CurrentSupersededGenerationRetirementService(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        Assert.AreEqual(superseded,await retirement.FinishAsync(superseded.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(owned.Pending.Path));Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(puts,fixture.Http.Puts);Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentHeaderUploadLostCreateReplyRecoversOnlyTheSameOriginalIdAndFirstBytes()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;var lost=false;var allocations=fixture.Data.Allocations;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {
            var actual=await create(folder,file,mime,bytes,sha,token).ConfigureAwait(false);
            if(!lost){lost=true;throw new HttpRequestException("Lost exact header create reply after storage.");}
            return actual;
        };
        await using var owner=HeaderUploader(owned);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>owner.UploadAsync(p1,CancellationToken.None)).ConfigureAwait(false);
        var pending=(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!;
        Assert.AreEqual(p1.Original.Header.FileId,pending.FileId);Assert.IsNull(pending.VerifiedVersion);
        var next=await FreshHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
        var saved=await owner.UploadAsync(next,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.Id,saved.Id);Assert.AreEqual(pending.FileId,saved.FileId);Assert.IsNotNull(saved.VerifiedVersion);
        CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),saved.Payload.ToArray());
        Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.IsNull(owner.Failure);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    [TestMethod]
    public async Task CurrentHeaderUploadRefusesEveryWrongReadbackFieldWithoutRecordingVerificationOrAllocatingAgain()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;var allocations=fixture.Data.Allocations;
        var mode=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {
            var value=await create(folder,file,mime,bytes,sha,token).ConfigureAwait(false);
            var changed=value.Bytes.ToArray();changed[0]^=1;
            return mode switch
            {
                0=>value with{AccountId="foreign-account"},1=>value with{FolderId="foreign-folder"},
                2=>value with{FileId="foreign-file"},3=>value with{MimeType="image/jpeg"},
                4=>value with{Bytes=value.Bytes[..^1]},5=>value with{Bytes=changed},6=>value with{DriveVersion=0},
                _=>throw new InvalidOperationException("Unknown readback fixture case.")
            };
        };
        await using var owner=HeaderUploader(owned);
        for(mode=0;mode<7;mode++)
        {
            await Assert.ThrowsExactlyAsync<WorkspacePublicationConflictException>(()=>owner.UploadAsync(p1,CancellationToken.None)).ConfigureAwait(false);
            var row=(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!;
            Assert.AreEqual(p1.Original.Header.FileId,row.FileId);Assert.IsNull(row.VerifiedVersion);
            CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),row.Payload.ToArray());
            Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderUploadCancellationOrExitJoinsAnEnteredWriteWithoutAdmittingALateReceipt(bool exit)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);return await create(folder,file,mime,bytes,sha,CancellationToken.None).ConfigureAwait(false);};
        await using var owner=HeaderUploader(owned);using var caller=new CancellationTokenSource();
        var pending=owner.UploadAsync(p1,caller.Token);var stopping=Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(pending.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);
        }
        finally{release.TrySetResult();try{await pending.ConfigureAwait(false);}catch(OperationCanceledException){}}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>pending).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var row=(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!;
        Assert.AreEqual(p1.Original.Header.FileId,row.FileId);Assert.IsNull(row.VerifiedVersion);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        if(exit) Assert.IsTrue(owner.ExitConfirmed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderUploadRefusesReceiptAfterActualSourceOrPublisherChangeDuringCreate(bool revoked)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {
            var result=await create(folder,file,mime,bytes,sha,token).ConfigureAwait(false);
            if(revoked) _=await SignHeaderAllocationRevocationAsync(fixture).ConfigureAwait(false);
            else _=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
            return result;
        };
        await using var owner=HeaderUploader(owned);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>owner.UploadAsync(p1,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.VerifiedVersion);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderUploadRollsBackReceiptAndContextTriggerChangesBeforeRetryingTheSameObject(bool context)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);var allocations=fixture.Data.Allocations;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var sql=context?
                "CREATE TRIGGER corrupt_header_receipt AFTER UPDATE OF verified_version ON workspace_publication WHEN NEW.role='catalog-commit' BEGIN UPDATE catalog_generation_capture SET phase='dependencies-ready' WHERE id='"+owned.Pending.Capture.Scope.GenerationId.ToString("D")+"'; END":
                "CREATE TRIGGER corrupt_header_receipt AFTER UPDATE OF verified_version ON workspace_publication WHEN NEW.role='catalog-commit' BEGIN UPDATE workspace_publication SET verified_version=NEW.verified_version+1 WHERE id=NEW.id; END";
            CurrentStageSql(database,sql);return true;
        },CancellationToken.None).ConfigureAwait(false);
        await using var owner=HeaderUploader(owned);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>owner.UploadAsync(p1,CancellationToken.None)).ConfigureAwait(false);
        var pending=(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!;
        Assert.IsNull(pending.VerifiedVersion);Assert.AreEqual(p1.Original.Header.FileId,pending.FileId);
        Assert.AreEqual("commit-sealed",(await ReadCompletionCaptureAsync(fixture.Data,owned.Pending.Capture.Scope.GenerationId).ConfigureAwait(false)).Phase);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER corrupt_header_receipt");return true;},CancellationToken.None).ConfigureAwait(false);
        var saved=await owner.UploadAsync(await FreshHeaderUploadAdmissionAsync(owned).ConfigureAwait(false),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.Id,saved.Id);Assert.AreEqual(pending.FileId,saved.FileId);Assert.IsNotNull(saved.VerifiedVersion);
        Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderUploadRejectsForeignReaderOrStaleNonceBeforeAnyCreate(bool stale)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var p1=await PrepareHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);var creates=fixture.Data.Creates;
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        if(stale) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=new CurrentCatalogHeaderUploadService(fixture.Data.Setup.Accounts.Service,stale?fixture.Reader:other);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>owner.UploadAsync(p1,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(creates,fixture.Data.Creates);Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.VerifiedVersion);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    private static CurrentCatalogHeaderUploadService HeaderUploader(HeaderRecoveryFixture owned)=>
        new(owned.Fixture.Data.Setup.Accounts.Service,owned.Fixture.Reader);
    private static async Task<LocalCurrentCatalogHeaderCloudAdmission> PrepareHeaderUploadAdmissionAsync(HeaderRecoveryFixture owned)
    {
        var p0=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);
        await using(var allocator=HeaderAllocator(owned)) _=await allocator.AllocateAsync(p0,CancellationToken.None).ConfigureAwait(false);
        return await FreshHeaderUploadAdmissionAsync(owned).ConfigureAwait(false);
    }
    private static async Task<LocalCurrentCatalogHeaderCloudAdmission> FreshHeaderUploadAdmissionAsync(HeaderRecoveryFixture owned)=>
        await owned.Fixture.Reader.InspectCatalogHeaderCloudAsync(await FreshLineageInventoryAsync(owned.Fixture).ConfigureAwait(false),
            owned.Pending.Capture.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
}
