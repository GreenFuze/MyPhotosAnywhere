using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void CurrentInventoryCompletionIsTypedIdempotentAndNeverAdvancesLegacyHeadOrGenerationState()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var before=new ProtocolSigningJournal(fixture.Database,fixture.Budget).Inventory(input.Context.LibraryId).Sha256;
        var result=CompleteCurrentInventory(repository,input);
        Assert.AreEqual(result,CompleteCurrentInventory(repository,input));Assert.AreEqual(result,repository.ReadCurrentInventory());
        repository.AssertCurrentInventoryUnchanged(result,input.Signing,input.Fenced,input.Publications);
        Assert.AreEqual(input.Legacy,repository.LastCheckpoint());Assert.AreEqual(2,result.Documents);Assert.AreEqual(input.Attempt.Id,result.AttemptId);
        Assert.AreEqual(input.Fenced.Id,result.RootAttemptId);Assert.AreEqual(input.Fenced.ContentSha256,result.RootSha256);
        Assert.AreEqual(2L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key GLOB 'current_workspace_inventory_v1*'"));
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>repository.AssertCompletedScan(input.Legacy));
        Assert.IsNull(new CurrentCatalogActivationRepository(fixture.Database,fixture.Budget).Read());
        Assert.IsNull(new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget).PublishedHead());
        Assert.AreEqual(before,new ProtocolSigningJournal(fixture.Database,fixture.Budget).Inventory(input.Context.LibraryId).Sha256);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CurrentInventoryLastHashFailureAndLowSpaceCannotPartiallyCompleteOrReleaseRetainedWork()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        fixture.Space.Available=1;Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>CompleteCurrentInventory(repository,input));
        fixture.Space.Available=20*StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_current_inventory BEFORE INSERT ON processor_meta WHEN NEW.key='current_workspace_inventory_v1:sha256' BEGIN SELECT RAISE(ABORT,'last current inventory hash'); END");
        Assert.ThrowsExactly<SqliteException>(()=>CompleteCurrentInventory(repository,input));
        Assert.IsNull(repository.ReadCurrentInventory());Assert.AreEqual(input.Legacy,repository.LastCheckpoint());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key GLOB 'current_workspace_inventory_v1*'"));
        fixture.Sql("DROP TRIGGER fail_current_inventory");Assert.AreEqual(input.Attempt.Id,CompleteCurrentInventory(repository,input).AttemptId);
    }

    [TestMethod]
    public void CurrentInventoryRequiresExplicitCompleteReplayAndKeepsUnknownRemovalRecoveryBlocking()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture,completeReplay:false);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>CompleteCurrentInventory(repository,input));
        var ready=repository.RememberHistoryPage(input.Attempt,input.Attempt.HistoryReplay!.Cursor,"inventory-end",true,
            [new("file","unclassified-removal",true,new string('d',64))],[]);
        input=input with {Attempt=ready};
        Assert.ThrowsExactly<WorkspaceHistoryRecoveryRequiredException>(()=>CompleteCurrentInventory(repository,input));
        Assert.ThrowsExactly<WorkspaceHistoryRecoveryRequiredException>(()=>CompleteCurrentInventory(repository,input));
        Assert.IsNull(repository.ReadCurrentInventory());Assert.AreEqual(input.Legacy,repository.LastCheckpoint());
    }

    [TestMethod]
    public void CurrentInventoryCannotCompleteByOmittingAnUnreadImmutableCandidate()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var pending=repository.RememberCandidates(input.Attempt,[new("unread-immutable",11,55)]);input=input with {Attempt=pending};
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>CompleteCurrentInventory(repository,input));
        Assert.IsTrue(repository.KnownIdentities(input.Context).Contains("unread-immutable"));Assert.IsNull(repository.ReadCurrentInventory());
    }

    [TestMethod]
    public void CurrentInventorySeparatesActualMutableRootHistoryWithoutPretendingItIsAnImmutableCurrentRead()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var pin=LibraryConnectionDescriptor.Decode(input.Signing.ConnectionCode);var native=new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(input.Fenced.SlotId)!;
        var root=new WorkspaceDocumentObservation(pin.CurrentFileId!,7,native.Envelope!.Value.Length,native.EnvelopeSha256!);
        repository.RememberRead(input.Context,root);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>repository.CompleteCurrentInventory(input.Attempt,"inventory-end",input.Inventory.Append(root).ToArray(),input.Signing,input.Fenced,input.Publications));
        var result=CompleteCurrentInventory(repository,input);Assert.AreEqual(2,result.Documents);
        Assert.AreEqual(root,repository.Known(input.Context).Single(value=>value.FileId==root.FileId));Assert.AreEqual(3,repository.Known(input.Context).Count);
        repository.AssertCurrentInventoryUnchanged(result,input.Signing,input.Fenced,input.Publications);
        Assert.AreEqual(input.Legacy,repository.LastCheckpoint());
    }

    [TestMethod]
    public void CurrentInventoryNewAttemptOrRootNonceInvalidatesCurrentUseWhileHistoricalReadSurvivesDisconnection()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var result=CompleteCurrentInventory(repository,input);_=repository.BeginScan(input.Context);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>repository.AssertCurrentInventoryUnchanged(result,input.Signing,input.Fenced,input.Publications));
        Assert.AreEqual(result,repository.ReadCurrentInventory());
        new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Begin(1);
        Assert.ThrowsExactly<CurrentControlObservationConflictException>(()=>repository.AssertCurrentInventoryUnchanged(result,input.Signing,input.Fenced,input.Publications));
        new DriveAccountRepository(fixture.Database).Disconnect(input.Context.AccountId,1);
        Assert.AreEqual(result,repository.ReadCurrentInventory());
    }

    [TestMethod]
    [DataRow("RootSha256")]
    [DataRow("RootSequence")]
    [DataRow("RootVersion")]
    [DataRow("AccountRevision")]
    [DataRow("AccountIdentity")]
    public void CurrentInventoryRetainedBindingRejectsSemanticDamageEvenWithARecomputedPairHash(string field)
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        CompleteCurrentInventory(repository,input);
        var encoded=(string)fixture.Scalar("SELECT value FROM processor_meta WHERE key='current_workspace_inventory_v1'");
        var node=JsonNode.Parse(encoded)!.AsObject();
        if(field=="AccountIdentity") node["Context"]!["AccountId"]="not-an-opaque-account@example.test";
        else if(field=="AccountRevision") node["Context"]![field]="9223372036854775807";
        else node[field]=field=="RootSha256" ? new string('f',64) : "9223372036854775807";
        var data=new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(node));var changed=System.Text.Encoding.UTF8.GetString(data);
        fixture.Sql("UPDATE processor_meta SET value='"+changed.Replace("'","''",StringComparison.Ordinal)+"' WHERE key='current_workspace_inventory_v1'");
        fixture.Sql("UPDATE processor_meta SET value='"+Convert.ToHexStringLower(SHA256.HashData(data))+"' WHERE key='current_workspace_inventory_v1:sha256'");
        Assert.ThrowsExactly<InvalidDataException>(()=>repository.ReadCurrentInventory());
    }

    [TestMethod]
    public void CurrentInventoryKeepsUnsignedNativeWorkButCannotAdoptUnstagedSealedPolicyWork()
    {
        using var fixture=new Fixture();Guid pending=Guid.Empty;
        var input=CurrentInventoryInput(fixture,beforeScan:value=>
        {
            var signing=new ProtocolSigningJournal(value.Database,value.Budget);
            pending=signing.Reserve(Guid.NewGuid(),value.ActorId,1,"library-publisher-authorization",sequence=>InitialPolicyBody(value.Policy(sequence,revoked:true))).Id;
        });
        var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);var result=CompleteCurrentInventory(repository,input);
        Assert.IsNull(new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(pending)!.Envelope);
        repository.AssertCurrentInventoryUnchanged(result,input.Signing,input.Fenced,input.Publications);
        var journal=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var reserved=journal.Read(pending)!;
        journal.Seal(pending,reserved.BodySha256,new ProtocolDocumentSigner(fixture.Controller).Sign(reserved.Body.Span));
        Assert.ThrowsExactly<CurrentControlSigningInventoryChangedException>(()=>repository.AssertCurrentInventoryUnchanged(result,input.Signing,input.Fenced,input.Publications));
        var refreshed=CurrentInventoryNextScan(fixture,input);
        Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(()=>CompleteCurrentInventory(repository,refreshed));
        Assert.AreEqual(result,repository.ReadCurrentInventory());Assert.AreEqual(pending,journal.Read(pending)!.Id);
    }

    [TestMethod]
    public void CurrentInventoryRequiresEveryVerifiedOutboxObjectAndKeepsItsOriginalCapturedIdentity()
    {
        using var fixture=new Fixture();WorkspacePublicationRecord? extra=null;
        var input=CurrentInventoryInput(fixture,beforeScan:value=>
        {
            var pin=value.Store.Controller()!;var actor=new ProtocolSigningJournal(value.Database,value.Budget).Inventory(pin.LibraryId).Actors.Single(entry=>entry.Role=="publisher");
            var configuration=new LibrarySourceRepository(value.Database).Snapshot().Revision;
            var scope=new WorkspacePublicationScope(pin.LibraryId,actor.ActorId,actor.Epoch,Guid.NewGuid(),pin.AccountId,1,pin.FolderId,configuration,new string('a',64));
            var publications=new WorkspacePublicationRepository(value.Database,value.Budget);
            extra=publications.Prepare(Guid.NewGuid(),scope,"facts-chunk","application/octet-stream",[1,2,3]);
            publications.AssignFileId(extra.Id,extra.Sha256,"verified-extra-facts");
            extra=publications.ConfirmVerified(extra.Id,pin.AccountId,pin.FolderId,"verified-extra-facts",extra.Sha256,3,9);
        });
        var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(()=>CompleteCurrentInventory(repository,input));
        Assert.IsNull(repository.ReadCurrentInventory());
        repository.RememberRead(input.Context,new(extra!.FileId!,9,3,extra.Sha256,"application/octet-stream"));
        input=input with {Inventory=repository.Known(input.Context).ToArray()};
        Assert.AreEqual(3,CompleteCurrentInventory(repository,input).Documents);
        var retained=new WorkspacePublicationRepository(fixture.Database,fixture.Budget).Read(extra.Id)!;
        Assert.AreEqual(extra.Id,retained.Id);Assert.AreEqual(extra.Scope,retained.Scope);Assert.AreEqual(extra.FileId,retained.FileId);
        Assert.AreEqual(extra.Sha256,retained.Sha256);Assert.AreEqual(extra.VerifiedVersion,retained.VerifiedVersion);
        CollectionAssert.AreEqual(extra.Payload.ToArray(),retained.Payload.ToArray());
    }

    [TestMethod]
    public void CurrentInventoryCancellationAndChangedNativeWorkNeverBecomeSuccessfulCompletion()
    {
        using var fixture=new Fixture();var input=CurrentInventoryInput(fixture);var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        using var stop=new CancellationTokenSource();stop.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>repository.CompleteCurrentInventory(input.Attempt,"inventory-end",input.Inventory,input.Signing,input.Fenced,input.Publications,stop.Token));
        var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);
        signing.Reserve(Guid.NewGuid(),fixture.ActorId,1,"library-publisher-authorization",sequence=>InitialPolicyBody(fixture.Policy(sequence,revoked:true)));
        Assert.ThrowsExactly<CurrentControlSigningInventoryChangedException>(()=>CompleteCurrentInventory(repository,input));
        Assert.IsNull(repository.ReadCurrentInventory());Assert.AreEqual(input.Legacy,repository.LastCheckpoint());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    private sealed record CurrentInventoryInputs(WorkspaceObservationContext Context,WorkspaceObservationAttempt Attempt,
        WorkspaceDocumentObservation[] Inventory,CurrentControlSigningInventory Signing,LocalCurrentControlAttempt Fenced,
        WorkspacePublicationInventory Publications,WorkspaceInventoryCheckpoint Legacy);

    private static CurrentInventoryInputs CurrentInventoryInput(Fixture fixture,bool completeReplay=true,Action<Fixture>? beforeScan=null)
    {
        var original=TransitionBootstrap(fixture);var pin=original.Slot.ConnectionDescriptor();
        beforeScan?.Invoke(fixture);
        var native=new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(original.Slot.Id)!;
        var actual=CaptureTransitionRoot(fixture,native.Envelope!.Value.ToArray(),7);
        var context=new WorkspaceObservationContext(pin.LibraryId,pin.Workspace.AccountId,pin.Workspace.FolderId,1,null);
        var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);var legacy=repository.LastCheckpoint()!;
        var attempt=repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(context),"inventory-start"));
        if(completeReplay) attempt=repository.RememberHistoryPage(attempt,attempt.HistoryReplay!.Cursor,"inventory-end",true,[],[]);
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(actual.Attempt);
        var fenced=new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Fence(actual.Attempt,actual.Metadata,native.EnvelopeSha256!);
        return new(context,attempt,repository.Known(context).ToArray(),signing,fenced,new WorkspacePublicationRepository(fixture.Database,fixture.Budget).Inventory(context),legacy);
    }

    private static CurrentInventoryInputs CurrentInventoryNextScan(Fixture fixture,CurrentInventoryInputs previous)
    {
        var native=new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(previous.Fenced.SlotId)!;
        var actual=CaptureTransitionRoot(fixture,native.Envelope!.Value.ToArray(),7);
        var repository=new WorkspaceObservationRepository(fixture.Database,fixture.Budget);
        var attempt=repository.StartHistoryReplay(repository.StartInventory(repository.BeginScan(previous.Context),"next-start"));
        attempt=repository.RememberHistoryPage(attempt,attempt.HistoryReplay!.Cursor,"inventory-end",true,[],[]);
        var signing=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget).CaptureSigningInventory(actual.Attempt);
        var fenced=new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Fence(actual.Attempt,actual.Metadata,native.EnvelopeSha256!);
        return new(previous.Context,attempt,repository.Known(previous.Context).ToArray(),signing,fenced,
            new WorkspacePublicationRepository(fixture.Database,fixture.Budget).Inventory(previous.Context),previous.Legacy);
    }

    private static CurrentWorkspaceInventoryCheckpoint CompleteCurrentInventory(WorkspaceObservationRepository repository,CurrentInventoryInputs input)=>
        repository.CompleteCurrentInventory(input.Attempt,"inventory-end",input.Inventory,input.Signing,input.Fenced,input.Publications);
}
