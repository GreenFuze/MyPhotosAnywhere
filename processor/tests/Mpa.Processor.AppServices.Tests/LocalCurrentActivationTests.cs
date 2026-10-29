using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;
using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly JsonSerializerOptions ActivationTestJson=new()
        {NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.WriteAsString|System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString};
    [TestMethod]
    public async Task LocalCurrentActivationCommitsTheExactSealedGraphWithoutAnotherNetworkRead()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await AddCatalogHeadAsync(fixture.Data,"active-first",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        var bodyReads=fixture.Http.BodyReads;var metadataReads=fixture.Http.MetadataReads;
        var receipt=await fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(prepared.Id,receipt.StageId);Assert.AreEqual(head.Reference,receipt.Catalog);Assert.AreEqual(1L,receipt.Index.Assets);
        Assert.AreEqual(prepared.Fence.Current.Attempt.Id,receipt.FencedAttempt);Assert.AreEqual(receipt,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(receipt,await fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(bodyReads,fixture.Http.BodyReads);Assert.AreEqual(metadataReads,fixture.Http.MetadataReads);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));Assert.AreEqual(1,(await stage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
        await stage.DisposeAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>pool.RetireAsync(receipt.StageId,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(await StagePathAsync(fixture,receipt.StageId).ConfigureAwait(false)));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentActivationReplacesOnlyThePointerAndRetiresOnlyTheClosedInactiveIndex()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await AddCatalogHeadAsync(fixture.Data,"activation-old",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var first=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var oldStage=first.Stage;
        var old=await fixture.Reader.ActivateAsync(first.Fence,oldStage,CancellationToken.None).ConfigureAwait(false);
        var next=await AddCatalogHeadAsync(fixture.Data,"activation-new",head.Reference,includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,next.Reference).ConfigureAwait(false);
        var second=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var newStage=second.Stage;
        Assert.AreEqual(old,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(1,(await oldStage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
        var active=await fixture.Reader.ActivateAsync(second.Fence,newStage,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(2L,active.CatalogSequence);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>pool.RetireAsync(old.StageId,CancellationToken.None)).ConfigureAwait(false);
        await oldStage.DisposeAsync().ConfigureAwait(false);await pool.RetireAsync(old.StageId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(active.StageId,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);Assert.AreEqual(active,await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(1,(await newStage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
        Assert.AreEqual(new Mpa.Processor.Protocol.DriveAssetIdentity(next.Plan.Scope.LibraryId).Derive("permission-A","read-only-original"),(await newStage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Single().Id);
    }

    [TestMethod]
    public async Task LocalCurrentActivationLastPairedWriteFailurePreservesOldSelectionAndTheSamePendingIdentity()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var first=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var oldStage=first.Stage;
        var old=await fixture.Reader.ActivateAsync(first.Fence,oldStage,CancellationToken.None).ConfigureAwait(false);
        var second=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var pending=second.Stage;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"CREATE TRIGGER fail_activation BEFORE INSERT ON processor_meta WHEN NEW.key='current_catalog_active_v1:sha256' BEGIN SELECT RAISE(ABORT,'last active checkpoint'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.ActivateAsync(second.Fence,pending,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(old,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(2,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_activation");return true;},CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(second.Id,(await fixture.Reader.ActivateAsync(second.Fence,pending,CancellationToken.None).ConfigureAwait(false)).StageId);
    }

    [TestMethod]
    public async Task LocalCurrentActivationRejectsForeignOwnersCancellationAndSupersededNonceBeforeIo()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        using var canceled=new CancellationTokenSource();canceled.Cancel();var reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>fixture.Reader.ActivateAsync(prepared.Fence,stage,canceled.Token)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.ActivateAsync(prepared.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(prepared.Id,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);
    }

    [TestMethod]
    public async Task LocalCurrentActivationRejectsHumanSourceChangesAfterTheActualRootFence()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var sources=new LibrarySourceRepository(database);var source=sources.Snapshot().Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,2);return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentActivationCorruptPairedPointerBlocksEvenInactiveRetirement()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        var active=await fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None).ConfigureAwait(false);
        await stage.DisposeAsync().ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"UPDATE processor_meta SET value='{}' WHERE key='current_catalog_active_v1'");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ActiveSelectionAsync(fixture)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>pool.RetireAsync(active.StageId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("sealed",(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Phase);Assert.IsTrue(File.Exists(await StagePathAsync(fixture,active.StageId).ConfigureAwait(false)));
    }

    private static Task<CurrentCatalogActivation?> ActiveSelectionAsync(CurrentReaderFixture fixture)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(
        database=>new CurrentCatalogActivationRepository(database,fixture.Data.Budget).Read(),CancellationToken.None);

    [TestMethod]
    public async Task LocalCurrentActivationRequiresTheSameLiveStageAndReaderThroughAdmission()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var first=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var oldStage=first.Stage;
        var next=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=next.Stage;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ActivateAsync(next.Fence,oldStage,CancellationToken.None)).ConfigureAwait(false);
        await stage.DisposeAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>fixture.Reader.ActivateAsync(next.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        await fixture.Reader.DisposeAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>fixture.Reader.ActivateAsync(next.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(2,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
    }

    [TestMethod]
    public async Task LocalCurrentActivationDisconnectedAccountCannotAdmitButHistoricalSelectionSurvives()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var first=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var oldStage=first.Stage;
        var old=await fixture.Reader.ActivateAsync(first.Fence,oldStage,CancellationToken.None).ConfigureAwait(false);
        var next=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=next.Stage;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(()=>fixture.Reader.ActivateAsync(next.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(old,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentActivationUnsupportedFormatBlocksRetirementEvenWithAMatchingDigest()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        var active=await fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None).ConfigureAwait(false);await stage.DisposeAsync().ConfigureAwait(false);
        var bytes=new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(active with {Major=2},ActivationTestJson));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(database.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());connection.Open();
            using var command=connection.CreateCommand();command.CommandText="UPDATE processor_meta SET value=CASE key WHEN 'current_catalog_active_v1' THEN $body ELSE $hash END WHERE key IN ('current_catalog_active_v1','current_catalog_active_v1:sha256')";
            command.Parameters.AddWithValue("$body",System.Text.Encoding.UTF8.GetString(bytes));command.Parameters.AddWithValue("$hash",CurrentHash(bytes));Assert.AreEqual(2,command.ExecuteNonQuery());return true;
        },CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>ActiveSelectionAsync(fixture)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>pool.RetireAsync(active.StageId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("sealed",(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Phase);
    }

    [TestMethod]
    public async Task LocalCurrentActivationRejectsCatalogRollbackEvenWhenTheNewerObservedHeadWasNeverActivated()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var old=await AddCatalogHeadAsync(fixture.Data,"rollback-old",includeAsset:true).ConfigureAwait(false);
        var next=await AddCatalogHeadAsync(fixture.Data,"rollback-new",old.Reference,includeAsset:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,next.Reference).ConfigureAwait(false);await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,old.Reference).ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var prepared=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=prepared.Stage;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ActivateAsync(prepared.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(prepared.Id,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Single().Id);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentActivationRevocationCanActivateEmptyAndNeverReactivateTheRetiredEpoch(bool activateRevocation)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var first=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var oldStage=first.Stage;
        var old=await fixture.Reader.ActivateAsync(first.Fence,oldStage,CancellationToken.None).ConfigureAwait(false);
        await SelectActivationPublisherAsync(fixture,"activation-revoked",revoked:true).ConfigureAwait(false);
        CurrentCatalogActivation expected=old;
        if(activateRevocation)
        {
            var revoked=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var empty=revoked.Stage;
            expected=await fixture.Reader.ActivateAsync(revoked.Fence,empty,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(expected.PublisherId);Assert.IsNull(expected.Catalog);Assert.AreEqual(0L,expected.Index.Assets);
            await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(
                database=>new LibraryAuthorityRepository(database,fixture.Data.Budget).CaptureLocalPublicationAuthority(),CancellationToken.None)).ConfigureAwait(false);
            await oldStage.DisposeAsync().ConfigureAwait(false);await pool.RetireAsync(first.Id,CancellationToken.None).ConfigureAwait(false);
        }
        else await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await SelectActivationPublisherAsync(fixture,"activation-reinstated",revoked:false).ConfigureAwait(false);
        var retry=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var retryStage=retry.Stage;
        await Assert.ThrowsExactlyAsync<ProtocolSigningConflictException>(()=>fixture.Reader.ActivateAsync(retry.Fence,retryStage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(expected,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentActivationPolicyAndPointerRollbackTogetherWhenTheLastWriteFails()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var first=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var oldStage=first.Stage;
        var old=await fixture.Reader.ActivateAsync(first.Fence,oldStage,CancellationToken.None).ConfigureAwait(false);
        await SelectActivationPublisherAsync(fixture,"rollback-revocation",revoked:true).ConfigureAwait(false);
        var next=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var pending=next.Stage;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"CREATE TRIGGER fail_policy_activation BEFORE INSERT ON processor_meta WHEN NEW.key='current_catalog_active_v1:sha256' BEGIN SELECT RAISE(ABORT,'paired activation failure'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>fixture.Reader.ActivateAsync(next.Fence,pending,CancellationToken.None)).ConfigureAwait(false);
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibraryAuthorityRepository(database,fixture.Data.Budget).CurrentPublisherPolicy()!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(old.PublisherSequence,retained.Sequence);Assert.IsNotNull(retained.Publisher);Assert.AreEqual(old,await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_policy_activation");return true;},CancellationToken.None).ConfigureAwait(false);
        var result=await fixture.Reader.ActivateAsync(next.Fence,pending,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(next.Id,result.StageId);Assert.IsNull(result.PublisherId);
    }

    [TestMethod]
    public async Task LocalCurrentPoliciesRetainRejectedActualBytesAcrossExactRepairAndDenyVersionRewind()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var reference=capture.Head.Publishers.Reference;
        var original=fixture.Data.Cloud[reference.FileId];var bad=original.Bytes.ToArray();bad[0]=0;fixture.Data.Cloud[reference.FileId]=original with {Bytes=bad};
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Task<LocalCurrentGraphObservation?> Read()=>fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LocalCurrentGraphObservations(database,fixture.Data.Budget).Current(capture.Attempt,reference),CancellationToken.None);
        var failed=await Read().ConfigureAwait(false);Assert.IsNotNull(failed);Assert.AreEqual(CurrentHash(bad),failed.RejectedPolicySha256);Assert.AreEqual(original.DriveVersion,failed.RejectedPolicyVersion);Assert.IsNull(failed.BodyVersion);
        fixture.Data.Cloud[reference.FileId]=original;await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var repaired=await Read().ConfigureAwait(false);Assert.IsNotNull(repaired);Assert.AreEqual(failed.RejectedPolicySha256,repaired.RejectedPolicySha256);Assert.AreEqual(reference.Sha256,repaired.BodySha256);
        fixture.Data.Cloud[reference.FileId]=original with {DriveVersion=original.DriveVersion-1};var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(repaired,await Read().ConfigureAwait(false));
    }

    private static async Task SelectActivationPublisherAsync(CurrentReaderFixture fixture,string file,bool revoked)
    {
        var signer=new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var policy=await signer.SignControllerAsync(Guid.NewGuid(),"library-publisher-authorization",(identity,sequence)=>new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa",major=1,minor=0,libraryId=identity.LibraryId.ToString("D"),actorId=identity.ControllerId.ToString("D"),epoch=identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            sequence=sequence.ToString(CultureInfo.InvariantCulture),kind="library-publisher-authorization",requiredCapabilities=new[]{CatalogPublisherAuthorization.Capability},
            payload=new {workspace=new {accountId="permission-A",folderId="metadata"},publisher=revoked ? null : new {actorId=identity.PublisherId.ToString("D"),epoch=identity.PublisherEpoch.ToString(CultureInfo.InvariantCulture),publicKeySpki=identity.PublisherPublicKeySpki,keyId=identity.PublisherKeyId}}
        })),CancellationToken.None).ConfigureAwait(false);
        var bytes=policy.Envelope!.Value.ToArray();fixture.Data.Cloud[file]=new("permission-A",file,"metadata",8,bytes);
        var slot=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Data.Budget).Current()!,CancellationToken.None).ConfigureAwait(false);
        var old=LibraryCurrentControl.Read(fixture.Data.Cloud["captured-3"].Bytes.Span,slot.ConnectionDescriptor());
        var root=await signer.SignControllerAsync(Guid.NewGuid(),LibraryCurrentControl.Kind,(_,sequence)=>new LibraryCurrentControlWriter(slot.ConnectionDescriptor()).EncodeDocument(
            sequence,new(new("permission-A",file,"metadata",bytes.Length,policy.EnvelopeSha256!),policy.Sequence),old.Sources,null),CancellationToken.None).ConfigureAwait(false);
        fixture.Data.Cloud["captured-3"]=fixture.Data.Cloud["captured-3"] with {DriveVersion=checked(fixture.Data.Cloud["captured-3"].DriveVersion+1),Bytes=root.Envelope!.Value.ToArray()};
    }

    private static async Task<(Guid Id,LocalCurrentSqliteStage Stage,LocalCurrentGraphFence Fence)> PrepareActivationAsync(CurrentReaderFixture fixture,LocalCurrentStagePool pool)
    {
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var id=Guid.NewGuid();var stage=await pool.OpenAsync(capture,id,CancellationToken.None).ConfigureAwait(false);
        try
        {
            var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None).ConfigureAwait(false);
            await stage.SealAsync(graph,CancellationToken.None).ConfigureAwait(false);
            return(id,stage,await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false));
        }
        catch {await stage.DisposeAsync().ConfigureAwait(false);throw;}
    }
}
