using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentTransitionPublishesSameFileWithStrongConditionAndRequiresSeparateCanonicalAdmission()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var result=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(plan.Id,result.TransitionId);Assert.AreEqual(8L,result.DriveVersion);Assert.IsFalse(result.AlreadyPresent);
        Assert.AreEqual(1,fixture.Http.Puts);Assert.AreEqual(3,fixture.Data.Allocations);Assert.AreEqual(3,fixture.Data.Creates);
        Assert.AreEqual(result.RootSha256,CurrentHash(fixture.Data.Cloud["captured-3"].Bytes.ToArray()));
        Assert.AreEqual("\"actual-7\"",fixture.Http.PutConditions.Single());Assert.AreEqual("captured-3",fixture.Http.PutFiles.Single());
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        var signed=await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false);
        var again=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(again.AlreadyPresent);Assert.AreEqual(1,fixture.Http.Puts);Assert.AreEqual(result.RootSha256,again.RootSha256);
        CollectionAssert.AreEqual(signed.Envelope!.Value.ToArray(),(await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Envelope!.Value.ToArray());
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(plan.Sequence,capture.Head.Sequence);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentTransitionLostAcknowledgementReopensSameWorkWithoutAnotherPutOrCounter()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);fixture.Http.LosePutAcknowledgement=true;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        var signed=await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false);Assert.AreEqual(1,fixture.Http.Puts);
        await fixture.Reader.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(fixture.Reader.ExitConfirmed);
        await using var resumed=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var result=await resumed.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(result.AlreadyPresent);Assert.AreEqual(8L,result.DriveVersion);Assert.AreEqual(1,fixture.Http.Puts);Assert.AreEqual(signed.EnvelopeSha256,result.RootSha256);
        Assert.AreEqual(plan,await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget).ReadAll().Single(),CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    public async Task LocalCurrentTransitionProviderConflictPreservesWorkAndNeverCreatesAnotherIdentity()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var original=fixture.Data.Cloud["captured-3"];var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        fixture.Http.BeforePut=()=>
        {fixture.Data.Cloud["captured-3"]=original with {DriveVersion=8};return Task.CompletedTask;};
        var error=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed,error.StatusCode);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(3,fixture.Data.Allocations);
        CollectionAssert.AreEqual(original.Bytes.ToArray(),fixture.Data.Cloud["captured-3"].Bytes.ToArray());
        fixture.Http.BeforePut=null;var signed=await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false);
        var result=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(9L,result.DriveVersion);Assert.AreEqual(1,fixture.Http.Puts);Assert.AreEqual(signed.EnvelopeSha256,result.RootSha256);
        Assert.AreEqual(1L,plan.AccountRevision);Assert.AreEqual(7L,plan.PreviousVersion);
    }

    [TestMethod]
    public async Task LocalCurrentTransitionHumanChangeBeforeTargetReadFailsWithoutACloudWrite()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var sources=new LibrarySourceRepository(database);var source=sources.Snapshot().Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,2);return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(7L,fixture.Data.Cloud["captured-3"].DriveVersion);
        Assert.IsNotNull((await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Envelope);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentTransitionCancellationAndExitJoinAnUncooperativePutAndRecoverItsActualBytes(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOnExit=new TestCallbackReleaseOwner(release);
        fixture.Http.AfterPut=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var pending=fixture.Reader.PublishTransitionAsync(plan.Id,caller.Token);_ =await Task.WhenAny(entered.Task,pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if(pending.IsCompleted) {await pending.ConfigureAwait(false);Assert.IsTrue(entered.Task.IsCompleted);}
        var stopping=Task.CompletedTask;if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(pending.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.Http.Disposed);Assert.AreEqual(0,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>pending).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.Http.Puts);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));fixture.Http.AfterPut=null;
        await using var resumed=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        Assert.IsTrue((await resumed.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false)).AlreadyPresent);Assert.AreEqual(1,fixture.Http.Puts);
        if(exit) Assert.IsTrue(fixture.Reader.ExitConfirmed);
    }

    [TestMethod]
    public async Task LocalCurrentTransitionVerifiesTheOriginalCapturedCatalogAndOnlyThenAllowsSeparateActualActivation()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var catalog=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var plan=await PrepareTransitionAsync(fixture,catalog.Intent.Id).ConfigureAwait(false);
        var before=await ReadCompletionCaptureAsync(fixture.Data,plan.GenerationId!.Value).ConfigureAwait(false);
        var result=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(result.AlreadyPresent);Assert.AreEqual(1,fixture.Http.Puts);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        foreach(var file in new[]{"transition-catalog","transition-catalog-index","transition-catalog-coverage","transition-catalog-coverage-page","transition-catalog-facts"})
            Assert.IsTrue(fixture.Http.BodyFiles.Contains(file),"Read every original selected graph object: "+file);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        Assert.AreEqual(before,await ReadCompletionCaptureAsync(fixture.Data,plan.GenerationId.Value).ConfigureAwait(false));
        await using var pool=new LocalCurrentStagePool(fixture.Data.Setup.Accounts.State,fixture.Data.Budget);
        var ready=await PrepareActivationAsync(fixture,pool).ConfigureAwait(false);await using var stage=ready.Stage;
        var active=await fixture.Reader.ActivateAsync(ready.Fence,stage,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(catalog.Reference,active.Catalog);Assert.AreEqual(1L,active.Index.Assets);Assert.AreEqual(1L,active.CatalogSequence);
        Assert.AreEqual(plan.Sequence,active.RootSequence);Assert.AreEqual(result.RootSha256,active.RootSha256);
        Assert.AreEqual(before,await ReadCompletionCaptureAsync(fixture.Data,plan.GenerationId.Value).ConfigureAwait(false));
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead(),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(1,(await stage.ReadPageAsync(null,60,CancellationToken.None).ConfigureAwait(false)).Assets.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentTransitionMissingOrCorruptedFactsCannotReplaceCurrentOrRetireCapturedWork(bool missing)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var catalog=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture,catalog.Intent.Id).ConfigureAwait(false);
        var before=await ReadCompletionCaptureAsync(fixture.Data,plan.GenerationId!.Value).ConfigureAwait(false);
        if(missing) fixture.Http.MissingFiles.Add("transition-catalog-facts");
        else {var original=fixture.Data.Cloud["transition-catalog-facts"];var bytes=original.Bytes.ToArray();bytes[0]^=1;fixture.Data.Cloud["transition-catalog-facts"]=original with {Bytes=bytes};}
        if(missing) await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(7L,fixture.Data.Cloud["captured-3"].DriveVersion);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(before,await ReadCompletionCaptureAsync(fixture.Data,plan.GenerationId.Value).ConfigureAwait(false));Assert.AreEqual(0,(await StageClaimsAsync(fixture).ConfigureAwait(false)).Count);
    }

    [TestMethod]
    public async Task LocalCurrentTransitionRetainsRejectedTargetPolicyBytesAndCanPublishRevocationAfterExactRepair()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var signer=new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var revoked=await signer.SignControllerAsync(Guid.NewGuid(),"library-publisher-authorization",(identity,sequence)=>new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa",major=1,minor=0,libraryId=identity.LibraryId.ToString("D"),actorId=identity.ControllerId.ToString("D"),epoch=identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            sequence=sequence.ToString(CultureInfo.InvariantCulture),kind="library-publisher-authorization",requiredCapabilities=new[]{CatalogPublisherAuthorization.Capability},
            payload=new {workspace=new {accountId="permission-A",folderId="metadata"},publisher=(object?)null}
        })),CancellationToken.None).ConfigureAwait(false);
        var bytes=revoked.Envelope!.Value.ToArray();var reference=new LibraryPolicyReference(new("permission-A","transition-revocation","metadata",bytes.Length,revoked.EnvelopeSha256!),revoked.Sequence);
        var good=new DriveWorkspaceObject("permission-A",reference.Reference.FileId,"metadata",8,bytes);fixture.Data.Cloud[good.FileId]=good;
        var plan=await PrepareTransitionAsync(fixture,publisher:reference).ConfigureAwait(false);
        var wrong=bytes.ToArray();wrong[0]^=1;fixture.Data.Cloud[good.FileId]=good with {Bytes=wrong};
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Http.Puts);fixture.Data.Cloud[good.FileId]=good;
        var result=await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(1,fixture.Http.Puts);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LocalCurrentGraphObservations(database,fixture.Data.Budget).Current(capture.Attempt,reference.Reference),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CurrentHash(wrong),retained!.RejectedPolicySha256);Assert.AreEqual(8L,retained.RejectedPolicyVersion);Assert.AreEqual(reference.Reference.Sha256,retained.BodySha256);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);Assert.IsNull(policies.Publishers.Publisher);Assert.IsNull(capture.Head.Catalog);
        Assert.AreEqual(result.RootSha256,capture.Head.EnvelopeSha256);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentTransitionRechecksHumanSourcesAndAccountAfterTheLastRootFence(bool disconnect)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        fixture.AfterTransport=async()=>
        {
            var current=await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false);if(current!.Phase!="fenced") return;
            await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            {
                if(disconnect) new DriveAccountRepository(database).Disconnect("permission-A",1);
                else {var sources=new LibrarySourceRepository(database);var source=sources.Snapshot().Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,2);}
                return true;
            },CancellationToken.None).ConfigureAwait(false);
        };
        if(disconnect) await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(7L,fixture.Data.Cloud["captured-3"].DriveVersion);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentTransitionCorruptCheckpointFailsBeforeSigningOrNetwork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"DELETE FROM processor_meta WHERE key='current_control_transition_v1:"+plan.Id.ToString("D")+":claim'");return true;},CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);Assert.IsNull((await TransitionIntentAsync(fixture,plan.Id).ConfigureAwait(false)).Envelope);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentTransitionRechecksAuthorityAfterActualConditionalMetadataAndBeforeAnyPut(bool disconnect)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var replace=fixture.Data.Setup.Accounts.Readers.ConditionalAdmittedReplace!;var changed=false;var admissions=0;
        fixture.Data.Setup.Accounts.Readers.ConditionalAdmittedReplace=(expected,bytes,hash,admit,token)=>
        {
            fixture.Http.BeforeMetadata=async file=>
            {
                if(file!="captured-3" || changed) return;changed=true;
                await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    if(disconnect) new DriveAccountRepository(database).Disconnect("permission-A",1);
                    else {var sources=new LibrarySourceRepository(database);var source=sources.Snapshot().Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,2);}
                    return true;
                },CancellationToken.None).ConfigureAwait(false);
            };
            return replace(expected,bytes,hash,async owned=>{admissions++;await admit(owned).ConfigureAwait(false);},token);
        };
        if(disconnect) await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(changed);Assert.AreEqual(1,admissions);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(7L,fixture.Data.Cloud["captured-3"].DriveVersion);
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    public async Task LocalCurrentTransitionCannotAdoptAnAdditionalUncheckpointedNativeRoot()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var pin=LibraryConnectionDescriptor.Decode(plan.ConnectionCode);
        await new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget).SignControllerAsync(Guid.NewGuid(),LibraryCurrentControl.Kind,
            (_,sequence)=>new LibraryCurrentControlWriter(pin).EncodeDocument(sequence,plan.Target.Publishers,plan.Target.Sources,null),CancellationToken.None).ConfigureAwait(false);
        var reads=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.BodyReads);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentTransitionASelectedNewerPolicyStopsOldWorkEvenWithoutChangedSourceRows(bool sourcePolicy)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        await SelectNewerTransitionPolicyAsync(fixture,sourcePolicy).ConfigureAwait(false);
        Exception? rejected=null;
        try {await fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None).ConfigureAwait(false);}
        catch(Exception error) {rejected=error;}
        Assert.AreEqual(0,fixture.Http.Puts,"A newer selected human policy must block the older transition before an actual PUT.");
        Assert.IsInstanceOfType<StaleLibraryConfigurationException>(rejected);
        Assert.AreEqual(7L,fixture.Data.Cloud["captured-3"].DriveVersion);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    private static async Task SelectNewerTransitionPolicyAsync(CurrentReaderFixture fixture,bool sourcePolicy)
    {
        var original=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var initial=new LibraryAuthorityRepository(database,fixture.Data.Budget).InitialPolicyPlan()!;
            return new ProtocolSigningJournal(database,fixture.Data.Budget).Read(sourcePolicy ? initial.SourceIntentId : initial.PublisherIntentId)!.Body.ToArray();
        },CancellationToken.None).ConfigureAwait(false);
        var kind=sourcePolicy ? "library-source-configuration" : "library-publisher-authorization";
        var newer=await new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget).SignControllerAsync(Guid.NewGuid(),kind,
            (_,sequence)=>
            {
                var body=JsonNode.Parse(original)!.AsObject();body["sequence"]=sequence.ToString(CultureInfo.InvariantCulture);
                if(!sourcePolicy) body["payload"]!["publisher"]=null;
                return new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(body));
            },CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var authority=new LibraryAuthorityRepository(database,fixture.Data.Budget);
            if(sourcePolicy) authority.SelectSourceConfiguration(newer.Envelope!.Value.Span);else authority.SelectPublisherPolicy(newer.Envelope!.Value.Span);
            return true;
        },CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentTransitionSelectedPolicyChangesDuringActualConditionalMetadataCannotReachPut(bool sourcePolicy)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var plan=await PrepareTransitionAsync(fixture).ConfigureAwait(false);
        var replace=fixture.Data.Setup.Accounts.Readers.ConditionalAdmittedReplace!;var changed=false;var admissions=0;
        fixture.Data.Setup.Accounts.Readers.ConditionalAdmittedReplace=(expected,bytes,hash,admit,token)=>
        {
            fixture.Http.BeforeMetadata=async file=>
            {
                if(file!="captured-3" || changed) return;changed=true;
                await SelectNewerTransitionPolicyAsync(fixture,sourcePolicy).ConfigureAwait(false);
            };
            return replace(expected,bytes,hash,async owned=>{admissions++;await admit(owned).ConfigureAwait(false);},token);
        };
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.PublishTransitionAsync(plan.Id,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsTrue(changed);Assert.AreEqual(1,admissions);Assert.AreEqual(0,fixture.Http.Puts);
        Assert.AreEqual(7L,fixture.Data.Cloud["captured-3"].DriveVersion);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    private static async Task<(CatalogObjectReference Reference,CatalogCommitPlan Plan,ProtocolSigningIntent Intent)> CapturedTransitionCatalogAsync(CurrentReaderFixture fixture)
    {
        var data=fixture.Data;
        var catalog=await AddCatalogHeadAsync(data,"transition-catalog",includeAsset:true,beforeSigning:(plan,intentId)=>data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            // Actual transport graph + synthetic immutable generation descriptor.
            // This test never opens, retires or claims a physical publication snapshot.
            var journal=new CatalogGenerationCaptureRepository(database,data.Budget);var scope=plan.Scope;var snapshotId=Guid.NewGuid();
            var capture=journal.Begin(scope,snapshotId,intentId);
            capture=journal.SealSnapshot(scope.GenerationId,new(snapshotId,scope.LibraryId,scope.ConfigurationRevision,1,1,4096,new string('a',64)));
            var basis=new CapturedCatalogBasis(capture.Snapshot!,null,0,plan.ApprovedSources.EnvelopeSha256,plan.SettingsRevision,plan.MinimumHistoryGeneration);
            journal.CapturePublicationBasis(scope.GenerationId,basis);
            var outbox=new WorkspacePublicationRepository(database,data.Budget);
            foreach(var dependency in new[]{(Role:"facts-index",Reference:plan.Dependencies.RootIndex),(Role:"coverage-root",Reference:plan.Dependencies.SourceCoverage)})
            {
                var actual=data.Cloud[dependency.Reference.FileId];var row=outbox.Prepare(Guid.NewGuid(),scope,dependency.Role,"application/json",actual.Bytes.Span);
                outbox.AssignFileId(row.Id,row.Sha256,actual.FileId);outbox.ConfirmVerified(row.Id,scope.AccountId,scope.FolderId,actual.FileId,row.Sha256,row.Payload.Length,actual.DriveVersion);
            }
            journal.SealDependencies(scope.GenerationId,new(basis,plan.Dependencies.RootIndex,plan.Dependencies.SourceCoverage));return true;
        },CancellationToken.None)).ConfigureAwait(false);
        await data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            new CatalogGenerationCaptureRepository(database,data.Budget).ConfirmCommitSealed(catalog.Plan.Scope.GenerationId);
            var store=new WorkspacePublicationRepository(database,data.Budget);var row=store.Prepare(catalog.Intent.Id,catalog.Plan.Scope,"catalog-commit","application/json",catalog.Intent.Envelope!.Value.Span);
            store.AssignFileId(row.Id,row.Sha256,catalog.Reference.FileId);store.ConfirmVerified(row.Id,catalog.Reference.AccountId,catalog.Reference.WorkspaceId,catalog.Reference.FileId,row.Sha256,row.Payload.Length,7);return true;
        },CancellationToken.None).ConfigureAwait(false);
        return catalog;
    }

    private static async Task<CurrentControlTransitionPlan> PrepareTransitionAsync(CurrentReaderFixture fixture,Guid? catalogPublicationId=null,LibraryPolicyReference? publisher=null)
    {
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        var fence=await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);
        return await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CurrentControlTransitionJournal(database,fixture.Data.Budget).Prepare(
            Guid.NewGuid(),fence.Current.Attempt,new(publisher ?? capture.Head.Publishers,capture.Head.Sources,catalogPublicationId)),CancellationToken.None).ConfigureAwait(false);
    }
    private static Task<ProtocolSigningIntent> TransitionIntentAsync(CurrentReaderFixture fixture,Guid id)=>fixture.Data.Setup.Accounts.State.ExecuteAsync(
        database=>new ProtocolSigningJournal(database,fixture.Data.Budget).Read(id)!,CancellationToken.None);

    private sealed partial class LibraryFactory
    {
        public Func<string,string,CancellationToken,Task<DriveWorkspaceConditionalJson>>? ConditionalCapture {get;set;}
        public Func<DriveWorkspaceConditionalJson,ReadOnlyMemory<byte>,string,CancellationToken,Task<DriveWorkspaceConditionalJson>>? ConditionalReplace {get;set;}
        public Func<DriveWorkspaceConditionalJson,ReadOnlyMemory<byte>,string,Func<CancellationToken,Task>,CancellationToken,Task<DriveWorkspaceConditionalJson>>? ConditionalAdmittedReplace {get;set;}
        private sealed partial class Connection : IGoogleWorkspaceConditionalJson
        {
            public Task<DriveWorkspaceConditionalJson> CaptureConditionalWorkspaceJsonAsync(string folder,string file,CancellationToken token)=>
                owner.ConditionalCapture?.Invoke(folder,file,token) ?? throw new InvalidOperationException("Unexpected conditional capture.");
            public Task<DriveWorkspaceConditionalJson> ReplaceConditionalWorkspaceJsonAsync(DriveWorkspaceConditionalJson expected,ReadOnlyMemory<byte> bytes,string hash,CancellationToken token)=>
                owner.ConditionalReplace?.Invoke(expected,bytes,hash,token) ?? throw new InvalidOperationException("Unexpected conditional update.");
        }
        private sealed partial class Connection : IGoogleWorkspaceConditionalPublication
        {
            public Task<DriveWorkspaceConditionalJson> ReplaceAdmittedConditionalWorkspaceJsonAsync(DriveWorkspaceConditionalJson expected,
                ReadOnlyMemory<byte> bytes,string hash,Func<CancellationToken,Task> admit,CancellationToken token)=>
                owner.ConditionalAdmittedReplace?.Invoke(expected,bytes,hash,admit,token) ?? throw new InvalidOperationException("Unexpected admitted conditional update.");
        }
    }
    private sealed partial class CurrentReadHttp
    {
        internal Func<Task>? BeforePut {get;set;}
        internal Func<Task>? AfterPut {get;set;}
        internal Func<string,Task>? BeforeMetadata {get;set;}
        internal bool LosePutAcknowledgement {get;set;}
        internal int Puts {get;private set;}
        internal List<string> PutConditions {get;}=[];
        internal List<string> PutFiles {get;}=[];
        private async Task<HttpResponseMessage> PutTransitionAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Assert.AreEqual("https",request.RequestUri!.Scheme);Assert.AreEqual("www.googleapis.com",request.RequestUri.Host);
            Assert.AreEqual("/upload/drive/v2/files/captured-3",request.RequestUri.AbsolutePath);StringAssert.Contains(request.RequestUri.Query,"uploadType=media");
            StringAssert.Contains(request.RequestUri.Query,"supportsAllDrives=true");Assert.AreEqual("synthetic-access",request.Headers.Authorization!.Parameter);
            Assert.AreEqual("application/json",request.Content!.Headers.ContentType!.MediaType);
            if(BeforePut is not null) await BeforePut().ConfigureAwait(false);
            var current=data.Cloud["captured-3"];var condition=request.Headers.IfMatch.Single().ToString();
            if(condition!="\"actual-"+current.DriveVersion.ToString(CultureInfo.InvariantCulture)+"\"") return Json("{}",HttpStatusCode.PreconditionFailed);
            var bytes=await request.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
            data.Cloud["captured-3"]=current with {DriveVersion=checked(current.DriveVersion+1),Bytes=bytes};Puts++;PutConditions.Add(condition);PutFiles.Add("captured-3");
            if(AfterPut is not null) await AfterPut().ConfigureAwait(false);
            if(LosePutAcknowledgement) {LosePutAcknowledgement=false;throw new HttpRequestException("Synthetic lost response after actual same-file update.");}
            return new(HttpStatusCode.OK) {Content=new StringContent("{}",Encoding.UTF8,"application/json")};
        }
    }
}
