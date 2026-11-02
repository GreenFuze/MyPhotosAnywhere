using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task TakeoutNativeApprovalUsesExistingControllerOnceAndFullCurrentInventoryAccountsForItsPrivateSignedCommand()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var before=await state.ExecuteAsync(db=>new ProtocolSigningJournal(db,fixture.Data.Budget).Inventory(selection.Completed.Target.LibraryId),deadline.Token).ConfigureAwait(false);
        var approval=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        var replay=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(approval.Id,replay.Id);Assert.AreEqual(approval.Sequence,replay.Sequence);CollectionAssert.AreEqual(approval.Envelope!.Value.ToArray(),replay.Envelope!.Value.ToArray());
        var after=await state.ExecuteAsync(db=>new ProtocolSigningJournal(db,fixture.Data.Budget).Inventory(selection.Completed.Target.LibraryId),deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(before.Intents.Count+1,after.Intents.Count);Assert.AreEqual(1,after.Intents.Count(row=>row.Kind==TakeoutOriginalApprovalRepository.Kind));
        Assert.AreEqual(fixture.Data.Setup.Keys.PublicIdentity.ControllerId,approval.ActorId);Assert.AreEqual(TakeoutOriginalApprovalRepository.Kind,approval.Kind);
        var pin=await state.ExecuteAsync(db=>new LibraryAuthorityRepository(db).Controller()!,deadline.Token).ConfigureAwait(false);
        var parsed=new ApprovedOriginalActionReader(pin.ActorId).Read(new SignedProtocolDocuments([]).VerifyAndRead(approval.Envelope.Value.Span,
            new(pin.LibraryId,pin.ActorId,pin.Epoch,Convert.FromBase64String(pin.PublicKeySpki),[TakeoutOriginalApprovalRepository.Kind])));
        Assert.AreEqual(OriginalActionKind.Import,parsed.Action);Assert.AreEqual("photos",parsed.Destination!.FolderId);Assert.AreEqual("metadata",parsed.Destination.WorkspaceFolderId);
        Assert.AreEqual(approval.Id,parsed.ApprovalId);
        // Exercise complete actual signing, immutable-workspace inventory and
        // new-generation admission after the new private original command.
        InstallCurrentInventoryTransport(fixture);var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        var retainedSigning=await state.ExecuteAsync(db=>new ProtocolSigningJournal(db,fixture.Data.Budget).Inventory(selection.Completed.Target.LibraryId),deadline.Token).ConfigureAwait(false);
        Assert.IsTrue(retainedSigning.Intents.Any(row=>row.Id==approval.Id));
        var generation=await fixture.Reader.BeginNewCurrentGenerationAsync(inventory,deadline.Token).ConfigureAwait(false);Assert.AreEqual("capturing",generation.Phase);
        CollectionAssert.AreEqual(selection.Payload,await File.ReadAllBytesAsync(selection.Path,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutNativeApprovalActuallyAdmitsOriginalUploadWithFreshRootPerBoundedStepAndKeepsControllerAndPublisherRolesSeparate()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);
        var approval=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        var input=await NativeTakeoutJobAsync(fixture,selection,approval.Id,deadline.Token).ConfigureAwait(false);
        var authority=await fixture.Reader.OpenTakeoutOriginalAuthorityAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        var cloud=new OriginalCloud(input.Payload){Partial=true,MissingHash=true};WireOriginal(fixture.Data.Setup.Accounts,cloud);
        var initial=fixture.Http.BodyReads;await using var upload=new TakeoutOriginalUploadService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,input.Budget,authority);
        var result=await upload.UploadAsync(input.Job,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual("first-original-id",result.FileId);Assert.AreEqual(1,cloud.Allocations);Assert.AreEqual(2,cloud.RangeReads);Assert.IsTrue(fixture.Http.BodyReads>initial+12);
        CollectionAssert.AreEqual(input.Payload,cloud.Uploaded);
        var summary=await fixture.Data.Setup.Accounts.State.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,summary.Ready);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>fixture.Data.Setup.Keys.SignCatalogDocument(approval.Body.Span));
    }

    [TestMethod]
    public async Task TakeoutNativeApprovalCannotOpenUnapprovedInputOrGrantAnotherImportAndSupersededFenceNeedsFreshPermission()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>fixture.Reader.OpenTakeoutOriginalAuthorityAsync(selection.Completed,deadline.Token)).ConfigureAwait(false);
        var approval=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        var input=await NativeTakeoutJobAsync(fixture,selection,approval.Id,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var authority=await fixture.Reader.OpenTakeoutOriginalAuthorityAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        var work=await state.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).Claim(input.Job)!,deadline.Token).ConfigureAwait(false);
        try
        {
            await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>state.ExecuteAsync(db=>{authority.AssertCurrent(db,work);return true;},deadline.Token)).ConfigureAwait(false);
            Assert.AreEqual(selection.Completed.Target.AccountRevision,await authority.RefreshAsync(work,deadline.Token).ConfigureAwait(false));
            await state.ExecuteAsync(db=>{authority.AssertCurrent(db,work);return true;},deadline.Token).ConfigureAwait(false);
            _=await fixture.Reader.CaptureAsync(deadline.Token).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>state.ExecuteAsync(db=>{authority.AssertCurrent(db,work);return true;},deadline.Token)).ConfigureAwait(false);
            await authority.RefreshAsync(work,deadline.Token).ConfigureAwait(false);await state.ExecuteAsync(db=>{authority.AssertCurrent(db,work);return true;},deadline.Token).ConfigureAwait(false);
            var other=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var otherApproval=await fixture.Reader.ApproveTakeoutImportAsync(other.Completed,deadline.Token).ConfigureAwait(false);
            var otherInput=await NativeTakeoutJobAsync(fixture,other,otherApproval.Id,deadline.Token).ConfigureAwait(false);var otherWork=await state.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).Claim(otherInput.Job)!,deadline.Token).ConfigureAwait(false);
            try {await Assert.ThrowsExactlyAsync<ProtocolSigningConflictException>(()=>authority.RefreshAsync(otherWork,deadline.Token)).ConfigureAwait(false);}
            finally {await state.ExecuteAsync(db=>{new DurableJobRepository(db).ReturnJoinedRead(otherWork.Lease);return true;},CancellationToken.None).ConfigureAwait(false);}
        }
        finally {await state.ExecuteAsync(db=>{new DurableJobRepository(db).ReturnJoinedRead(work.Lease);return true;},CancellationToken.None).ConfigureAwait(false);}
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TakeoutNativeApprovalRejectsUnapprovedDestinationOrStaleSelectionBeforeAnySignatureReservation(bool stale)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token,stale).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var before=await state.ExecuteAsync(db=>new ProtocolSigningJournal(db,fixture.Data.Budget).Inventory(selection.Completed.Target.LibraryId),deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token)).ConfigureAwait(false);
        var after=await state.ExecuteAsync(db=>new ProtocolSigningJournal(db,fixture.Data.Budget).Inventory(selection.Completed.Target.LibraryId),deadline.Token).ConfigureAwait(false);Assert.AreEqual(before.Sha256,after.Sha256);
    }

    [TestMethod]
    public async Task TakeoutNativeApprovalRevokedSourceStopsExistingIntentWhileReconnectUsesSeparateCurrentAccessWithoutRewritingIt()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var approval=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);var input=await NativeTakeoutJobAsync(fixture,selection,approval.Id,deadline.Token).ConfigureAwait(false);
        var authority=await fixture.Reader.OpenTakeoutOriginalAuthorityAsync(selection.Completed,deadline.Token).ConfigureAwait(false);var work=await state.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).Claim(input.Job)!,deadline.Token).ConfigureAwait(false);
        try
        {
            await authority.RefreshAsync(work,deadline.Token).ConfigureAwait(false);var context=work.Lease.CapturedContext;
            var reconnected=await fixture.Data.Setup.Accounts.Service.ConnectAsync("permission-A",new NoBrowser(),deadline.Token).ConfigureAwait(false);
            var revision=await authority.RefreshAsync(work,deadline.Token).ConfigureAwait(false);Assert.AreEqual(reconnected.Revision,revision);Assert.IsTrue(revision>work.Capture.Inventory.Target.AccountRevision);Assert.AreEqual(context,work.Lease.CapturedContext);
            await state.ExecuteAsync(db=>
            {authority.AssertCurrent(db,work);var sources=new LibrarySourceRepository(db);var snapshot=sources.Snapshot();var selected=snapshot.Sources.Single();sources.Update(selected.Id,false,selected.IncludeDescendants,selected.Revision,snapshot.Revision);return true;},deadline.Token).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>state.ExecuteAsync(db=>{authority.AssertCurrent(db,work);return true;},deadline.Token)).ConfigureAwait(false);
            Assert.AreEqual(0,(await state.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false)).Ready);
            var retained=await state.ExecuteAsync(db=>new TakeoutOriginalApprovalRepository(db,fixture.Data.Budget).RequireSealed(selection.Completed),deadline.Token).ConfigureAwait(false);
            CollectionAssert.AreEqual(approval.Envelope!.Value.ToArray(),retained.Envelope!.Value.ToArray());
        }
        finally {await state.ExecuteAsync(db=>{new DurableJobRepository(db).ReturnJoinedRead(work.Lease);return true;},CancellationToken.None).ConfigureAwait(false);}
    }

    [TestMethod]
    public async Task TakeoutNativeApprovalCannotSkipMissingNativeBindingOrReserveAnUnownedActionCounter()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var approval=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        await state.ExecuteAsync(db=>
        {
            var journal=new ProtocolSigningJournal(db,fixture.Data.Budget);var before=journal.Inventory(selection.Completed.Target.LibraryId);var other=Guid.NewGuid();
            Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(()=>journal.Reserve(other,approval.ActorId,approval.Epoch,TakeoutOriginalApprovalRepository.Kind,sequence=>
            {
                var json=System.Text.Json.Nodes.JsonNode.Parse(approval.Body.Span)!;json["sequence"]=sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);json["payload"]!["approvalId"]=other.ToString("D");
                return new CanonicalJson().Encode(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
            }));Assert.AreEqual(before.Sha256,journal.Inventory(selection.Completed.Target.LibraryId).Sha256);
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(db.StateDirectory,"processor.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());connection.Open();
            using var remove=connection.CreateCommand();remove.CommandText="DELETE FROM processor_meta WHERE key=$key";remove.Parameters.AddWithValue("$key","takeout-original-approval-v1:"+approval.Id.ToString("D"));Assert.AreEqual(1,remove.ExecuteNonQuery());
            Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(()=>journal.Inventory(selection.Completed.Target.LibraryId));
            return true;
        },deadline.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TakeoutNativeApprovalRecoversOnlyTheExactPendingSignatureAfterSourceChangeWithoutRevivingOriginalWritePermission()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var draft=await state.ExecuteAsync(db=>new TakeoutOriginalApprovalRepository(db,fixture.Data.Budget).CaptureIntent(selection.Completed),deadline.Token).ConfigureAwait(false);
        Assert.IsNull(draft.Envelope);
        await state.ExecuteAsync(db=>{var sources=new LibrarySourceRepository(db);var snapshot=sources.Snapshot();var source=snapshot.Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,snapshot.Revision);return true;},deadline.Token).ConfigureAwait(false);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Allocations,fixture.Data.Creates);
        var sealedIntent=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(draft.Id,sealedIntent.Id);Assert.AreEqual(draft.Sequence,sealedIntent.Sequence);Assert.AreEqual(draft.BodySha256,sealedIntent.BodySha256);CollectionAssert.AreEqual(draft.Body.ToArray(),sealedIntent.Body.ToArray());Assert.IsNotNull(sealedIntent.Envelope);
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts,fixture.Data.Allocations,fixture.Data.Creates));
        var replay=await fixture.Reader.ApproveTakeoutImportAsync(selection.Completed,deadline.Token).ConfigureAwait(false);CollectionAssert.AreEqual(sealedIntent.Envelope.Value.ToArray(),replay.Envelope!.Value.ToArray());
        var input=await NativeTakeoutJobAsync(fixture,selection,sealedIntent.Id,deadline.Token).ConfigureAwait(false);var authority=await fixture.Reader.OpenTakeoutOriginalAuthorityAsync(selection.Completed,deadline.Token).ConfigureAwait(false);
        var work=await state.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).Claim(input.Job)!,deadline.Token).ConfigureAwait(false);
        try {await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>authority.RefreshAsync(work,deadline.Token)).ConfigureAwait(false);}
        finally {await state.ExecuteAsync(db=>{new DurableJobRepository(db).ReturnJoinedRead(work.Lease);return true;},CancellationToken.None).ConfigureAwait(false);}
        Assert.AreEqual(0,(await state.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false)).Ready);
    }

    private sealed record NativeTakeoutSelection(TakeoutCompletedInventory Completed,string Path,byte[] Payload);
    private static async Task<NativeTakeoutSelection> NativeTakeoutSelectionAsync(CurrentReaderFixture fixture,CancellationToken token,bool? invalid=null)
    {
        var state=fixture.Data.Setup.Accounts.State;
        var selection=await state.ExecuteAsync(db=>new{db.StateDirectory,Pin=new LibraryAuthorityRepository(db).Controller()!,Sources=new LibrarySourceRepository(db).Snapshot(),Account=new DriveAccountRepository(db).Accounts().Single()},token).ConfigureAwait(false);
        var folder=Path.Combine(selection.StateDirectory,"takeout-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var path=Path.Combine(folder,"a.jpg");
        var payload=System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024*1024+19);await File.WriteAllBytesAsync(path,payload,token).ConfigureAwait(false);
        var id=Guid.NewGuid();using(var source=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder))
        {
            var target=new TakeoutImportTarget(selection.Pin.LibraryId,selection.Account.PermissionId,selection.Account.Revision,invalid==false?"not-approved":"photos",invalid==true?selection.Sources.Revision-1:selection.Sources.Revision);
            await state.ExecuteAsync(db=>{new TakeoutImportInventoryRepository(db).Ensure(id,target,[source.Binding]);return true;},token).ConfigureAwait(false);
        }
        await using var owner=new TakeoutInventoryService(state,fixture.Data.Budget);var completed=await owner.RunAsync(id,token).ConfigureAwait(false);return new(completed,path,payload);
    }
    private static async Task<OriginalInput> NativeTakeoutJobAsync(CurrentReaderFixture fixture,NativeTakeoutSelection selection,Guid approval,CancellationToken token)
    {
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(selection.Completed,fixture.Data.Budget,token).ConfigureAwait(false);
        await using var source=await TakeoutOriginalReader.CaptureAsync(selection.Completed,catalog,catalog.ReadPage(TakeoutEntryKind.Media,token:token).Entries.Single(),token).ConfigureAwait(false);
        var job=await fixture.Data.Setup.Accounts.State.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).Ensure(source.Capture,approval),token).ConfigureAwait(false);
        return new(job,approval,selection.Path,selection.Payload,source.Capture.Sha256,fixture.Data.Budget);
    }
}
