using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentOriginalAuthorityVerifiesTheExactOldHeaderAfterActualPublisherRevocationWithoutRevivingIt()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        // This helper owns a synthetic outgoing snapshot descriptor, not a real
        // completed outgoing snapshot. Native signatures and Root writes are real.
        var old=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var revoked=await RevokeForOriginalAuthorityTestAsync(fixture).ConfigureAwait(false);
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        var before=await OriginalAuthorityStateAsync(fixture).ConfigureAwait(false);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
        var evidence=await fixture.Reader.InspectOriginalCatalogAuthorityAsync(fence,old.Plan.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(old.Plan.Scope,evidence.OriginalScope);Assert.AreEqual(old.Intent.Id,evidence.CommitIntentId);
        Assert.AreEqual(old.Intent.Sequence,evidence.CommitSequence);Assert.AreEqual(old.Intent.BodySha256,evidence.CommitBodySha256);
        Assert.AreEqual(old.Intent.EnvelopeSha256,evidence.CommitEnvelopeSha256);Assert.AreEqual("commit-sealed",evidence.Phase);
        Assert.IsTrue(evidence.WitnessRootSequence<revoked.Sequence);Assert.AreNotEqual(fence.Capture.Head.EnvelopeSha256,evidence.WitnessRootSha256);
        Assert.AreEqual(before,await OriginalAuthorityStateAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var authority=new LibraryAuthorityRepository(database,fixture.Data.Budget);
            Assert.IsNull(authority.CurrentPublisherPolicy()!.Publisher);
            Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(()=>authority.AssertLocalPublicationScope(evidence.OriginalScope));
            return true;
        },CancellationToken.None).ConfigureAwait(false);
        // Exercise the actual captured-generation signing boundary. The generic
        // outbox is local bookkeeping, not publisher authorization/cloud admission.
        var signer=new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(()=>signer.SignCapturedCatalogAsync(
            evidence.OriginalScope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await OriginalAuthorityStateAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentOriginalAuthorityReturnsAPolicyWitnessWithoutInventingAnExactCaptureRoot()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var initial=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var old=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var transition=await PrepareTransitionAsync(fixture,old.Intent.Id).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(transition.Id,CancellationToken.None).ConfigureAwait(false);
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        var evidence=await fixture.Reader.InspectOriginalCatalogAuthorityAsync(fence,old.Plan.Scope.GenerationId,CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(fence.Capture.Head.Sequence>initial.Head.Sequence);
        Assert.AreEqual(initial.Head.Publishers,evidence.Publishers);Assert.AreEqual(initial.Head.Sources,evidence.Sources);
        Assert.AreEqual(initial.Head.Sequence,evidence.WitnessRootSequence);Assert.AreEqual(initial.Head.EnvelopeSha256,evidence.WitnessRootSha256);
        Assert.AreEqual(initial.Attempt.SlotId,evidence.WitnessRootIntentId);Assert.AreEqual(old.Plan.Scope,evidence.OriginalScope);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentOriginalAuthorityRejectsForeignOrSupersededFenceBeforeChangingAnyState(bool foreign)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var old=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,
            fixture.Data.Setup.Keys,fixture.Data.Budget);
        if(!foreign) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var before=await OriginalAuthorityStateAsync(fixture).ConfigureAwait(false);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>(foreign?other:fixture.Reader)
            .InspectOriginalCatalogAuthorityAsync(fence,old.Plan.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await OriginalAuthorityStateAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Http.Puts));
    }

    [TestMethod]
    public async Task CurrentOriginalAuthorityRefusesUnwitnessedPolicyEvenWhenNativeAndHumanInputsAreIndividuallyValid()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var fence=await HeaderRecoveryFenceAsync(fixture).ConfigureAwait(false);
        var source=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new ProtocolSigningJournal(database,fixture.Data.Budget);
            var id=journal.Inventory(fixture.Data.Setup.Keys.PublicIdentity.LibraryId).Intents.Single(value=>value.Kind=="library-source-configuration").Id;
            return journal.Read(id)!;
        },CancellationToken.None).ConfigureAwait(false);
        var signer=new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var newer=await signer.SignControllerAsync(Guid.NewGuid(),"library-source-configuration",(_,sequence)=>
        {
            var document=JsonNode.Parse(source.Body.Span) ?? throw new InvalidDataException("Fixture source body is missing.");
            document["sequence"]=sequence.ToString(CultureInfo.InvariantCulture);
            return new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(document));
        },CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {new LibraryAuthorityRepository(database,fixture.Data.Budget).SelectSourceConfiguration(newer.Envelope!.Value.Span);return true;},CancellationToken.None).ConfigureAwait(false);
        var old=await CapturedTransitionCatalogAsync(fixture).ConfigureAwait(false);
        var before=await OriginalAuthorityStateAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>fixture.Reader.InspectOriginalCatalogAuthorityAsync(
            fence,old.Plan.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await OriginalAuthorityStateAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(0,fixture.Http.Puts);
    }

    private static Task<(string Signing,string Capture,string Pending)> OriginalAuthorityStateAsync(CurrentReaderFixture fixture)=>
        fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var journal=new ProtocolSigningJournal(database,fixture.Data.Budget);
            var capture=new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).Active().Single();
            var scope=capture.Scope;
            var pending=new WorkspacePublicationRepository(database,fixture.Data.Budget).Inventory(new(scope.LibraryId,scope.AccountId,scope.FolderId,scope.AccountRevision,null));
            return(journal.Inventory(scope.LibraryId).Sha256,JsonSerializer.Serialize(capture),pending.Sha256);
        },CancellationToken.None);

    private static async Task<ProtocolSigningIntent> RevokeForOriginalAuthorityTestAsync(CurrentReaderFixture fixture)
    {
        var signer=new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var revoked=await signer.SignControllerAsync(Guid.NewGuid(),"library-publisher-authorization",(identity,sequence)=>new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa",major=1,minor=0,libraryId=identity.LibraryId.ToString("D"),actorId=identity.ControllerId.ToString("D"),epoch=identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            sequence=sequence.ToString(CultureInfo.InvariantCulture),kind="library-publisher-authorization",requiredCapabilities=new[]{CatalogPublisherAuthorization.Capability},
            payload=new {workspace=new {accountId="permission-A",folderId="metadata"},publisher=(object?)null}
        })),CancellationToken.None).ConfigureAwait(false);
        var bytes=revoked.Envelope!.Value;
        var policy=new LibraryPolicyReference(new("permission-A","original-authority-revocation","metadata",bytes.Length,revoked.EnvelopeSha256!),revoked.Sequence);
        fixture.Data.Cloud.Add(policy.Reference.FileId,new("permission-A",policy.Reference.FileId,"metadata",7,bytes));
        var transition=await PrepareTransitionAsync(fixture,publisher:policy).ConfigureAwait(false);
        _=await fixture.Reader.PublishTransitionAsync(transition.Id,CancellationToken.None).ConfigureAwait(false);
        // Reading a Root does not itself accept its policies. Select the exact
        // actually published signed revocation explicitly for this local fence
        // assertion; no canonical activation or complete inventory is claimed.
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {new LibraryAuthorityRepository(database,fixture.Data.Budget).SelectPublisherPolicy(bytes.Span);return true;},CancellationToken.None).ConfigureAwait(false);
        return revoked;
    }
}
