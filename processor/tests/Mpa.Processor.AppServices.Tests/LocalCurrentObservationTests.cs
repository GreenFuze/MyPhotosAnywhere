using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentObservationsKeepHistoricalFloorsAndRetireEveryCapturedNonceAtFenceOrNewBegin()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);var bytes=fixture.Cloud["captured-3"].Bytes.ToArray();
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        Assert.AreEqual(7L,open.MaxVersion);Assert.AreEqual(7L,open.ContentVersion);Assert.AreEqual(CurrentHash(bytes),open.ContentSha256);
        var captured=await ObserveCurrentAsync(fixture,open,bytes,7).ConfigureAwait(false);
        Assert.AreEqual(3L,captured.RootSequence);
        Assert.IsTrue(await CurrentStateAsync(fixture,store=>store.CheckFenceMetadata(captured,CurrentMetadata(bytes,7))).ConfigureAwait(false));
        var fenced=await CurrentStateAsync(fixture,store=>store.Fence(captured,CurrentMetadata(bytes,7),CurrentHash(bytes))).ConfigureAwait(false);
        Assert.AreEqual("fenced",fenced.Phase);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>CurrentStateAsync(fixture,store=>{store.AssertCaptured(captured);return true;})).ConfigureAwait(false);
        var next=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        Assert.AreNotEqual(open.Id,next.Id);Assert.AreEqual(open.Revision+1,next.Revision);Assert.AreEqual(7L,next.ContentVersion);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(bytes,7)))).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Creates);Assert.AreEqual(3,fixture.Allocations);
    }

    [TestMethod]
    public async Task LocalCurrentObservationsPersistMetadataBeforeBytesAndMalformedBytesBeforeSemanticVerification()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);var good=fixture.Cloud["captured-3"].Bytes.ToArray();var bad="{broken"u8.ToArray();
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        var reading=await CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(bad,8))).ConfigureAwait(false);
        Assert.AreEqual("reading",(await CurrentStateAsync(fixture,store=>store.Current()).ConfigureAwait(false))!.Phase);
        var read=await CurrentStateAsync(fixture,store=>store.RememberBytes(reading,bad)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(()=>CurrentStateAsync(fixture,store=>store.Observe(read,bad))).ConfigureAwait(false);
        Assert.AreEqual(read,await CurrentStateAsync(fixture,store=>store.Current()).ConfigureAwait(false));
        open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        Assert.AreEqual(8L,open.ContentVersion);Assert.AreEqual(CurrentHash(bad),open.ContentSha256);
        reading=await CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(good,8))).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>CurrentStateAsync(fixture,store=>store.RememberBytes(reading,good))).ConfigureAwait(false);
        open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        Assert.AreEqual(3L,(await ObserveCurrentAsync(fixture,open,good,9).ConfigureAwait(false)).RootSequence);
        Assert.AreEqual(3,fixture.Creates);
    }

    [TestMethod]
    public async Task LocalCurrentObservationsRejectRootRollbackAndRetainTheHigherActualHeadAcrossReopen()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);var initial=fixture.Cloud["captured-3"].Bytes.ToArray();
        var higher=await SignNextCurrentObservationAsync(fixture).ConfigureAwait(false);
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        var captured=await ObserveCurrentAsync(fixture,open,higher.Envelope!.Value.ToArray(),8).ConfigureAwait(false);
        Assert.AreEqual(4L,captured.RootSequence);
        open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        var reading=await CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(initial,9))).ConfigureAwait(false);
        var read=await CurrentStateAsync(fixture,store=>store.RememberBytes(reading,initial)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(()=>CurrentStateAsync(fixture,store=>store.Observe(read,initial))).ConfigureAwait(false);
        open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        var restored=await ObserveCurrentAsync(fixture,open,higher.Envelope.Value.ToArray(),10).ConfigureAwait(false);
        Assert.AreEqual(4L,restored.RootSequence);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>CurrentStateAsync(fixture,store=>{store.AssertCaptured(restored with {RootSequence=3});return true;})).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentObservationsRejectCloudSignaturesMissingFromTheRetainedLocalRootJournal()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);
        var slot=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Budget).Current()!,CancellationToken.None).ConfigureAwait(false);
        var old=LibraryCurrentControl.Read(fixture.Cloud["captured-3"].Bytes.Span,slot.ConnectionDescriptor());
        var body=new LibraryCurrentControlWriter(slot.ConnectionDescriptor()).EncodeDocument(100,old.Publishers,old.Sources,null);
        var signature=fixture.Setup.Keys.SignControllerDocument(body);
        var envelope=new SignedProtocolDocuments([LibraryCurrentControl.Capability]).Encode(body,signature);
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        var reading=await CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(envelope,8))).ConfigureAwait(false);
        var read=await CurrentStateAsync(fixture,store=>store.RememberBytes(reading,envelope)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(()=>CurrentStateAsync(fixture,store=>store.Observe(read,envelope))).ConfigureAwait(false);
        Assert.AreEqual(read,await CurrentStateAsync(fixture,store=>store.Current()).ConfigureAwait(false));
        var inventory=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Budget).Inventory(slot.ConnectionDescriptor().LibraryId),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3L,inventory.Actors.Single(actor=>actor.Role=="controller").Sequence);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentObservationsRejectMissingOrCorruptStateWithoutResettingItsNonce(bool missing)
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);
        _=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        Assert.AreEqual(1,await CompletionSqlAsync(fixture,missing ? "DELETE FROM processor_meta WHERE key='local_current_observation_v1'" :
            "UPDATE processor_meta SET value='{}' WHERE key='local_current_observation_v1'").ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>CurrentStateAsync(fixture,store=>store.Current())).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>CurrentStateAsync(fixture,store=>store.Begin(1))).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Creates);Assert.AreEqual(3,fixture.Allocations);
    }

    [TestMethod]
    public async Task LocalCurrentObservationsCannotLoseAnOlderObservedRootPrefixEvenWithTheNewestIntentPresent()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);
        var second=await SignNextCurrentObservationAsync(fixture).ConfigureAwait(false);var third=await SignNextCurrentObservationAsync(fixture).ConfigureAwait(false);
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        await ObserveCurrentAsync(fixture,open,second.Envelope!.Value.ToArray(),8).ConfigureAwait(false);
        open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        await ObserveCurrentAsync(fixture,open,third.Envelope!.Value.ToArray(),9).ConfigureAwait(false);
        Assert.AreEqual(1,await CompletionSqlAsync(fixture,"DELETE FROM signing_intent WHERE id='"+second.Id.ToString("D")+"'").ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>CurrentStateAsync(fixture,store=>store.Current())).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>CurrentStateAsync(fixture,store=>store.Begin(1))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentObservationsKeepLowStorageAndFailedSqlCheckpointsAtomic()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);
        space.Available=1;
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>CurrentStateAsync(fixture,store=>store.Begin(1),budget)).ConfigureAwait(false);
        Assert.IsNull(await CurrentStateAsync(fixture,store=>store.Current(),budget).ConfigureAwait(false));space.Available=20*StorageBudgetManager.Gibibyte;
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1),budget).ConfigureAwait(false);
        await CompletionSqlAsync(fixture,"""
            CREATE TRIGGER fail_current_observation BEFORE UPDATE ON processor_meta
            WHEN NEW.key='local_current_observation_v1:sha256'
            BEGIN SELECT RAISE(ABORT,'synthetic observation checkpoint failure'); END;
            """).ConfigureAwait(false);
        var bytes=fixture.Cloud["captured-3"].Bytes.ToArray();
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(()=>CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(bytes,8)),budget)).ConfigureAwait(false);
        Assert.AreEqual(open,await CurrentStateAsync(fixture,store=>store.Current(),budget).ConfigureAwait(false));
        await CompletionSqlAsync(fixture,"DROP TRIGGER fail_current_observation").ConfigureAwait(false);
        var reading=await CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(bytes,8)),budget).ConfigureAwait(false);
        Assert.AreEqual("reading",reading.Phase);Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task LocalCurrentObservationsPersistChangedFenceAndRefuseAnOldAttemptAfterDisconnect()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);var bytes=fixture.Cloud["captured-3"].Bytes.ToArray();
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);var captured=await ObserveCurrentAsync(fixture,open,bytes,7).ConfigureAwait(false);
        Assert.IsFalse(await CurrentStateAsync(fixture,store=>store.CheckFenceMetadata(captured,CurrentMetadata(bytes,8))).ConfigureAwait(false));
        var blocked=await CurrentStateAsync(fixture,store=>store.Current()).ConfigureAwait(false);Assert.AreEqual("blocked",blocked!.Phase);Assert.AreEqual(8L,blocked.MaxVersion);
        open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(bytes,7)))).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(()=>CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(bytes,8)))).ConfigureAwait(false);
        Assert.AreEqual(open,await CurrentStateAsync(fixture,store=>store.Current()).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentObservationsDoNotRequireAnActivePublisherToObserveRootRevocation()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);var signer=new JournaledProtocolSigner(fixture.Setup.Accounts.State,fixture.Setup.Keys,fixture.Budget);
        var revoked=await signer.SignControllerAsync(Guid.NewGuid(),"library-publisher-authorization",(identity,sequence)=>new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa",major=1,minor=0,libraryId=identity.LibraryId.ToString("D"),actorId=identity.ControllerId.ToString("D"),epoch=identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            sequence=sequence.ToString(CultureInfo.InvariantCulture),kind="library-publisher-authorization",requiredCapabilities=new[]{CatalogPublisherAuthorization.Capability},
            payload=new {workspace=new {accountId="permission-A",folderId="metadata"},publisher=(object?)null}
        })),CancellationToken.None).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryAuthorityRepository(database,fixture.Budget).SelectPublisherPolicy(revoked.Envelope!.Value.Span),CancellationToken.None).ConfigureAwait(false);
        var next=await SignNextCurrentObservationAsync(fixture,new(new("permission-A","revoked-policy","metadata",revoked.Envelope!.Value.Length,revoked.EnvelopeSha256!),revoked.Sequence)).ConfigureAwait(false);
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);var captured=await ObserveCurrentAsync(fixture,open,next.Envelope!.Value.ToArray(),8).ConfigureAwait(false);
        Assert.AreEqual(5L,captured.RootSequence);Assert.AreEqual("captured",captured.Phase);Assert.AreEqual(3,fixture.Creates);
    }

    [TestMethod]
    public async Task LocalCurrentObservationsKeepFullInt64DriveVersionsAndDoNotResetOnCancellation()
    {
        await using var fixture=await ObservedCurrentFixtureAsync().ConfigureAwait(false);var bytes=fixture.Cloud["captured-3"].Bytes.ToArray();
        var open=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);var captured=await ObserveCurrentAsync(fixture,open,bytes,long.MaxValue).ConfigureAwait(false);
        Assert.AreEqual(long.MaxValue,(await CurrentStateAsync(fixture,store=>store.Current()).ConfigureAwait(false))!.MaxVersion);
        using var caller=new CancellationTokenSource();caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>CurrentStateAsync(fixture,store=>store.Begin(1,caller.Token))).ConfigureAwait(false);
        Assert.AreEqual(captured,await CurrentStateAsync(fixture,store=>store.Current()).ConfigureAwait(false));
        var fresh=await CurrentStateAsync(fixture,store=>store.Begin(1)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>CurrentStateAsync(fixture,store=>store.RememberMetadata(fresh,CurrentMetadata(bytes,long.MaxValue-1)))).ConfigureAwait(false);
    }

    private static async Task<InitialPublicationFixture> ObservedCurrentFixtureAsync()
    {
        var fixture=await InitialPublicationFixture.CreateAsync().ConfigureAwait(false);
        try {await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);ConfigureCurrentPublication(fixture);await using var publisher=CurrentPublisher(fixture);await publisher.PublishAsync(CancellationToken.None).ConfigureAwait(false);return fixture;}
        catch {await fixture.DisposeAsync().ConfigureAwait(false);throw;}
    }
    private static Task<T> CurrentStateAsync<T>(InitialPublicationFixture fixture,Func<LocalCurrentControlObservations,T> operation,StorageBudgetManager? budget=null)=>
        fixture.Setup.Accounts.State.ExecuteAsync(database=>operation(new(database,budget ?? fixture.Budget)),CancellationToken.None);
    private static string CurrentHash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static LocalCurrentControlMetadata CurrentMetadata(byte[] bytes,long version)=>new(version,bytes.Length,"\"actual-"+version.ToString(CultureInfo.InvariantCulture)+"\"",new string('a',64),null);
    private static async Task<LocalCurrentControlAttempt> ObserveCurrentAsync(InitialPublicationFixture fixture,LocalCurrentControlAttempt open,byte[] bytes,long version)
    {
        var metadata=await CurrentStateAsync(fixture,store=>store.RememberMetadata(open,CurrentMetadata(bytes,version))).ConfigureAwait(false);
        var read=await CurrentStateAsync(fixture,store=>store.RememberBytes(metadata,bytes)).ConfigureAwait(false);
        return(await CurrentStateAsync(fixture,store=>store.Observe(read,bytes)).ConfigureAwait(false)).Attempt;
    }
    private static async Task<ProtocolSigningIntent> SignNextCurrentObservationAsync(InitialPublicationFixture fixture,LibraryPolicyReference? publishers=null)
    {
        var slot=await fixture.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Budget).Current()!,CancellationToken.None).ConfigureAwait(false);
        var initial=LibraryCurrentControl.Read(fixture.Cloud["captured-3"].Bytes.Span,slot.ConnectionDescriptor());
        return await new JournaledProtocolSigner(fixture.Setup.Accounts.State,fixture.Setup.Keys,fixture.Budget).SignControllerAsync(Guid.NewGuid(),LibraryCurrentControl.Kind,
            (_,sequence)=>new LibraryCurrentControlWriter(slot.ConnectionDescriptor()).EncodeDocument(sequence,publishers ?? initial.Publishers,initial.Sources,null),CancellationToken.None).ConfigureAwait(false);
    }
}
