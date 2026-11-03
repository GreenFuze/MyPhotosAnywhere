using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentWithdrawalNeverSignsOrPutsUnsignedWorkAndReplaysHistoricallyAfterDisconnection()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var before=(await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Body.ToArray();
        var receipt=await fixture.Reader.WithdrawTransitionAsync(plan.Id,CurrentControlTransitionWithdrawalReason.Cancelled,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(plan.Id,receipt.TransitionId);Assert.AreEqual(plan.PreviousSha256,receipt.RootSha256);Assert.AreEqual(7L,receipt.DriveVersion);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.IsNull((await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Envelope);
        CollectionAssert.AreEqual(before,(await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Body.ToArray());
        var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlTransitionWithdrawnException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(3,fixture.Data.Allocations);Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(receipt,await fixture.Reader.WithdrawTransitionAsync(plan.Id,CurrentControlTransitionWithdrawalReason.Cancelled,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentWithdrawalOfSupersededPolicyAllowsANewCapturedRevocationWithoutRetargetingOldWork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var old=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        await SelectNewerTransitionPolicyAsync(fixture,false).ConfigureAwait(false);
        var withdrawn=await fixture.Reader.WithdrawTransitionAsync(old.Id,CurrentControlTransitionWithdrawalReason.ConfigurationChanged,CancellationToken.None).ConfigureAwait(false);
        var signed=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var selected=new LibraryAuthorityRepository(database,fixture.Data.Budget).CurrentPublisherPolicy()!;
            var intent=new ProtocolSigningJournal(database,fixture.Data.Budget).Inventory(fixture.Data.Setup.Keys.PublicIdentity.LibraryId).Intents.Single(value=>value.Kind=="library-publisher-authorization" && value.Sequence==selected.Sequence);
            return new ProtocolSigningJournal(database,fixture.Data.Budget).Read(intent.Id)!;
        },CancellationToken.None).ConfigureAwait(false);
        var bytes=signed.Envelope!.Value.ToArray();fixture.Data.Cloud["withdrawal-revocation"]=new("permission-A","withdrawal-revocation","metadata",8,bytes);
        var policy=new LibraryPolicyReference(new("permission-A","withdrawal-revocation","metadata",bytes.Length,signed.EnvelopeSha256!),signed.Sequence);
        var next=await PrepareTransitionAsync(fixture,publisher:policy).ConfigureAwait(false);
        Assert.AreEqual(6L,next.Sequence);Assert.AreEqual(4L,old.Sequence);Assert.AreEqual(old.Id,withdrawn.TransitionId);
        var result=await fixture.Reader.PublishTransitionAsync(next.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.Http.Puts);Assert.AreEqual(8L,result.DriveVersion);Assert.AreEqual(next.Id,result.TransitionId);
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget).ReadAll(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(old,retained.Single(value=>value.Id==old.Id));Assert.IsNull((await TransitionIntentAsync(fixture,old.Id).ConfigureAwait(false)).Envelope);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);Assert.IsNull(policies.Publishers.Publisher);
        Assert.AreEqual(3,fixture.Data.Allocations);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentWithdrawalOfAlreadyAppliedWorkCannotTurnIntoCanonicalActivation()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var uploaded=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        var receipt=await fixture.Reader.WithdrawTransitionAsync(plan.Id,CurrentControlTransitionWithdrawalReason.Cancelled,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(uploaded.RootSha256,receipt.RootSha256);Assert.AreEqual(plan.Sequence,receipt.RootSequence);Assert.AreEqual(1,fixture.Http.Puts);
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var ready=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=ready.Stage;
        await Assert.ThrowsExactlyAsync<CurrentControlTransitionWithdrawnException>(()=>fixture.Reader.ActivateAsync(ready.Fence,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(1,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
        Assert.AreEqual(receipt,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget).ReadWithdrawal(plan.Id),CancellationToken.None).ConfigureAwait(false));
        await stage.DisposeAsync().ConfigureAwait(false);await pool.RetireAsync(ready.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentWithdrawalJoinsAnOlderActualAccountOperationAndInvalidatesItsPermit(bool actualWrite)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOnExit=new TestCallbackReleaseOwner(release);
        if(actualWrite) fixture.Http.AfterPut=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        else
        {
            var replace=fixture.Data.Setup.Accounts.Readers.ConditionalAdmittedReplace!;
            fixture.Data.Setup.Accounts.Readers.ConditionalAdmittedReplace=(expected,bytes,hash,admit,token)=>
            {
                fixture.Http.BeforeMetadata=async file=>{if(file=="captured-3") {entered.TrySetResult();await release.Task.ConfigureAwait(false);}};
                return replace(expected,bytes,hash,admit,token);
            };
        }
        var publishing=fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None);
        _ =await Task.WhenAny(entered.Task,publishing).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if(publishing.IsCompleted) {await publishing.ConfigureAwait(false);Assert.IsTrue(entered.Task.IsCompleted);}
        var prior=await CurrentStateAsync(fixture.Data,store=>store.Current()!).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var withdrawing=other.WithdrawTransitionAsync(plan.Id,CurrentControlTransitionWithdrawalReason.Cancelled,CancellationToken.None);
        try
        {
            var timer=Stopwatch.StartNew();
            while((await CurrentStateAsync(fixture.Data,store=>store.Current()!).ConfigureAwait(false)).Id==prior.Id)
            {
                if(timer.Elapsed>=TimeSpan.FromSeconds(10)) throw new TimeoutException("Withdrawal did not durably invalidate the older nonce.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            Assert.IsFalse(publishing.IsCompleted);Assert.IsFalse(withdrawing.IsCompleted);Assert.IsFalse(fixture.Http.Disposed);
            Assert.AreEqual(actualWrite ? 1 : 0,fixture.Http.Puts);Assert.AreEqual(0,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally {fixture.Http.BeforeMetadata=null;fixture.Http.AfterPut=null;release.TrySetResult();}
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>publishing).ConfigureAwait(false);
        var receipt=await withdrawing.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(actualWrite ? plan.Sequence : plan.PreviousSequence,receipt.RootSequence);Assert.AreEqual(actualWrite ? 8L : 7L,receipt.DriveVersion);
        Assert.AreEqual(actualWrite ? 1 : 0,fixture.Http.Puts);var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlTransitionWithdrawnException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Data.Allocations);
    }
}
