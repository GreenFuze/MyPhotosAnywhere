using System.Globalization;
using System.Runtime.InteropServices;
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
    public async Task LocalCurrentPoliciesReadExactSelectedHttpObjectsKeepOwnedBytesAndRetireAtTheFinalFence()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreSame(capture,policies.Capture);Assert.AreEqual(1L,policies.Publishers.Sequence);
        Assert.IsNotNull(policies.Publishers.Publisher);Assert.AreEqual(2L,capture.Head.Sources.Sequence);Assert.IsTrue(policies.Sources.Revision>0);
        CollectionAssert.AreEqual(new[]{"captured-3",capture.Head.Publishers.Reference.FileId,capture.Head.Sources.Reference.FileId},fixture.Http.BodyFiles);
        Assert.IsTrue(MemoryMarshal.TryGetArray(policies.PublisherEnvelope,out var publisher));
        Assert.IsTrue(MemoryMarshal.TryGetArray(policies.SourceEnvelope,out var source));
        publisher.Array![publisher.Offset]=0;source.Array![source.Offset]=0;
        Assert.AreNotEqual((byte)0,policies.PublisherEnvelope.Span[0]);Assert.AreNotEqual((byte)0,policies.SourceEnvelope.Span[0]);
        Assert.AreEqual("captured",(await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false))!.Phase);
        _=await fixture.Reader.FenceAsync(capture,CancellationToken.None).ConfigureAwait(false);var reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);Assert.AreEqual(3,fixture.Data.Creates);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task LocalCurrentPoliciesRejectActualMimeParentsAndCorpusBeforeAnyPolicyBody(int change)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Http.MetadataJson=(file,raw)=>
        {
            if(file!=capture.Head.Publishers.Reference.FileId) return raw;
            var value=JsonNode.Parse(raw)!.AsObject();
            switch(change) {case 0:value["mimeType"]="image/jpeg";break;case 1:value["parents"]=new JsonArray(new JsonObject { ["id"]="other-folder" });break;case 2:value["driveId"]="other-corpus";break;}
            return value.ToJsonString();
        };
        if(change==2) await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.Http.BodyReads);Assert.AreEqual("captured-3",fixture.Http.BodyFiles.Single());Assert.AreEqual(3,fixture.Data.Creates);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentPoliciesRejectDeclaredLengthAndRetainedVersionRewindBeforeBody(bool rewind)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Http.MetadataJson=(file,raw)=>
        {
            if(file!=capture.Head.Publishers.Reference.FileId) return raw;
            var value=JsonNode.Parse(raw)!.AsObject();
            if(rewind) value["version"]="1";
            else value["fileSize"]=(capture.Head.Publishers.Reference.Bytes+1).ToString(CultureInfo.InvariantCulture);
            return value.ToJsonString();
        };
        if(rewind) await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.Http.BodyReads);
    }

    [TestMethod]
    public async Task LocalCurrentPoliciesRejectReplacementBytesBeforeAnotherRoleAndKeepTheCapturedRoot()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var file=capture.Head.Publishers.Reference.FileId;
        var old=fixture.Data.Cloud[file];var bytes=old.Bytes.ToArray();bytes[0]=0;fixture.Data.Cloud[file]=old with {Bytes=bytes};
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2,fixture.Http.BodyReads);Assert.AreEqual(file,fixture.Http.BodyFiles[^1]);
        Assert.AreEqual(capture.Attempt,await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false));
        fixture.Data.Cloud[file]=old;
        Assert.IsNotNull((await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false)).Publishers.Publisher);
    }

    [TestMethod]
    public async Task LocalCurrentPoliciesRejectForeignReadersAndNewNoncesBeforeIo()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);
        _=await CurrentStateAsync(fixture.Data,store=>store.Begin(1)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentPoliciesCancellationOrCloseJoinsTheActualPolicyBodyAndKeepsBorrowedOwners(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeFileBody=async file=>{Assert.AreEqual(capture.Head.Publishers.Reference.FileId,file);entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var reading=fixture.Reader.ReadSelectedPoliciesAsync(capture,caller.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stopping=Task.CompletedTask;if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.Http.Disposed);Assert.AreEqual(0,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsFalse(fixture.Http.Disposed);Assert.AreEqual(2,fixture.Http.BodyReads);Assert.IsFalse(fixture.Http.BodyFiles.Contains(capture.Head.Sources.Reference.FileId));
        if(exit) Assert.IsTrue(fixture.Reader.ExitConfirmed);
        else
        {
            fixture.Http.BeforeFileBody=null;
            Assert.IsNotNull((await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false)).Publishers.Publisher);
        }
    }

    [TestMethod]
    public async Task LocalCurrentPoliciesSupersededDuringACompletePolicyBodyCannotAdmitTheNextRole()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Http.BeforeFileBody=async _=>{await CurrentStateAsync(fixture.Data,store=>store.Begin(1)).ConfigureAwait(false);};
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2,fixture.Http.BodyReads);Assert.IsFalse(fixture.Http.BodyFiles.Contains(capture.Head.Sources.Reference.FileId));
        Assert.AreEqual("open",(await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false))!.Phase);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentPoliciesObserveRevocationButRejectAnExactlySelectedInvalidRootSignatureBeforeSources(bool badSignature)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var signer=new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var revoked=await signer.SignControllerAsync(Guid.NewGuid(),"library-publisher-authorization",(identity,sequence)=>new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa",major=1,minor=0,libraryId=identity.LibraryId.ToString("D"),actorId=identity.ControllerId.ToString("D"),epoch=identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            sequence=sequence.ToString(CultureInfo.InvariantCulture),kind="library-publisher-authorization",requiredCapabilities=new[]{CatalogPublisherAuthorization.Capability},
            payload=new {workspace=new {accountId="permission-A",folderId="metadata"},publisher=(object?)null}
        })),CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibraryAuthorityRepository(database,fixture.Data.Budget).SelectPublisherPolicy(revoked.Envelope!.Value.Span),CancellationToken.None).ConfigureAwait(false);
        var bytes=revoked.Envelope!.Value.ToArray();
        if(badSignature)
        {
            var envelope=JsonNode.Parse(bytes)!.AsObject();var signature=envelope["signature"]!.AsObject();var old=signature["value"]!.GetValue<string>();
            signature["value"]=(old[0]=='A' ? "B" : "A")+old[1..];bytes=new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(envelope));
        }
        const string file="selected-revoked";
        fixture.Data.Cloud[file]=new("permission-A",file,"metadata",8,bytes);
        var next=await SignNextCurrentObservationAsync(fixture.Data,new(new("permission-A",file,"metadata",bytes.Length,CurrentHash(bytes)),revoked.Sequence)).ConfigureAwait(false);
        fixture.Data.Cloud["captured-3"]=fixture.Data.Cloud["captured-3"] with {DriveVersion=8,Bytes=next.Envelope!.Value.ToArray()};
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(5L,capture.Head.Sequence);
        if(badSignature)
        {
            await Assert.ThrowsExactlyAsync<ProtocolFormatException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(file,fixture.Http.BodyFiles[^1]);Assert.IsFalse(fixture.Http.BodyFiles.Contains(capture.Head.Sources.Reference.FileId));
        }
        else
        {
            var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(policies.Publishers.Publisher);Assert.AreEqual(4L,policies.Publishers.Sequence);Assert.AreEqual(1L,policies.Sources.Revision);
        }
        Assert.AreEqual(3,fixture.Data.Creates);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    public async Task LocalCurrentPoliciesDisconnectDuringThePolicyBodyDeniesTheNextRoleAndRetainsHistoricalRootWork()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Http.BeforeFileBody=async _=>{await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);};
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(()=>fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2,fixture.Http.BodyReads);Assert.IsFalse(fixture.Http.BodyFiles.Contains(capture.Head.Sources.Reference.FileId));
        Assert.AreEqual(capture.Attempt,await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false));Assert.AreEqual(3,fixture.Data.Creates);
    }
}
