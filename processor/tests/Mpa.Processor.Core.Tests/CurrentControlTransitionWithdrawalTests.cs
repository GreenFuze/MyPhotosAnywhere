using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void CurrentWithdrawalPreservesUnsignedWorkAcrossRestartWithinTheExistingFiveRows()
    {
        using var directory=new WorkerProcessTests.WorkerTestDirectory();CurrentControlTransitionWithdrawal receipt;CurrentControlTransitionPlan plan;byte[] body;
        using(var fixture=new Fixture(directory.Path))
        {
            var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
            plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
            body=new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(plan.Id)!.Body.ToArray();
            receipt=journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled);
            Assert.AreEqual(current.Head.EnvelopeSha256,receipt.RootSha256);Assert.AreEqual(3L,receipt.RootSequence);Assert.AreEqual(7L,receipt.DriveVersion);
            Assert.AreEqual(plan.BodySha256,receipt.BodySha256);Assert.AreEqual(5L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key GLOB 'current_control_transition_v1:*'"));
            Assert.AreEqual(0,journal.Pending().Count);Assert.AreEqual(plan,journal.ReadAll().Single());Assert.AreEqual(receipt,journal.ReadWithdrawal(plan.Id));
            Assert.ThrowsExactly<CurrentControlTransitionWithdrawnException>(()=>journal.ReadForSigning(plan.Id));
            Assert.IsNull(new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(plan.Id)!.Envelope);
        }
        using var database=new ProcessorStateDatabase(directory.Path);var budget=new StorageBudgetManager(new Space());var reopened=new CurrentControlTransitionJournal(database,budget);
        Assert.AreEqual(receipt,reopened.ReadWithdrawal(plan.Id));Assert.AreEqual(plan,reopened.ReadAll().Single());
        CollectionAssert.AreEqual(body,new ProtocolSigningJournal(database,budget).Read(plan.Id)!.Body.ToArray());Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CurrentWithdrawalLowSpaceAndLastRowFailureKeepTheOriginalPendingWorkAtomic()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        fixture.Space.Available=1;Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled));
        fixture.Space.Available=20*StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_withdrawal BEFORE INSERT ON processor_meta WHEN NEW.key LIKE 'current_control_transition_v1:%:activated:sha256' BEGIN SELECT RAISE(ABORT,'last terminal row'); END");
        Assert.ThrowsExactly<SqliteException>(()=>journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled));
        Assert.IsNull(journal.ReadWithdrawal(plan.Id));Assert.AreEqual(plan,journal.Pending().Single());
        Assert.AreEqual(3L,fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key GLOB 'current_control_transition_v1:*'"));
        Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_withdrawal");Assert.AreEqual(plan.Id,journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled).TransitionId);
    }

    [TestMethod]
    public void CurrentWithdrawalSupersededHumanPolicyPermitsSeparateNewWorkWithoutRetargetingTheOldPlan()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var old=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        var signing=new ProtocolSigningJournal(fixture.Database,fixture.Budget);var body=signing.Read(old.Id)!.Body.ToArray();
        var revoked=signing.Reserve(Guid.NewGuid(),fixture.ActorId,1,"library-publisher-authorization",sequence=>InitialPolicyBody(fixture.Policy(sequence,revoked:true)));
        revoked=signing.Seal(revoked.Id,revoked.BodySha256,new ProtocolDocumentSigner(fixture.Controller).Sign(revoked.Body.Span));fixture.Store.SelectPublisherPolicy(revoked.Envelope!.Value.Span);
        var receipt=journal.Withdraw(old.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.ConfigurationChanged);
        Assert.AreEqual(old.Id,receipt.TransitionId);
        var policy=new LibraryPolicyReference(new("account-A","new-revocation","metadata-workspace",revoked.Envelope!.Value.Length,revoked.EnvelopeSha256!),revoked.Sequence);
        var next=journal.Prepare(Guid.NewGuid(),current.Fenced,new(policy,current.Head.Sources,null));
        Assert.AreEqual(6L,next.Sequence);Assert.AreEqual(4L,old.Sequence);Assert.AreEqual(old,journal.Prepare(old.Id,current.Fenced,old.Target));
        CollectionAssert.AreEqual(body,signing.Read(old.Id)!.Body.ToArray());Assert.IsNull(signing.Read(old.Id)!.Envelope);Assert.AreEqual(next,journal.Pending().Single());
        Assert.AreEqual(receipt,journal.Withdraw(old.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.ConfigurationChanged));
        Assert.ThrowsExactly<CurrentControlObservationConflictException>(()=>journal.Withdraw(old.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CurrentWithdrawalMissingOrCorruptTerminalEvidenceCannotResumeOrRemint(bool missing)
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled);
        if(missing) fixture.Sql("DELETE FROM processor_meta WHERE key='current_control_transition_v1:"+plan.Id.ToString("D")+":activated:sha256'");
        else fixture.Sql("UPDATE processor_meta SET value='{}' WHERE key='current_control_transition_v1:"+plan.Id.ToString("D")+":activated'");
        Assert.ThrowsExactly<InvalidDataException>(()=>journal.ReadAll());Assert.ThrowsExactly<InvalidDataException>(()=>journal.ReadForSigning(plan.Id));
        Assert.ThrowsExactly<InvalidDataException>(()=>journal.Prepare(Guid.NewGuid(),current.Fenced,plan.Target));
        Assert.AreEqual(4L,fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
    }

    [TestMethod]
    public void CurrentWithdrawalRequiresTheActualCurrentFenceAndAnHonestConfigurationReason()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        Assert.ThrowsExactly<InvalidOperationException>(()=>journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.ConfigurationChanged));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>journal.Withdraw(plan.Id,current.Fenced,(CurrentControlTransitionWithdrawalReason)99));
        new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Begin(1);
        Assert.ThrowsExactly<CurrentControlObservationConflictException>(()=>journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled));
        Assert.AreEqual(plan,journal.Pending().Single());Assert.IsNull(journal.ReadWithdrawal(plan.Id));
    }

    [TestMethod]
    [DataRow("Reason")]
    [DataRow("BodySha256")]
    [DataRow("RootSha256")]
    [DataRow("StageId")]
    public void CurrentWithdrawalSemanticCorruptionFailsEvenWithARecomputedPairedHash(string field)
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));
        journal.Withdraw(plan.Id,current.Fenced,CurrentControlTransitionWithdrawalReason.Cancelled);
        var key="current_control_transition_v1:"+plan.Id.ToString("D")+":activated";
        using var read=fixture.Database.Connection.CreateCommand();read.CommandText="SELECT value FROM processor_meta WHERE key=$key";read.Parameters.AddWithValue("$key",key);
        var value=System.Text.Json.Nodes.JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
        if(field=="Reason") value["Withdrawal"]![field]="unsupported";
        else if(field=="BodySha256") value["Withdrawal"]![field]=new string('b',64);
        else if(field=="RootSha256") value[field]=plan.BodySha256;
        else value[field]=Guid.NewGuid().ToString("D");
        var body=System.Text.Encoding.UTF8.GetString(new CanonicalJson(16384).Encode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value)));
        var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
        using var update=fixture.Database.Connection.CreateCommand();update.CommandText="UPDATE processor_meta SET value=CASE WHEN key=$key THEN $body ELSE $hash END WHERE key=$key OR key=$key||':sha256'";
        update.Parameters.AddWithValue("$key",key);update.Parameters.AddWithValue("$body",body);update.Parameters.AddWithValue("$hash",hash);Assert.AreEqual(2,update.ExecuteNonQuery());
        Assert.ThrowsExactly<InvalidDataException>(()=>journal.ReadWithdrawal(plan.Id));Assert.ThrowsExactly<InvalidDataException>(()=>journal.Pending());
        Assert.ThrowsExactly<InvalidDataException>(()=>journal.Prepare(Guid.NewGuid(),current.Fenced,plan.Target));
    }

    [TestMethod]
    public void CurrentWithdrawalCanConsumeAnActuallyAppliedSealedRootWithoutActivationOrSnapshotCompletion()
    {
        using var fixture=new Fixture();var current=TransitionBootstrap(fixture);var journal=new CurrentControlTransitionJournal(fixture.Database,fixture.Budget);
        var plan=journal.Prepare(Guid.NewGuid(),current.Fenced,new(current.Head.Publishers,current.Head.Sources,null));var signed=SealTransition(fixture,plan);
        var observed=CaptureTransitionRoot(fixture,signed.Envelope!.Value.ToArray(),8);
        var fence=new LocalCurrentControlObservations(fixture.Database,fixture.Budget).Fence(observed.Attempt,observed.Metadata,signed.EnvelopeSha256!);
        var result=journal.Withdraw(plan.Id,fence,CurrentControlTransitionWithdrawalReason.Cancelled);
        Assert.AreEqual(plan.Sequence,result.RootSequence);Assert.AreEqual(signed.EnvelopeSha256,result.RootSha256);Assert.AreEqual(8L,result.DriveVersion);
        Assert.IsNull(new CurrentCatalogActivationRepository(fixture.Database,fixture.Budget).Read());
        Assert.ThrowsExactly<CurrentControlTransitionWithdrawnException>(()=>journal.AssertPublicationCurrent(plan.Id,fence,[]));
        Assert.ThrowsExactly<CurrentControlTransitionWithdrawnException>(()=>journal.ReadForSigning(plan.Id));
        CollectionAssert.AreEqual(signed.Envelope.Value.ToArray(),new ProtocolSigningJournal(fixture.Database,fixture.Budget).Read(plan.Id)!.Envelope!.Value.ToArray());
    }
}
