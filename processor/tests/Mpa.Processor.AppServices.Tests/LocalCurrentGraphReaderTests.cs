using System.Globalization;
using System.Net;
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
    public async Task LocalCurrentGraphReadsOnlyTheRootSelectedHeadRetainsAllObservationsAndFencesAfterStaging()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var first=await AddCatalogHeadAsync(fixture.Data,"selected-commit",includeAsset:true).ConfigureAwait(false);
        var orphan=await AddCatalogHeadAsync(fixture.Data,"unselected-commit",first.Reference,includeAsset:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,first.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);var stage=new CurrentGraphStage();
        fixture.Http.BeforeFileBody=async file=>
        {
            if(file!="selected-commit-facts") return;
            var declared=new CatalogObjectReference("permission-A",file,"metadata",fixture.Data.Cloud[file].Bytes.Length,CurrentHash(fixture.Data.Cloud[file].Bytes.ToArray()));
            var actual=await GraphStateAsync(fixture,store=>store.Current(capture.Attempt,declared)).ConfigureAwait(false);
            Assert.IsNotNull(actual);Assert.AreEqual(7L,actual.Metadata.Version);Assert.IsNull(actual.BodyVersion);
        };
        var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Reference,graph.Catalog);Assert.AreEqual(1L,graph.Sequence);Assert.AreEqual(1,graph.Commits);
        Assert.AreEqual(1L,graph.Assets);Assert.AreEqual(1L,stage.Assets);Assert.AreEqual(5,graph.Observations.Count);
        Assert.IsTrue(graph.Observations.All(row=>row.BodySha256==row.Reference.Sha256 && row.BodyVersion==row.Metadata.Version));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains(orphan.Reference.FileId));Assert.IsFalse(fixture.Http.BodyFiles.Any(file=>file.StartsWith("unselected-",StringComparison.Ordinal)));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        var inventory=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new ProtocolSigningJournal(database,fixture.Data.Budget).Inventory(first.Plan.Scope.LibraryId),CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(inventory.Intents.Any(row=>row.Id==orphan.Intent.Id && row.EnvelopeSha256==orphan.Intent.EnvelopeSha256));
        fixture.Http.BeforeFileBody=null;var fence=await fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None).ConfigureAwait(false);
        Assert.AreSame(graph,fence.Graph);Assert.AreEqual("fenced",fence.Current.Attempt.Phase);Assert.AreEqual("captured-3",fixture.Http.BodyFiles[^1]);var reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);Assert.AreEqual(3,fixture.Data.Creates);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    public async Task LocalCurrentGraphVerifiesCompleteAncestryBeforeFactsAndKeepsAPartialRetryFromFencingAnOlderGraph()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var first=await AddCatalogHeadAsync(fixture.Data,"predecessor",includeAsset:true).ConfigureAwait(false);
        var second=await AddCatalogHeadAsync(fixture.Data,"selected",first.Reference,includeAsset:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,second.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var complete=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(2,complete.Commits);
        fixture.Http.MissingFiles.Add(first.Reference.FileId);var stage=new CurrentGraphStage();
        var error=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound,error.StatusCode);Assert.AreEqual(0L,stage.Assets);var reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.FenceSelectedGraphAsync(complete,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);Assert.AreEqual(capture.Attempt,await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalCurrentGraphRejectsValidSignedButMismatchedSourceCoverageBeforeFactsOrStaging()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await AddCatalogHeadAsync(fixture.Data,"wrong-coverage",includeAsset:true,mismatchedCoverage:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);var stage=new CurrentGraphStage();
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0L,stage.Assets);Assert.IsFalse(fixture.Http.BodyFiles.Contains("wrong-coverage-facts"));
        Assert.IsTrue(fixture.Http.BodyFiles.Contains("wrong-coverage-coverage-page"));Assert.AreEqual(3,fixture.Data.Creates);
    }

    [TestMethod]
    public async Task LocalCurrentGraphRejectsAnActuallyReferencedInvalidSignatureBeforeDependencies()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var head=await AddCatalogHeadAsync(fixture.Data,"bad-signature",includeAsset:true).ConfigureAwait(false);
        var value=JsonNode.Parse(fixture.Data.Cloud[head.Reference.FileId].Bytes.ToArray())!.AsObject();var signature=value["signature"]!.AsObject();var old=signature["value"]!.GetValue<string>();
        signature["value"]=(old[0]=='A' ? "B" : "A")+old[1..];var bytes=new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(value));
        fixture.Data.Cloud[head.Reference.FileId]=fixture.Data.Cloud[head.Reference.FileId] with {Bytes=bytes};
        var actual=head.Reference with {Bytes=bytes.Length,Sha256=CurrentHash(bytes)};await SelectCurrentCatalogAsync(fixture,actual).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);var stage=new CurrentGraphStage();
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,stage,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0L,stage.Assets);Assert.AreEqual(head.Reference.FileId,fixture.Http.BodyFiles[^1]);Assert.IsFalse(fixture.Http.BodyFiles.Contains(head.Plan.Dependencies.RootIndex.FileId));
    }

    [TestMethod]
    public async Task LocalCurrentGraphRetainsAChangedFinalObjectVersionBeforeRejectingAndRefusesTheOldVersionBeforeBody()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var head=await AddCatalogHeadAsync(fixture.Data,"moving-head").ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        fixture.Http.BeforeFileBody=file=>{if(file==head.Reference.FileId) fixture.Data.Cloud[file]=fixture.Data.Cloud[file] with {DriveVersion=8};return Task.CompletedTask;};
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None)).ConfigureAwait(false);
        var observed=await GraphStateAsync(fixture,store=>store.Current(capture.Attempt,head.Reference)).ConfigureAwait(false);Assert.AreEqual(8L,observed!.Metadata.Version);Assert.IsNull(observed.BodyVersion);
        fixture.Http.BeforeFileBody=null;fixture.Data.Cloud[head.Reference.FileId]=fixture.Data.Cloud[head.Reference.FileId] with {DriveVersion=7};var bodies=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(bodies,fixture.Http.BodyReads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentGraphCancellationOrExitJoinsActualFactsBodyBeforeBorrowedOwnersRetire(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);var head=await AddCatalogHeadAsync(fixture.Data,"joining",includeAsset:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);var stage=new CurrentGraphStage();using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeFileBody=async file=>{if(file=="joining-facts") {entered.TrySetResult();await release.Task.ConfigureAwait(false);}};
        var reading=fixture.Reader.ReadSelectedGraphAsync(policies,stage,caller.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.Http.Disposed);Assert.AreEqual(0,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(0L,stage.Assets);Assert.IsFalse(fixture.Http.Disposed);
        if(exit) Assert.IsTrue(fixture.Reader.ExitConfirmed);
        else {fixture.Http.BeforeFileBody=null;Assert.AreEqual(1L,(await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false)).Assets);}
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentGraphCancellationOrExitJoinsTheActualStageCallbackAndNeverReturnsPartialSuccess(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);var head=await AddCatalogHeadAsync(fixture.Data,"stage-join",includeAsset:true).ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage=new CurrentGraphStage {Hook=async _=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);}};
        var reading=fixture.Reader.ReadSelectedGraphAsync(policies,stage,caller.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.AreEqual(1L,stage.Assets);Assert.AreEqual("captured",(await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false))!.Phase);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsFalse(fixture.Http.Disposed);if(exit) Assert.IsTrue(fixture.Reader.ExitConfirmed);
    }

    [TestMethod]
    public async Task LocalCurrentGraphRejectsForeignAndSupersededFencesAndMetadataCorpusBeforeAnotherBody()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var head=await AddCatalogHeadAsync(fixture.Data,"scoped-head").ConfigureAwait(false);
        await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);var graph=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);var reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.FenceSelectedGraphAsync(graph,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(reads,fixture.Http.MetadataReads);
        var newer=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.FenceSelectedGraphAsync(graph,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(reads,fixture.Http.MetadataReads);Assert.AreNotSame(graph,newer);
        fixture.Http.MetadataJson=(file,raw)=>{if(file!=head.Reference.FileId) return raw;var value=JsonNode.Parse(raw)!.AsObject();value["driveId"]="foreign-corpus";return value.ToJsonString();};var bodies=fixture.Http.BodyReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(bodies,fixture.Http.BodyReads);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.FenceSelectedGraphAsync(newer,CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentGraphEmptySelectionNeedsNoFactsAndAStagingFailureCannotFenceAnOlderCompleteRead()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);var reads=fixture.Http.MetadataReads;
        var empty=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage {Hook=_=>throw new InvalidOperationException("Unexpected empty-selection leaf.")},CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(empty.Catalog);Assert.AreEqual(0L,empty.Assets);Assert.AreEqual(reads,fixture.Http.MetadataReads);
        var head=await AddCatalogHeadAsync(fixture.Data,"stage-fail",includeAsset:true).ConfigureAwait(false);await SelectCurrentCatalogAsync(fixture,head.Reference).ConfigureAwait(false);
        capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);policies=await fixture.Reader.ReadSelectedPoliciesAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var complete=await fixture.Reader.ReadSelectedGraphAsync(policies,new CurrentGraphStage(),CancellationToken.None).ConfigureAwait(false);
        var failed=new CurrentGraphStage {Hook=_=>throw new InvalidOperationException("synthetic stage failure")};
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>fixture.Reader.ReadSelectedGraphAsync(policies,failed,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(1L,failed.Assets);reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.FenceSelectedGraphAsync(complete,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(reads,fixture.Http.MetadataReads);
    }

    private static async Task SelectCurrentCatalogAsync(CurrentReaderFixture fixture,CatalogObjectReference selected)
    {
        var slot=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Data.Budget).Current()!,CancellationToken.None).ConfigureAwait(false);
        var old=LibraryCurrentControl.Read(fixture.Data.Cloud["captured-3"].Bytes.Span,slot.ConnectionDescriptor());
        var root=await new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget).SignControllerAsync(Guid.NewGuid(),LibraryCurrentControl.Kind,
            (_,sequence)=>new LibraryCurrentControlWriter(slot.ConnectionDescriptor()).EncodeDocument(sequence,old.Publishers,old.Sources,selected),CancellationToken.None).ConfigureAwait(false);
        fixture.Data.Cloud["captured-3"]=fixture.Data.Cloud["captured-3"] with {DriveVersion=checked(fixture.Data.Cloud["captured-3"].DriveVersion+1),Bytes=root.Envelope!.Value.ToArray()};
    }
    private sealed class CurrentGraphStage : ILocalCurrentCatalogStage
    {
        internal long Assets {get;private set;}
        internal Func<CatalogFactsLeaf,Task>? Hook {get;init;}
        public async Task AddAsync(CatalogFactsLeaf leaf,CancellationToken token)
        {Assets=checked(Assets+leaf.Facts.Assets.Count);if(Hook is not null) await Hook(leaf).ConfigureAwait(false);}
    }
}
