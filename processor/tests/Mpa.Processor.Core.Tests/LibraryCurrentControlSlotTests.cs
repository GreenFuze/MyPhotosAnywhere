using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    private static LibraryCurrentControlSlotRepository Slots(Fixture fixture) => new(fixture.Database,fixture.Budget);
    private static LibraryInitialPublicationCompletion CompleteControlBootstrap(Fixture fixture)
    {
        var work=fixture.PrepareCompletionEvidence();return fixture.Store.CompleteInitialPublication(work.Plan,work.Checkpoint);
    }

    [TestMethod]
    public void ControlSlotRequiresCompletedBootstrapAndKeepsOneImmutablePlan()
    {
        using var fixture=new Fixture();var slots=Slots(fixture);
        Assert.IsNull(slots.Current());Assert.ThrowsExactly<InvalidOperationException>(()=>slots.Prepare());
        CompleteControlBootstrap(fixture);var plan=slots.Prepare();Assert.AreEqual(plan,slots.Prepare());Assert.IsNull(plan.FileId);
        Assert.ThrowsExactly<InvalidOperationException>(()=>plan.ConnectionDescriptor());Assert.AreEqual(13L,fixture.Scalar("PRAGMA user_version"));
    }

    [TestMethod]
    public void ActualControlIdSurvivesReopenWithoutReplacingThePlanOrItsRevision()
    {
        using var directory=new WorkerProcessTests.WorkerTestDirectory();LibraryCurrentControlSlot saved;
        using(var fixture=new Fixture(directory.Path))
        {
            CompleteControlBootstrap(fixture);var slots=Slots(fixture);var plan=slots.Prepare();saved=slots.CaptureAllocatedFile(plan,"account-A",1,"control-file");
            Assert.AreEqual(saved,slots.CaptureAllocatedFile(plan,"account-A",1,"control-file"));Assert.AreEqual(saved,slots.Prepare());
            Assert.AreEqual("control-file",saved.ConnectionDescriptor().CurrentFileId);Assert.AreEqual(1L,saved.AccountRevision);
        }
        using var database=new ProcessorStateDatabase(directory.Path);Assert.AreEqual(saved,new LibraryCurrentControlSlotRepository(database,new(new Space())).Current());
    }

    [TestMethod]
    public void ControlIdCannotRetargetItsAccountFolderPlanOrAnImmutablePublication()
    {
        using var fixture=new Fixture();CompleteControlBootstrap(fixture);var slots=Slots(fixture);var plan=slots.Prepare();
        Assert.ThrowsExactly<InvalidOperationException>(()=>slots.CaptureAllocatedFile(plan with { Id=Guid.NewGuid() },"account-A",1,"control-file"));
        Assert.ThrowsExactly<InvalidOperationException>(()=>slots.CaptureAllocatedFile(plan,"foreign",1,"control-file"));
        Assert.ThrowsExactly<InvalidOperationException>(()=>slots.CaptureAllocatedFile(plan,"account-A",0,"control-file"));
        foreach(var file in new[]{"root","metadata-workspace","publisher-file","source-file"})
            Assert.ThrowsExactly<InvalidOperationException>(()=>slots.CaptureAllocatedFile(plan,"account-A",1,file));
        var captured=slots.CaptureAllocatedFile(plan,"account-A",1,"control-file");
        Assert.ThrowsExactly<InvalidOperationException>(()=>slots.CaptureAllocatedFile(plan,"account-A",1,"replacement"));
        Assert.ThrowsExactly<InvalidOperationException>(()=>slots.CaptureAllocatedFile(plan,"account-A",2,"control-file"));Assert.AreEqual(captured,slots.Current());
    }

    [TestMethod]
    public void ControlReceiptCanBeRetainedAfterDisconnectButCannotGrantFreshAccess()
    {
        using var fixture=new Fixture();CompleteControlBootstrap(fixture);var slots=Slots(fixture);var plan=slots.Prepare();
        new DriveAccountRepository(fixture.Database).Disconnect("account-A",1);
        var captured=slots.CaptureAllocatedFile(plan,"account-A",1,"control-file");Assert.AreEqual(captured,slots.Current());
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>slots.Prepare());Assert.AreEqual(1L,captured.AccountRevision);
    }

    [TestMethod]
    public void FailedControlCheckpointsRollBackAllKeysAndRetryTheSameActualId()
    {
        using var fixture=new Fixture();CompleteControlBootstrap(fixture);var slots=Slots(fixture);fixture.Space.Available=1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>slots.Prepare());Assert.IsNull(slots.Current());
        fixture.Space.Available=20*StorageBudgetManager.Gibibyte;var plan=slots.Prepare();
        fixture.Sql("CREATE TRIGGER fail_control_receipt BEFORE INSERT ON processor_meta WHEN NEW.key='current_control_file_claim_v1' BEGIN SELECT RAISE(ABORT,'synthetic control receipt failure'); END");
        Assert.ThrowsExactly<SqliteException>(()=>slots.CaptureAllocatedFile(plan,"account-A",1,"control-file"));Assert.IsNull(slots.Current()!.FileId);
        Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key GLOB 'current_control_file*'"));
        fixture.Sql("DROP TRIGGER fail_control_receipt");Assert.AreEqual("control-file",slots.CaptureAllocatedFile(plan,"account-A",1,"control-file").FileId);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void LostControlIdentityRecordsNeverBecomeAReplacementReservation()
    {
        foreach(var key in new[]{"current_control_slot_v1","current_control_slot_v1:sha256","current_control_slot_claim_v1","current_control_file_v1","current_control_file_v1:sha256","current_control_file_claim_v1"})
        {
            using var fixture=new Fixture();CompleteControlBootstrap(fixture);var slots=Slots(fixture);slots.CaptureAllocatedFile(slots.Prepare(),"account-A",1,"control-file");
            fixture.Sql("DELETE FROM processor_meta WHERE key='"+key+"'");Assert.ThrowsExactly<InvalidDataException>(()=>slots.Current());Assert.ThrowsExactly<InvalidDataException>(()=>slots.Prepare());
        }
    }

    [TestMethod]
    public void AControlSlotCannotHideLossOfItsIndependentControllerPin()
    {
        using var fixture=new Fixture();CompleteControlBootstrap(fixture);Slots(fixture).Prepare();
        fixture.Sql("DELETE FROM processor_meta WHERE key IN('library_controller_v1','library_controller_v1:sha256')");
        Assert.ThrowsExactly<InvalidDataException>(()=>fixture.Store.Controller());Assert.ThrowsExactly<InvalidDataException>(()=>Slots(fixture).Current());
    }

    [TestMethod]
    public void CurrentControlUsesTheSameDurableRootCounterAndPreservesItsFirstSignature()
    {
        using var fixture=new Fixture();var completion=CompleteControlBootstrap(fixture);var slots=Slots(fixture);var slot=slots.Prepare();
        var journal=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var id=Guid.NewGuid();var pin=fixture.Store.Controller()!;
        var publishers=ControlPolicy(completion.Publisher,journal.Read(completion.Plan.PublisherIntentId)!.Sequence);
        var sources=ControlPolicy(completion.Sources,journal.Read(completion.Plan.SourceIntentId)!.Sequence);
        var prospective=new LibraryConnectionDescriptor(pin.LibraryId,pin.ActorId,pin.Epoch,new(pin.AccountId,pin.FolderId),Convert.FromBase64String(pin.PublicKeySpki),"control-file");
        Assert.ThrowsExactly<InvalidOperationException>(()=>journal.Reserve(id,pin.ActorId,pin.Epoch,LibraryCurrentControl.Kind,n=>new LibraryCurrentControlWriter(prospective).EncodeDocument(n,publishers,sources,null)));
        slot=slots.CaptureAllocatedFile(slot,"account-A",1,"control-file");var writer=new LibraryCurrentControlWriter(slot.ConnectionDescriptor());
        var intent=journal.Reserve(id,pin.ActorId,pin.Epoch,LibraryCurrentControl.Kind,n=>writer.EncodeDocument(n,publishers,sources,null));Assert.AreEqual(3L,intent.Sequence);
        var signature=new ProtocolDocumentSigner(fixture.Controller).Sign(intent.Body.Span);var sealedIntent=journal.Seal(id,intent.BodySha256,signature);
        Assert.AreEqual(sealedIntent.EnvelopeSha256,journal.Seal(id,intent.BodySha256,signature).EnvelopeSha256);
        var control=LibraryCurrentControl.Read(sealedIntent.Envelope!.Value.Span,slot.ConnectionDescriptor());Assert.AreEqual(3L,control.Sequence);
        Assert.AreEqual(sealedIntent.EnvelopeSha256,journal.Read(id)!.EnvelopeSha256);Assert.AreEqual(3L,journal.Inventory(pin.LibraryId).Actors.Single(actor=>actor.Role=="controller").Sequence);
        fixture.Sql("DELETE FROM processor_meta WHERE key GLOB 'current_control_file*'");Assert.ThrowsExactly<InvalidDataException>(()=>slots.Current());
        Assert.ThrowsExactly<InvalidDataException>(()=>journal.Read(id));
    }

    private static LibraryPolicyReference ControlPolicy(WorkspaceDocumentObservation observed,long sequence) =>
        new(new("account-A",observed.FileId,"metadata-workspace",observed.Bytes,observed.Sha256),sequence);
}
