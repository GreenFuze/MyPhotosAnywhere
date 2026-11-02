using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false,false)]
    [DataRow(false,true)]
    [DataRow(true,false)]
    [DataRow(true,true)]
    public async Task OriginalOwnerRunsActualSourceDurableJobAndAccountWithPartialOffsetsAndIndependentContentVerification(bool partial,bool missingHash)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=new Fixture();
        var input=await PrepareOriginalJobAsync(fixture,deadline.Token).ConfigureAwait(false);var cloud=new OriginalCloud(input.Payload){Partial=partial,MissingHash=missingHash};WireOriginal(fixture,cloud);
        var authority=new OriginalAuthority(input.Approval);await using var owner=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,authority);
        var result=await owner.UploadAsync(input.Job,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual("first-original-id",result.FileId);Assert.AreEqual("Human renamed.jpg",result.Name);Assert.AreEqual(1,cloud.Allocations);
        CollectionAssert.AreEqual(input.Payload,cloud.Uploaded);Assert.AreEqual(partial?43:1024*1024,cloud.Offsets[1]);
        Assert.AreEqual(missingHash?2:0,cloud.RangeReads);Assert.IsTrue(authority.Checks>cloud.Calls);
        var summary=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,summary.Ready);
        var receipts=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts(),deadline.Token).ConfigureAwait(false);Assert.AreEqual(2,receipts.Count);
        var receipt=receipts.Single(row=>row.JobId==input.Job);StringAssert.Contains(receipt.Receipt,"\"fileId\":\"first-original-id\"");StringAssert.Contains(receipt.Receipt,input.Sha256);
        CollectionAssert.AreEqual(input.Payload,await File.ReadAllBytesAsync(input.Path,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostOriginalReplyRetriesSameFirstIdentityAndReplaysRetainedPrefixBeforeOnlyOneReadyReceipt(bool final)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=new Fixture();
        var input=await PrepareOriginalJobAsync(fixture,deadline.Token).ConfigureAwait(false);var cloud=new OriginalCloud(input.Payload){LoseReplyAt=final?2:1};WireOriginal(fixture,cloud);
        await using var owner=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,new OriginalAuthority(input.Approval));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>owner.UploadAsync(input.Job,deadline.Token)).ConfigureAwait(false);
        var pending=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,pending.Queued);Assert.AreEqual(0,pending.Ready);
        var result=await owner.UploadAsync(input.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual("first-original-id",result.FileId);Assert.AreEqual(1,cloud.Allocations);Assert.AreEqual(2,cloud.ChunkWrites);
        var receipts=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts(),deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,receipts.Count(row=>row.JobId==input.Job));CollectionAssert.AreEqual(input.Payload,cloud.Uploaded);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalCallerCancelAndExitJoinOutstandingAccountRequestBeforeSourceReleaseAndReconcileSameId(bool exit)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=new Fixture();
        var input=await PrepareOriginalJobAsync(fixture,deadline.Token).ConfigureAwait(false);var cloud=new OriginalCloud(input.Payload){Hold=true};WireOriginal(fixture,cloud);
        await using var owner=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,new OriginalAuthority(input.Approval));using var caller=new CancellationTokenSource();
        var running=owner.UploadAsync(input.Job,caller.Token);Task? closing=null;
        try
        {
            await cloud.Entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if(exit)closing=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            await cloud.Canceled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.IsFalse(running.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);if(closing is not null)Assert.IsFalse(closing.IsCompleted);
            if(OperatingSystem.IsWindows())Assert.ThrowsExactly<IOException>(()=>File.Open(input.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None));
        }
        finally {cloud.Release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);if(closing is not null)await closing.ConfigureAwait(false);
        Assert.AreEqual(0,(await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false)).Ready);
        cloud.Hold=false;
        await using var retry=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,new OriginalAuthority(input.Approval));
        await retry.UploadAsync(input.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,cloud.Allocations);Assert.AreEqual(2,cloud.ChunkWrites);
        Assert.AreEqual(1,(await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false)).Ready);
    }

    [TestMethod]
    public async Task OriginalAuthorityRevocationAfterActualBytesPreventsLateReadyAndRetainsSameIdForExplicitRetry()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=new Fixture();
        var input=await PrepareOriginalJobAsync(fixture,deadline.Token).ConfigureAwait(false);var authority=new OriginalAuthority(input.Approval);var cloud=new OriginalCloud(input.Payload){AfterFinal=()=>authority.Allowed=false};WireOriginal(fixture,cloud);
        await using var owner=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,authority);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(()=>owner.UploadAsync(input.Job,deadline.Token)).ConfigureAwait(false);
        Assert.AreEqual(0,(await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false)).Ready);
        authority.Allowed=true;await owner.UploadAsync(input.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,cloud.Allocations);Assert.AreEqual(2,cloud.ChunkWrites);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalContentOrIndependentProviderReadMismatchCannotManufactureAnyBackupReceipt(bool remote)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=new Fixture();
        var input=await PrepareOriginalJobAsync(fixture,deadline.Token).ConfigureAwait(false);var cloud=new OriginalCloud(input.Payload){MissingHash=true,CorruptRead=remote};WireOriginal(fixture,cloud);
        if(!remote){var stamp=File.GetLastWriteTimeUtc(input.Path);var replacement=input.Payload.ToArray();replacement[0]^=255;await File.WriteAllBytesAsync(input.Path,replacement,deadline.Token).ConfigureAwait(false);File.SetLastWriteTimeUtc(input.Path,stamp);}
        await using var owner=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,new OriginalAuthority(input.Approval));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owner.UploadAsync(input.Job,deadline.Token)).ConfigureAwait(false);
        Assert.AreEqual(remote?1:0,cloud.Allocations);Assert.AreEqual(0,(await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).Summary(input.Job),deadline.Token).ConfigureAwait(false)).Ready);
        Assert.AreEqual(1,(await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).PendingReceiptCount(),deadline.Token).ConfigureAwait(false)));
    }

    private static async Task<OriginalInput> PrepareOriginalJobAsync(Fixture fixture,CancellationToken token)
    {
        var account=await fixture.Service.ConnectAsync(null,new NoBrowser(),token).ConfigureAwait(false);
        var state=await fixture.State.ExecuteAsync(db=>db.StateDirectory,token).ConfigureAwait(false);var folder=Path.Combine(state,"original-fixture");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"a.jpg");var payload=RandomNumberGenerator.GetBytes(1024*1024+19);await File.WriteAllBytesAsync(path,payload,token).ConfigureAwait(false);
        var budget=new StorageBudgetManager(new OriginalSpace(state));var import=Guid.NewGuid();var part=Guid.NewGuid();
        using(var selected=TakeoutSelectedInput.OpenFolder(part,folder))await fixture.State.ExecuteAsync(db=>{new TakeoutImportInventoryRepository(db).Ensure(import,new(Guid.NewGuid(),account.PermissionId,account.Revision,"original-destination",1),[selected.Binding]);return true;},token).ConfigureAwait(false);
        await using var inventoryOwner=new TakeoutInventoryService(fixture.State,budget);var inventory=await inventoryOwner.RunAsync(import,token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(inventory,budget,token).ConfigureAwait(false);
        await using var source=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,catalog.ReadPage(TakeoutEntryKind.Media,token:token).Entries.Single(),token).ConfigureAwait(false);
        var approval=Guid.NewGuid();var job=await fixture.State.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).Ensure(source.Capture,approval),token).ConfigureAwait(false);
        return new(job,approval,path,payload,source.Capture.Sha256,budget);
    }
    private sealed record OriginalInput(Guid Job,Guid Approval,string Path,byte[] Payload,string Sha256,StorageBudgetManager Budget);
    private sealed class OriginalSpace(string state) : IStorageSpaceReader
    {public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,40*StorageBudgetManager.Gibibyte,TakeoutAssociationCatalog.MeasureOwnedBytes(state));}
    private sealed class OriginalAuthority(Guid approval) : ITakeoutOriginalAuthority
    {
        internal bool Allowed=true;internal int Checks;
        public Task<long> RefreshAsync(TakeoutOriginalWork work,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(!Allowed||work.ApprovalId!=approval)throw new UnauthorizedAccessException("Synthetic exact original approval revoked.");return Task.FromResult(work.Capture.Inventory.Target.AccountRevision);}
        public void AssertCurrent(ProcessorStateDatabase database,TakeoutOriginalWork work)
        {Checks++;if(!Allowed||work.ApprovalId!=approval)throw new UnauthorizedAccessException("Synthetic exact original approval revoked.");new TakeoutOriginalJobRepository(database).AssertCurrent(work);}
    }
    private static void WireOriginal(Fixture fixture,OriginalCloud cloud)
    {
        fixture.Readers.Original=cloud;
        fixture.Readers.WorkspaceAllocate=token=>{token.ThrowIfCancellationRequested();cloud.Allocations++;return Task.FromResult("first-original-id");};
    }
    private sealed class OriginalCloud(byte[] expected)
    {
        internal byte[] Uploaded=new byte[expected.Length];internal long Offset;internal int Allocations,ChunkWrites,RangeReads,Calls,LoseReplyAt;
        internal bool Partial,MissingHash,Hold,CorruptRead;internal Action? AfterFinal;internal List<long> Offsets=[];
        internal TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Canceled=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DriveOriginalUploadProgress Progress(DriveOriginalUpload capture)=>new(capture,Offset,Offset==expected.Length?new(capture,"Human renamed.jpg",capture.MimeType,7,MissingHash?null:Convert.ToHexStringLower(SHA256.HashData(Uploaded))):null);
        internal async Task<DriveOriginalUploadProgress> PrepareAsync(DriveOriginalUpload capture,Func<CancellationToken,Task> admit,CancellationToken token)
        {await admit(token).ConfigureAwait(false);Calls++;return Progress(capture);}
        internal async Task<DriveOriginalUploadProgress> WriteAsync(DriveOriginalUpload capture,long offset,ReadOnlyMemory<byte> chunk,Func<CancellationToken,Task> admit,CancellationToken token)
        {
            await admit(token).ConfigureAwait(false);Calls++;Assert.AreEqual(Offset,offset);Offsets.Add(offset);ChunkWrites++;
            if(Hold)
            {Entered.TrySetResult();using var canceled=token.Register(()=>Canceled.TrySetResult());await Release.Task.ConfigureAwait(false);}
            var retained=Partial&&ChunkWrites==1?43:chunk.Length;chunk.Span[..retained].CopyTo(Uploaded.AsSpan((int)offset));Offset+=retained;
            if(Offset==expected.Length)AfterFinal?.Invoke();if(LoseReplyAt==ChunkWrites)throw new HttpRequestException("Synthetic lost reply after actual stored bytes.");return Progress(capture);
        }
        internal async Task<byte[]> ReadAsync(DriveOriginalUpload capture,long version,long offset,int count,Func<CancellationToken,Task> admit,CancellationToken token)
        {await admit(token).ConfigureAwait(false);Calls++;RangeReads++;Assert.AreEqual(7L,version);var bytes=Uploaded.AsSpan((int)offset,count).ToArray();if(CorruptRead)bytes[0]^=255;return bytes;}
    }
    private sealed partial class LibraryFactory
    {
        internal OriginalCloud? Original;
        private sealed partial class Connection : IGoogleOriginalTransfer
        {
            public Task<DriveOriginalUploadProgress> PrepareOriginalAsync(DriveOriginalUpload capture,Func<CancellationToken,Task> admit,CancellationToken caller)
                =>(owner.Original??throw new InvalidOperationException("Unexpected original preparation.")).PrepareAsync(capture,admit,caller);
            public Task<DriveOriginalUploadProgress> UploadOriginalChunkAsync(DriveOriginalUpload capture,long offset,ReadOnlyMemory<byte> chunk,Func<CancellationToken,Task> admit,CancellationToken caller)
                =>(owner.Original??throw new InvalidOperationException("Unexpected original write.")).WriteAsync(capture,offset,chunk,admit,caller);
            public Task<byte[]> ReadOriginalRangeAsync(DriveOriginalUpload capture,long version,long offset,int count,Func<CancellationToken,Task> admit,CancellationToken caller)
                =>(owner.Original??throw new InvalidOperationException("Unexpected original content read.")).ReadAsync(capture,version,offset,count,admit,caller);
        }
    }
}
