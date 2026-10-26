using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void CurrentTransitionReservesTheFixedBodyAndRetainsItsFirstSignatureAcrossRestart()
    {
        using var directory=new WorkerProcessTests.WorkerTestDirectory();CurrentControlTransitionPlan plan;byte[] sealedBytes;
        using(var fixture=new Fixture(directory.Path))
        {
            var current=TransitionBootstrap(fixture);var target=new CurrentControlTransitionTarget(current.Head.Publishers,current.Head.Sources,null);
            var store=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);plan=store.Prepare(Guid.NewGuid(),current.Fenced,target);
            Assert.AreEqual(4L,plan.Sequence);Assert.AreEqual(current.Slot.Id,plan.PreviousIntentId);Assert.AreEqual(current.Fenced.Id,plan.CapturedAttempt);
            var journal=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var reserved=journal.Read(plan.Id)!;
            CollectionAssert.AreEqual(new LibraryCurrentControlWriter(current.Slot.ConnectionDescriptor()).EncodeDocument(4,target.Publishers,target.Sources,null),reserved.Body.ToArray());
            var signed=journal.Seal(plan.Id,reserved.BodySha256,new ProtocolDocumentSigner(fixture.Controller).Sign(reserved.Body.Span));sealedBytes=signed.Envelope!.Value.ToArray();
            Assert.AreEqual(plan,store.Prepare(plan.Id,current.Fenced,target));Assert.AreEqual(plan,store.ReadAll().Single());Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
            Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        }
        using var database=new ProcessorStateDatabase(directory.Path);var budget=new StorageBudgetManager(new Space());
        Assert.AreEqual(plan,new CurrentControlTransitionJournal(database,budget).ReadAll().Single());
        CollectionAssert.AreEqual(sealedBytes,new ProtocolSigningJournal(database,budget).Read(plan.Id)!.Envelope!.Value.ToArray());
    }

    [TestMethod]
    public void CurrentTransitionRecoveryNeverRemintsOrRelabelsAfterConfigurationAndAccountChanges()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var store=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var target=new CurrentControlTransitionTarget(current.Head.Publishers,current.Head.Sources,null);var plan=store.Prepare(Guid.NewGuid(),current.Fenced,target);
        fixture.DisablePublicationSource();new DriveAccountRepository(fixture.Database).Disconnect("account-A",1);
        Assert.AreEqual(plan,store.Prepare(plan.Id,current.Fenced,target));Assert.AreEqual(1L,store.ReadAll().Single().AccountRevision);
        Assert.ThrowsExactly<CurrentControlTransitionInProgressException>(()=>store.Prepare(Guid.NewGuid(),current.Fenced,target));
        Assert.ThrowsExactly<ProtocolSigningConflictException>(()=>store.Prepare(plan.Id,current.Fenced,target with {CatalogPublicationId=Guid.NewGuid()}));
        Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CurrentTransitionLastCheckpointAndStorageFailureRollBackItsNativeCounterAndIntent()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var store=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var id=Guid.NewGuid();var target=new CurrentControlTransitionTarget(current.Head.Publishers,current.Head.Sources,null);
        fixture.Space.Available=1;Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>store.Prepare(id,current.Fenced,target));fixture.Space.Available=20*StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_root_plan BEFORE INSERT ON processor_meta WHEN NEW.key LIKE 'current_control_transition_v1:%:claim' BEGIN SELECT RAISE(ABORT,'last Root plan checkpoint'); END");
        Assert.ThrowsExactly<SqliteException>(()=>store.Prepare(id,current.Fenced,target));Assert.AreEqual(3L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
        Assert.AreEqual(0,store.ReadAll().Count);Assert.IsNull(new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(id));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_root_plan");Assert.AreEqual(4L,store.Prepare(id,current.Fenced,target).Sequence);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CurrentTransitionMissingOrCorruptPlanCannotAdoptAnOrphanRootEvenAfterItWasObserved(bool deleteAll)
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var store=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var target=new CurrentControlTransitionTarget(current.Head.Publishers,current.Head.Sources,null);var plan=store.Prepare(Guid.NewGuid(),current.Fenced,target);
        var signed=SealTransition(fixture,plan);_=CaptureTransitionRoot(fixture,signed.Envelope!.Value.ToArray(),8);
        if(deleteAll) fixture.Sql("DELETE FROM processor_meta WHERE key GLOB 'current_control_transition_v1:*'");
        else fixture.Sql("DELETE FROM processor_meta WHERE key='current_control_transition_v1:"+plan.Id.ToString("D")+":sha256'");
        if(deleteAll)
        {Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(()=>store.ReadAll());Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(()=>store.Prepare(Guid.NewGuid(),current.Fenced,target));}
        else
        {Assert.ThrowsExactly<InvalidDataException>(()=>store.ReadAll());Assert.ThrowsExactly<InvalidDataException>(()=>store.Prepare(Guid.NewGuid(),current.Fenced,target));}
        Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));Assert.IsNotNull(new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(plan.Id)!.Envelope);
    }

    [TestMethod]
    public void CurrentTransitionRevocationAndCanonicalReceiptCommitTogetherWithoutAnActivePublisher()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new ProtocolSigningJournal(fixture.Database,fixture.Budget);
        var revoked=journal.Reserve(Guid.NewGuid(),fixture.ActorId,1,"library-publisher-authorization",sequence=>InitialPolicyBody(fixture.Policy(sequence,revoked:true)));
        revoked=journal.Seal(revoked.Id,revoked.BodySha256,new ProtocolDocumentSigner(fixture.Controller).Sign(revoked.Body.Span));fixture.Store.SelectPublisherPolicy(revoked.Envelope!.Value.Span);
        var reference=new LibraryPolicyReference(new("account-A","revoked-file","metadata-workspace",revoked.Envelope!.Value.Length,revoked.EnvelopeSha256!),revoked.Sequence);
        var target=new CurrentControlTransitionTarget(reference,current.Head.Sources,null);var store=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=store.Prepare(Guid.NewGuid(),current.Fenced,target);var signed=SealTransition(fixture,plan);
        var captured=CaptureTransitionRoot(fixture,signed.Envelope!.Value.ToArray(),8);
        var graph=new LocalCurrentGraphObservations(fixture.Database,fixture.Budget);
        foreach(var policy in new[]{reference,current.Head.Sources})
        {
            var known=graph.RememberMetadata(captured.Attempt,policy.Reference,new(7,checked((int)policy.Reference.Bytes),"application/json",null,new string('a',64)));
            graph.RememberBytes(captured.Attempt,known,policy.Reference.Sha256);
        }
        var stages=new CurrentCatalogStageJournal(fixture.Database,fixture.Budget);var claim=stages.Begin(captured.Attempt,Guid.NewGuid());CurrentCatalogStageDescriptor descriptor;
        using(var area=new CurrentCatalogStageArea(fixture.Database.StateDirectory,fixture.Database.InstallationId,[claim],fixture.Budget))
        using(var index=area.Open(claim,fixture.Budget)) descriptor=index.Seal(0);
        claim=stages.RecordSealed(claim,captured.Attempt,descriptor);
        var fenced=new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Fence(captured.Attempt,captured.Metadata,signed.EnvelopeSha256!);
        var source=journal.Read(current.SourceIntentId)!.Envelope!.Value;var activation=new CurrentCatalogActivationRepository(fixture.Database,fixture.Budget);
        fixture.Sql("CREATE TRIGGER fail_root_activation BEFORE INSERT ON processor_meta WHEN NEW.key='current_catalog_active_v1:sha256' BEGIN SELECT RAISE(ABORT,'last activation checkpoint'); END");
        Assert.ThrowsExactly<SqliteException>(()=>activation.Activate(fenced,claim,revoked.Envelope.Value,source,0,0,[]));
        Assert.IsNull(activation.Read());Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key LIKE 'current_control_transition_v1:%:activated%'"));
        Assert.ThrowsExactly<CurrentControlTransitionInProgressException>(()=>store.Prepare(Guid.NewGuid(),fenced,target));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_root_activation");var accepted=activation.Activate(fenced,claim,revoked.Envelope.Value,source,0,0,[]);
        Assert.IsNull(accepted.PublisherId);Assert.AreEqual(2L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key LIKE 'current_control_transition_v1:%:activated%'"));
        var reinstated=journal.Reserve(Guid.NewGuid(),fixture.ActorId,1,"library-publisher-authorization",sequence=>InitialPolicyBody(fixture.Policy(sequence)));
        reinstated=journal.Seal(reinstated.Id,reinstated.BodySha256,new ProtocolDocumentSigner(fixture.Controller).Sign(reinstated.Body.Span));
        var reinstatedTarget=target with {Publishers=new(new("account-A","reinstated-file","metadata-workspace",reinstated.Envelope!.Value.Length,reinstated.EnvelopeSha256!),reinstated.Sequence)};
        Assert.ThrowsExactly<ProtocolSigningConflictException>(()=>store.Prepare(Guid.NewGuid(),fenced,reinstatedTarget));
        Assert.AreEqual(6L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
        Assert.AreEqual(7L,store.Prepare(Guid.NewGuid(),fenced,target).Sequence);Assert.AreEqual(plan,store.ReadAll().Single(value=>value.Id==plan.Id));
    }

    [TestMethod]
    public void CurrentTransitionRequiresTheLiveNonceAndExactHumanSourcesBeforeReservingAnything()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var store=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var target=new CurrentControlTransitionTarget(current.Head.Publishers,current.Head.Sources,null);fixture.DisablePublicationSource();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>store.Prepare(Guid.NewGuid(),current.Fenced,target));
        new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Begin(1);
        Assert.ThrowsExactly<CurrentControlObservationConflictException>(()=>store.Prepare(Guid.NewGuid(),current.Fenced,target));
        Assert.AreEqual(0,store.ReadAll().Count);Assert.AreEqual(3L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
    }

    [TestMethod]
    public void CurrentTransitionBindsTheVerifiedOutboxToItsOriginalCapturedGeneration()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var publication=PreparedPublication(fixture);
        var capture=publication.Capture;var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);
        var intent=signing.Reserve(capture.CommitIntentId,capture.Scope.ActorId,capture.Scope.ActorEpoch,"catalog-commit",sequence=>publication.Plan!.EncodeBody(capture.Scope,sequence));
        intent=signing.Seal(intent.Id,intent.BodySha256,new ProtocolDocumentSigner(fixture.Publisher).Sign(intent.Body.Span));
        new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget).ConfirmCommitSealed(capture.Scope.GenerationId);
        var outbox=new WorkspacePublicationRepository(fixture.Database,fixture.Budget);var row=outbox.Prepare(intent.Id,capture.Scope,"catalog-commit","application/json",intent.Envelope!.Value.Span);outbox.AssignFileId(row.Id,row.Sha256,"selected-captured-commit");
        var target=new CurrentControlTransitionTarget(current.Head.Publishers,current.Head.Sources,row.Id);var id=Guid.NewGuid();var store=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        Assert.ThrowsExactly<CatalogGenerationCaptureConflictException>(()=>store.Prepare(id,current.Fenced,target));Assert.AreEqual(3L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
        outbox.ConfirmVerified(row.Id,capture.Scope.AccountId,capture.Scope.FolderId,"selected-captured-commit",row.Sha256,row.Payload.Length,11);
        var plan=store.Prepare(id,current.Fenced,target);Assert.AreEqual(capture.Scope.GenerationId,plan.GenerationId);Assert.AreEqual(row.Id,plan.Target.CatalogPublicationId);
        Assert.AreEqual("selected-captured-commit",plan.Catalog!.FileId);var retained=new CatalogGenerationCaptureRepository(fixture.Database,fixture.Budget).Read(capture.Scope.GenerationId)!;
        Assert.AreEqual(capture.Scope,retained.Scope);Assert.AreEqual(capture.SnapshotId,retained.SnapshotId);Assert.AreEqual(capture.CommitIntentId,retained.CommitIntentId);
        Assert.AreEqual(capture.Snapshot,retained.Snapshot);Assert.AreEqual("commit-sealed",retained.Phase);
        Assert.AreEqual(capture.SnapshotId,publication.Capture.SnapshotId);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void BorrowedSigningReservationHoldsAdmissionThroughCommitAndDoesNotSeal()
    {
        using var fixture=new Fixture();_=TransitionBootstrap(fixture);var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var id=Guid.NewGuid();
        using(var transaction=fixture.Database.Connection.BeginTransaction())
        {
            using var reservation=signing.ReserveIn(id,fixture.ActorId,1,"library-source-configuration",sequence=>InitialPolicyBody(fixture.SourcePolicy(sequence,2,sourceRevision:1)),transaction);
            Assert.IsGreaterThan(0L,fixture.Budget.Status().ReservedBytes);Assert.IsNull(reservation.Value.Envelope);reservation.Recheck();transaction.Commit();
            Assert.IsGreaterThan(0L,fixture.Budget.Status().ReservedBytes);
        }
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);Assert.IsNull(signing.Read(id)!.Envelope);
    }

    private static (LibraryCurrentControlSlot Slot,LibraryCurrentControl Head,LocalCurrentControlAttempt Fenced,Guid SourceIntentId) TransitionBootstrap(Fixture fixture)
    {
        var completion=CompleteControlBootstrap(fixture);var slots=Slots(fixture);var slot=slots.CaptureAllocatedFile(slots.Prepare(),"account-A",1,"control-file");
        var journal=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var publishers=ControlPolicy(completion.Publisher,journal.Read(completion.Plan.PublisherIntentId)!.Sequence);
        var sources=ControlPolicy(completion.Sources,journal.Read(completion.Plan.SourceIntentId)!.Sequence);var pin=slot.ConnectionDescriptor();
        var initial=journal.Reserve(slot.Id,pin.ControllerId,pin.ControllerEpoch,LibraryCurrentControl.Kind,sequence=>new LibraryCurrentControlWriter(pin).EncodeDocument(sequence,publishers,sources,null));
        initial=journal.Seal(initial.Id,initial.BodySha256,new ProtocolDocumentSigner(fixture.Controller).Sign(initial.Body.Span));
        new InitialCurrentControlPublicationRepository(fixture.Database,fixture.Budget).RecordObserved(slot,initial.EnvelopeSha256!,7,1);
        var captured=CaptureTransitionRoot(fixture,initial.Envelope!.Value.ToArray(),7);var fenced=new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Fence(captured.Attempt,captured.Metadata,initial.EnvelopeSha256!);
        return(slot,LibraryCurrentControl.Read(initial.Envelope.Value.Span,pin),fenced,completion.Plan.SourceIntentId);
    }
    private static (LocalCurrentControlAttempt Attempt,LocalCurrentControlMetadata Metadata) CaptureTransitionRoot(Fixture fixture,byte[] bytes,long version)
    {
        var observations=new LocalCurrentControlObservations(fixture.Database,fixture.Budget);var open=observations.Begin(1);
        var metadata=new LocalCurrentControlMetadata(version,bytes.Length,"\"observed-"+version.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\"",new string('a',64),null);
        var reading=observations.RememberMetadata(open,metadata);var read=observations.RememberBytes(reading,bytes);return(observations.Observe(read,bytes).Attempt,metadata);
    }
    private static ProtocolSigningIntent SealTransition(Fixture fixture,CurrentControlTransitionPlan plan)
    {
        var journal=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var intent=journal.Read(plan.Id)!;
        return journal.Seal(plan.Id,plan.BodySha256,new ProtocolDocumentSigner(fixture.Controller).Sign(intent.Body.Span));
    }
}
