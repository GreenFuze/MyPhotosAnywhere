using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentInventoryUsesActualImmutableReadsThenFinalRootAndNeverCompletesLegacyOrActivates()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var trace=InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
        var legacy=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).LastCheckpoint(),CancellationToken.None).ConfigureAwait(false);
        var inventory=await fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(fixture.Data.Cloud.Count-1,inventory.Checkpoint.Documents);Assert.AreEqual("root",trace[^1]);
        Assert.IsTrue(trace.IndexOf("final-changes")<trace.LastIndexOf("root"));
        Assert.IsFalse(trace.Contains("immutable:"+"captured-3",StringComparer.Ordinal));
        var checkpoint=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(inventory.Checkpoint,checkpoint);
        var afterLegacy=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).LastCheckpoint(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(legacy,afterLegacy);Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);
        var count=trace.Count;await fixture.Reader.AssertWorkspaceInventoryUnchangedAsync(inventory,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(count,trace.Count);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.AssertWorkspaceInventoryUnchangedAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
        _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.AssertWorkspaceInventoryUnchangedAsync(inventory,CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalCurrentInventoryKeepsWrongChannelRootHistoryAndSeparatesOnlyItsPinnedIdentity()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var root=fixture.Data.Cloud["captured-3"];var observation=new WorkspaceDocumentObservation(root.FileId,root.DriveVersion,root.Bytes.Length,CurrentHash(root.Bytes.ToArray()));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            new WorkspaceObservationRepository(database,fixture.Data.Budget).RememberRead(new(new LibraryAuthorityRepository(database,fixture.Data.Budget).Controller()!.LibraryId,root.AccountId,root.FolderId,capture.Attempt.AccountRevision,capture.Attempt.SharedDriveId),observation);return true;
        },CancellationToken.None).ConfigureAwait(false);
        var result=await fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).Known(result.Checkpoint.Context),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(observation,retained.Single(value=>value.FileId==root.FileId));Assert.AreEqual(fixture.Data.Cloud.Count-1,result.Checkpoint.Documents);
    }

    [TestMethod]
    public async Task LocalCurrentInventoryFinalRootChangeBlocksCompletionAfterTheLastImmutableCheck()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var checks=0;
        fixture.Data.Setup.Accounts.Readers.DocumentChanges=(cursor,_)=>
        {
            if(++checks==3) {var old=fixture.Data.Cloud["captured-3"];fixture.Data.Cloud[old.FileId]=old with {DriveVersion=old.DriveVersion+1};}
            return Task.FromResult(new DriveChangePage(new([]),null,cursor));
        };
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3,checks);
        var state=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LocalCurrentControlObservations(database,fixture.Data.Budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("blocked",state!.Phase);Assert.AreEqual(capture.Attempt.Version+1,state.MaxVersion);
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory(),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task LocalCurrentInventoryNewerListedRootRetainsHighWaterWithoutInventingConditionalFields()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var root=fixture.Data.Cloud["captured-3"];
        fixture.Data.Setup.Accounts.Readers.MetadataInventory=(_,_,_,_)=>Task.FromResult(new DriveWorkspaceMetadataPage(new([
            new(root.FileId,root.DriveVersion+1,root.Bytes.Length,"application/json"),new("unread-after-root",9,12,"application/json")]),null,false));
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        var state=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LocalCurrentControlObservations(database,fixture.Data.Budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("blocked",state!.Phase);Assert.AreEqual(root.DriveVersion+1,state.MaxVersion);Assert.AreEqual(capture.Attempt.EntityTag,state.EntityTag);
        var known=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).KnownIdentities(new(new LibraryAuthorityRepository(database,fixture.Data.Budget).Controller()!.LibraryId,root.AccountId,root.FolderId,capture.Attempt.AccountRevision,capture.Attempt.SharedDriveId)),CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(known.Contains("unread-after-root",StringComparer.Ordinal));
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.CaptureAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task LocalCurrentInventoryConflictingRootChangeAlsoRetainsOtherUnknownRemovalOnThatRawPage(int conflictAt)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var root=fixture.Data.Cloud["captured-3"];
        var pages=0;
        fixture.Data.Setup.Accounts.Readers.DocumentChanges=(cursor,_)=>Task.FromResult(++pages==conflictAt ? new DriveChangePage(new([
            new("file",root.FileId,false,new("application/json",new([root.FolderId]),false,root.DriveVersion+1,root.Bytes.Length,true)),
            new("file","unknown-removed-beside-root",true,null)]),null,cursor) : new(new([]),null,cursor));
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        var context=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationContext(new LibraryAuthorityRepository(database,fixture.Data.Budget).Controller()!.LibraryId,root.AccountId,root.FolderId,capture.Attempt.AccountRevision,capture.Attempt.SharedDriveId),CancellationToken.None).ConfigureAwait(false);
        var known=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).KnownHistory(context),CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(known.Any(value=>value.Id=="unknown-removed-beside-root" && value.RecoveryRequired));
        Assert.IsFalse(known.Any(value=>value.Id==root.FileId));Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task LocalCurrentInventoryNormalMutableRootEventsNeverBecomeImmutableRecoveryHistory()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var root=fixture.Data.Cloud["captured-3"];
        fixture.Data.Setup.Accounts.Readers.DocumentChanges=(cursor,_)=>Task.FromResult(new DriveChangePage(new([
            new("file",root.FileId,false,new("application/json",new([root.FolderId]),false,root.DriveVersion,root.Bytes.Length,true))]),null,cursor));
        var inventory=await fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var known=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).KnownHistory(inventory.Checkpoint.Context),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0,known.Count);Assert.AreEqual(fixture.Data.Cloud.Count-1,inventory.Checkpoint.Documents);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentInventoryRawPageBoundsAndDuplicatePinnedRootCannotBeHiddenBySeparation(bool duplicate)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var trace=InstallCurrentInventoryTransport(fixture);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var root=fixture.Data.Cloud["captured-3"];var row=new DriveWorkspaceMetadataCandidate(root.FileId,root.DriveVersion,root.Bytes.Length,"application/json");
        var rows=duplicate ? new[]{row,row} : new[]{row}.Concat(Enumerable.Range(0,100).Select(index=>new DriveWorkspaceMetadataCandidate("unread-"+index,7,12,"application/json"))).ToArray();
        fixture.Data.Setup.Accounts.Readers.MetadataInventory=(_,_,_,_)=>Task.FromResult(new DriveWorkspaceMetadataPage(new(rows),null,false));
        if(duplicate) await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(()=>fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(trace.Any(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentInventoryCancelOrExitJoinsUncooperativeImmutableReadWithoutLateCheckpoint(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        InstallCurrentInventoryTransport(fixture);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.MetadataRead=async(_,candidate,_)=>
        {entered.TrySetResult();await release.Task.ConfigureAwait(false);return fixture.Data.Cloud[candidate.FileId];};
        var reading=fixture.Reader.ObserveWorkspaceInventoryAsync(capture,caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.AreEqual(0,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory(),CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(exit,fixture.Reader.ExitConfirmed);Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentInventoryMissingPinnedRootInAClaimedCompleteListingCannotGrantCompletion(bool withSibling)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var trace=InstallCurrentInventoryTransport(fixture);var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Data.Setup.Accounts.Readers.MetadataInventory=(_,_,_,_)=>Task.FromResult(new DriveWorkspaceMetadataPage(new(withSibling ?
            [new("listed-without-root",7,12,"application/json")] : []),null,false));
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(()=>fixture.Reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(trace.Any(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));Assert.AreEqual(0,fixture.Http.Puts);
        Assert.IsNull(await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new WorkspaceObservationRepository(database,fixture.Data.Budget).ReadCurrentInventory(),CancellationToken.None).ConfigureAwait(false));
        var retained=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var repository=new WorkspaceObservationRepository(database,fixture.Data.Budget);return repository.KnownIdentities(repository.LastAttempt()!.Context);},CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(withSibling,retained.Contains("listed-without-root",StringComparer.Ordinal));
    }

    private static List<string> InstallCurrentInventoryTransport(CurrentReaderFixture fixture)
    {
        var trace=new List<string>();var checks=0;
        fixture.Data.Setup.Accounts.Readers.DocumentStart=(shared,_)=>Task.FromResult(new DriveChangeCursor("permission-A",shared,"current"));
        fixture.Data.Setup.Accounts.Readers.DocumentChanges=(cursor,_)=>
        {trace.Add(++checks==3 ? "final-changes" : "changes");return Task.FromResult(new DriveChangePage(new([]),null,cursor));};
        fixture.Data.Setup.Accounts.Readers.MetadataInventory=(_,_,_,_)=>Task.FromResult(new DriveWorkspaceMetadataPage(new(fixture.Data.Cloud.Values.OrderBy(value=>value.FileId,StringComparer.Ordinal)
            .Select(value=>new DriveWorkspaceMetadataCandidate(value.FileId,value.DriveVersion,value.Bytes.Length,fixture.Data.MetadataMimeTypes.GetValueOrDefault(value.FileId,value.MimeType))).ToList()),null,false));
        fixture.Data.Setup.Accounts.Readers.MetadataRead=(_,candidate,_)=>
        {trace.Add("immutable:"+candidate.FileId);return Task.FromResult(fixture.Data.Cloud[candidate.FileId]);};
        fixture.Http.BeforeFileBody=file=>{if(file=="captured-3") trace.Add("root");return Task.CompletedTask;};
        return trace;
    }
}
