using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutOriginalJobTests
{
    [TestMethod]
    public async Task SameCapturedOriginalCannotAcquireAnotherApprovalOrBecomeABackupFromProgressAlone()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var inventory=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(inventory,TakeoutOriginalReaderTests.Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        await using var reader=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single(),deadline.Token).ConfigureAwait(false);
        var jobs=new TakeoutOriginalJobRepository(database);var approval=Guid.NewGuid();var id=jobs.Ensure(reader.Capture,approval);Assert.AreEqual(id,jobs.Ensure(reader.Capture,approval));
        Assert.ThrowsExactly<ConflictingJobItemException>(()=>jobs.Ensure(reader.Capture,Guid.NewGuid()));
        var work=jobs.Claim(id)!;Assert.IsNotNull(work);Assert.IsNull(jobs.Claim(id));Assert.AreEqual(approval,work.ApprovalId);Assert.AreEqual(reader.Capture.Entry,work.Capture.Entry);
        // Native .NET source stamps exceed the protocol's exact integer range.
        StringAssert.Contains(work.Lease.CapturedContext,"\"sourceStamp\":\""+reader.Capture.Entry.SourceStamp.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\"");
        var assigned=jobs.RetainAllocatedFileId(work,"first-original-id");
        Assert.ThrowsExactly<StaleStageLeaseException>(()=>jobs.RetainAllocatedFileId(work,"second-original-id"));
        Assert.ThrowsExactly<InvalidDataException>(()=>jobs.RetainAllocatedFileId(assigned,"second-original-id"));
        Assert.ThrowsExactly<InvalidDataException>(()=>jobs.ObserveAcknowledgedBytes(assigned,4));
        var allBytes=jobs.ObserveAcknowledgedBytes(assigned,3);Assert.AreEqual(3L,allBytes.AcknowledgedBytes);
        Assert.AreEqual(0,new DurableJobRepository(database).Summary(id).Ready);Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount());
        jobs.AssertCurrent(allBytes);new DurableJobRepository(database).ReturnJoinedRead(allBytes.Lease);
    }

    [TestMethod]
    public async Task ActualDatabaseReopenRetainsFirstIdentityContextAndAttemptWhileServerMayResetItsObservedOffset()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var state=Path.Combine(root.Path,"state");
        Guid id,approval=Guid.NewGuid();string context;
        using(var database=new ProcessorStateDatabase(state))
        {
            var inventory=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],true,deadline.Token).ConfigureAwait(false);
            using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(inventory,TakeoutOriginalReaderTests.Budget(state),deadline.Token).ConfigureAwait(false);
            await using var reader=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single(),deadline.Token).ConfigureAwait(false);
            var jobs=new TakeoutOriginalJobRepository(database);id=jobs.Ensure(reader.Capture,approval);var work=jobs.Claim(id)!;context=work.Lease.CapturedContext;
            var progress=jobs.ObserveAcknowledgedBytes(jobs.RetainAllocatedFileId(work,"first-original-id"),2);
            new DurableJobRepository(database).ReturnJoinedRead(progress.Lease);
        }
        using(var database=new ProcessorStateDatabase(state))
        {
            var jobs=new TakeoutOriginalJobRepository(database);var retry=jobs.Claim(id)!;Assert.AreEqual(2,retry.Lease.Attempt);Assert.AreEqual("first-original-id",retry.FileId);
            Assert.AreEqual(2L,retry.AcknowledgedBytes);Assert.AreEqual(approval,retry.ApprovalId);Assert.AreEqual(context,retry.Lease.CapturedContext);
            var reset=jobs.ObserveAcknowledgedBytes(retry,0);Assert.AreEqual("first-original-id",reset.FileId);Assert.AreEqual(context,reset.Lease.CapturedContext);
            Assert.ThrowsExactly<StaleStageLeaseException>(()=>jobs.AssertCurrent(retry));jobs.AssertCurrent(reset);new DurableJobRepository(database).ReturnJoinedRead(reset.Lease);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KnownAllocationIsRetainedAfterPauseOrCancelWithoutReopeningUploadAdmission(bool cancel)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var inventory=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(inventory,TakeoutOriginalReaderTests.Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        await using var reader=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single(),deadline.Token).ConfigureAwait(false);
        var jobs=new TakeoutOriginalJobRepository(database);var id=jobs.Ensure(reader.Capture,Guid.NewGuid());var work=jobs.Claim(id)!;var durable=new DurableJobRepository(database);durable.Pause(id,cancelJob:cancel);
        Assert.ThrowsExactly<StaleStageLeaseException>(()=>jobs.AssertCurrent(work));var retained=jobs.RetainAllocatedFileId(work,"known-returned-id");
        Assert.ThrowsExactly<StaleStageLeaseException>(()=>jobs.ObserveAcknowledgedBytes(retained,1));
        durable.ReturnJoinedRead(retained.Lease);Assert.IsNull(jobs.Claim(id));
        if(cancel){Assert.AreEqual(1,durable.Summary(id).Canceled);Assert.ThrowsExactly<InvalidOperationException>(()=>durable.Resume(id));}
        else {durable.Resume(id);var retry=jobs.Claim(id)!;Assert.AreEqual("known-returned-id",retry.FileId);Assert.AreEqual(work.Lease.CapturedContext,retry.Lease.CapturedContext);durable.ReturnJoinedRead(retry.Lease);}
        Assert.AreEqual(1,durable.PendingReceiptCount());
    }

    [TestMethod]
    public async Task SameSizeAndTimestampContentReplacementCannotSilentlyReplaceTheFirstCapturedWork()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var inventory=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(inventory,TakeoutOriginalReaderTests.Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        var entry=catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single();var approval=Guid.NewGuid();var jobs=new TakeoutOriginalJobRepository(database);Guid id;
        await using(var first=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,entry,deadline.Token).ConfigureAwait(false))id=jobs.Ensure(first.Capture,approval);
        var path=Path.Combine(inventory.Parts.Single().Location,"a.jpg");await File.WriteAllBytesAsync(path,[4,5,6],deadline.Token).ConfigureAwait(false);File.SetLastWriteTimeUtc(path,new(entry.SourceStamp,DateTimeKind.Utc));
        await using var replacement=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,entry,deadline.Token).ConfigureAwait(false);
        Assert.ThrowsExactly<ConflictingJobItemException>(()=>jobs.Ensure(replacement.Capture,approval));
        var original=jobs.Claim(id)!;Assert.AreNotEqual(replacement.Capture.Sha256,original.Capture.Sha256);Assert.AreEqual(entry.MediaId,original.Capture.Entry.MediaId);
        new DurableJobRepository(database).ReturnJoinedRead(original.Lease);
    }
}
