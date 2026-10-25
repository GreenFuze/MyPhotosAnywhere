using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;
using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task InitialCurrentSigningNeedsCompletedPoliciesAndAnActualAllocatedControlId()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        var signer=new JournaledProtocolSigner(fixture.Setup.Accounts.State,fixture.Setup.Keys,fixture.Budget);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>signer.SignInitialCurrentControlAsync(CancellationToken.None)).ConfigureAwait(false);
        var completion=await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>signer.SignInitialCurrentControlAsync(CancellationToken.None)).ConfigureAwait(false);
        var slot=await AllocateControlAsync(fixture).ConfigureAwait(false);
        var beforeCalls=fixture.Setup.Accounts.Readers.DocumentCalls;
        var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>signer.SignInitialCurrentControlAsync(CancellationToken.None))).ConfigureAwait(false);
        Assert.IsTrue(results.All(result=>result.Id==slot.Id && result.Sequence==3 && result.Kind==LibraryCurrentControl.Kind));
        foreach(var result in results) CollectionAssert.AreEqual(results[0].Envelope!.Value.ToArray(),result.Envelope!.Value.ToArray());
        var selected=LibraryCurrentControl.Read(results[0].Envelope!.Value.Span,slot.ConnectionDescriptor());
        Assert.IsNull(selected.Catalog);Assert.AreEqual(completion.Publisher.FileId,selected.Publishers.Reference.FileId);
        Assert.AreEqual(completion.Sources.FileId,selected.Sources.Reference.FileId);
        Assert.IsNotNull(selected.ReadPublishers(fixture.Cloud[completion.Publisher.FileId!].Bytes.Span,slot.ConnectionDescriptor()).Publisher);
        Assert.AreEqual(1L,selected.ReadSources(fixture.Cloud[completion.Sources.FileId!].Bytes.Span,slot.ConnectionDescriptor()).Revision);
        Assert.AreEqual(beforeCalls,fixture.Setup.Accounts.Readers.DocumentCalls);Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(2,fixture.Allocations);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task DisconnectStopsFreshCurrentSigningButPreservesTheSealedIntentAndFileIdentity()
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);var slot=await AllocateControlAsync(fixture).ConfigureAwait(false);
        var signer=new JournaledProtocolSigner(fixture.Setup.Accounts.State,fixture.Setup.Keys,fixture.Budget);
        var signed=await signer.SignInitialCurrentControlAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>signer.SignInitialCurrentControlAsync(CancellationToken.None)).ConfigureAwait(false);
        var retained=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Budget).Read(slot.Id),CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(signed.Envelope!.Value.ToArray(),retained!.Envelope!.Value.ToArray());
        Assert.AreEqual(slot,await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Budget).Current(),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(2,fixture.Allocations);
    }

    private static Task<LibraryCurrentControlSlot> AllocateControlAsync(InitialPublicationFixture fixture)=>
        fixture.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var slots=new LibraryCurrentControlSlotRepository(database,fixture.Budget);
            return slots.CaptureAllocatedFile(slots.Prepare(),"permission-A",1,"actual-synthetic-control-id");
        },CancellationToken.None);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitialCurrentSigningCannotReviveARevokedPublisherOrChangedHumanSources(bool changedSources)
    {
        await using var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);var slot=await AllocateControlAsync(fixture).ConfigureAwait(false);
        var signer=new JournaledProtocolSigner(fixture.Setup.Accounts.State,fixture.Setup.Keys,fixture.Budget);
        var signed=await signer.SignInitialCurrentControlAsync(CancellationToken.None).ConfigureAwait(false);
        if (changedSources)
        {
            await fixture.Setup.AddSourceAsync("new-human-source").ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>signer.SignInitialCurrentControlAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        else
        {
            var revoked=await signer.SignControllerAsync(Guid.NewGuid(),"library-publisher-authorization",(identity,sequence)=>
                new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    format="mpa",major=1,minor=0,libraryId=identity.LibraryId.ToString("D"),actorId=identity.ControllerId.ToString("D"),
                    epoch=identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),sequence=sequence.ToString(CultureInfo.InvariantCulture),
                    kind="library-publisher-authorization",requiredCapabilities=new[]{CatalogPublisherAuthorization.Capability},
                    payload=new { workspace=new { accountId="permission-A",folderId="metadata" },publisher=(object?)null }
                })),CancellationToken.None).ConfigureAwait(false);
            await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryAuthorityRepository(database,fixture.Budget)
                .SelectPublisherPolicy(revoked.Envelope!.Value.Span),CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(()=>signer.SignInitialCurrentControlAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        var retained=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Budget).Read(slot.Id),CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(signed.Envelope!.Value.ToArray(),retained!.Envelope!.Value.ToArray());
        Assert.AreEqual(2,fixture.Creates);Assert.AreEqual(2,fixture.Allocations);
    }
}
