using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentLiveReadsReuseOnlyCompletedSameOwnerObservationsWhilePublicAndColdReadersStillReadEveryBody()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var trace=InstallCurrentInventoryTransport(fixture);
        async Task<LocalCurrentWorkspaceInventory> Scan(LocalCurrentControlReader reader,bool session)
        {
            var capture=await reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
            return session?await reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false):
                await reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        }
        var first=await Scan(fixture.Reader,true).ConfigureAwait(false);var count=fixture.Data.Cloud.Count-1;
        Assert.AreEqual(count,trace.Count(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));Assert.AreEqual("root",trace[^1]);
        var second=await Scan(fixture.Reader,true).ConfigureAwait(false);
        Assert.AreEqual(0,trace.Count(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));Assert.AreEqual("root",trace[^1]);
        Assert.AreEqual(first.Checkpoint.InventorySha256,second.Checkpoint.InventorySha256);Assert.AreEqual(count,second.Checkpoint.Documents);
        await fixture.Reader.AssertWorkspaceInventoryUnchangedAsync(second,CancellationToken.None).ConfigureAwait(false);
        _=await Scan(fixture.Reader,false).ConfigureAwait(false);Assert.AreEqual(count,trace.Count(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));
        await using var cold=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        _=await Scan(cold,true).ConfigureAwait(false);Assert.AreEqual(count,trace.Count(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));
        Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentLiveReadsChangedServerVersionUsesActualBodyAndMissingKnownCandidateCannotComplete()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var trace=InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var item=fixture.Data.Cloud.Values.First(value=>value.FileId!="captured-3");fixture.Data.Cloud[item.FileId]=item with {DriveVersion=item.DriveVersion+1};
        capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
        var retained=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[]{"immutable:"+item.FileId},trace.Where(value=>value.StartsWith("immutable:",StringComparison.Ordinal)).ToArray());Assert.AreEqual("root",trace[^1]);
        var changed=fixture.Data.Cloud[item.FileId];fixture.Data.Cloud.Remove(item.FileId);
        capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<WorkspaceObservationConflictException>(()=>fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        var history=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var repository=new WorkspaceObservationRepository(database,fixture.Data.Budget);return (Checkpoint:repository.ReadCurrentInventory(),Attempt:repository.LastAttempt());},CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(retained.Checkpoint,history.Checkpoint);Assert.AreNotEqual(retained.Checkpoint.AttemptId,history.Attempt!.Id);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.AssertWorkspaceInventoryUnchangedAsync(retained,CancellationToken.None)).ConfigureAwait(false);
        fixture.Data.Cloud.Add(item.FileId,changed);capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
        _=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(fixture.Data.Cloud.Count-1,trace.Count(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task CurrentLiveReadsIncompleteListingDiscardsCacheAndStillRetainsObservedCandidatesBeforeFailure()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var trace=InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var listing=fixture.Data.Setup.Accounts.Readers.MetadataInventory!;
        fixture.Data.Setup.Accounts.Readers.MetadataInventory=async(folder,shared,page,token)=>
        {var result=await listing(folder,shared,page,token).ConfigureAwait(false);return result with {IncompleteSearch=true};};
        capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(()=>fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(trace.Any(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));
        fixture.Data.Setup.Accounts.Readers.MetadataInventory=listing;capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
        _=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(fixture.Data.Cloud.Count-1,trace.Count(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));Assert.AreEqual("root",trace[^1]);
    }

    [TestMethod]
    public async Task CurrentLiveReadsFailedNewBodyNeverSeedsCacheAndRetryReadsTheCompleteActualCorpus()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var trace=InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var original=fixture.Data.Cloud.Values.First();var extra=new DriveWorkspaceObject(original.AccountId,"live-new-document",original.FolderId,7,"{}"u8.ToArray());fixture.Data.Cloud.Add(extra.FileId,extra);
        var read=fixture.Data.Setup.Accounts.Readers.MetadataRead!;
        fixture.Data.Setup.Accounts.Readers.MetadataRead=(folder,candidate,token)=>candidate.FileId==extra.FileId?
            Task.FromException<DriveWorkspaceObject>(new IOException("actual candidate read failed")):read(folder,candidate,token);
        capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<IOException>(()=>fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        var known=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var repository=new WorkspaceObservationRepository(database,fixture.Data.Budget);return repository.KnownIdentities(repository.LastAttempt()!.Context);},CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(known.Contains(extra.FileId,StringComparer.Ordinal));
        fixture.Data.Setup.Accounts.Readers.MetadataRead=read;capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
        _=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(fixture.Data.Cloud.Count-1,trace.Count(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));Assert.AreEqual("root",trace[^1]);Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    public async Task CurrentLiveReadsStillMakesFinalActualRootReadAndRejectsItsChangedHighWaterWithoutBodyRereads()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var trace=InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        _=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var checks=0;fixture.Data.Setup.Accounts.Readers.DocumentChanges=(cursor,_)=>
        {if(++checks==3) {var root=fixture.Data.Cloud["captured-3"];fixture.Data.Cloud[root.FileId]=root with {DriveVersion=root.DriveVersion+1};}return Task.FromResult(new DriveChangePage(new([]),null,cursor));};
        capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);trace.Clear();
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3,checks);Assert.IsFalse(trace.Any(value=>value.StartsWith("immutable:",StringComparison.Ordinal)));
        var state=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LocalCurrentControlObservations(database,fixture.Data.Budget).Current(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("blocked",state!.Phase);Assert.AreEqual(capture.Attempt.Version+1,state.MaxVersion);Assert.AreEqual(0,fixture.Http.Puts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentLiveReadsCancelAndExitJoinAnActualNewBodyEvenWhenOtherObservationsAreCached(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);InstallCurrentInventoryTransport(fixture);
        var capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var retained=await fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var original=fixture.Data.Cloud.Values.First();var extra=new DriveWorkspaceObject(original.AccountId,"live-uncooperative-document",original.FolderId,7,"{}"u8.ToArray());fixture.Data.Cloud.Add(extra.FileId,extra);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.MetadataRead=async(_,candidate,_)=>
        {Assert.AreEqual(extra.FileId,candidate.FileId);entered.TrySetResult();await release.Task.ConfigureAwait(false);return extra;};
        capture=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var reading=fixture.Reader.ObserveWorkspaceInventorySessionAsync(capture,caller.Token);var stopping=Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.Reader.ExitConfirmed);
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(exit,fixture.Reader.ExitConfirmed);
        var history=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {var repository=new WorkspaceObservationRepository(database,fixture.Data.Budget);return (Checkpoint:repository.ReadCurrentInventory(),Attempt:repository.LastAttempt());},CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(retained.Checkpoint,history.Checkpoint);Assert.AreNotEqual(retained.Checkpoint.AttemptId,history.Attempt!.Id);
        if(exit) await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>fixture.Reader.AssertWorkspaceInventoryUnchangedAsync(retained,CancellationToken.None)).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.AssertWorkspaceInventoryUnchangedAsync(retained,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Http.Puts);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
