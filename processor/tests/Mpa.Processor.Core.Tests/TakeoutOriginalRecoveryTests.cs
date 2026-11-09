using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutOriginalRecoveryTests
{
    [TestMethod]
    public async Task OriginalColdReopenKeepsUnknownProviderOutcomeFirstIdentityContextAndOffsetRatherThanClaimingSuccess()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var state=Path.Combine(root.Path,"state");Guid job,import;string context;
        using(var database=new ProcessorStateDatabase(state))
        {
            var completed=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);import=completed.ImportId;
            using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,TakeoutOriginalReaderTests.Budget(state),deadline.Token).ConfigureAwait(false);
            await using var source=await TakeoutOriginalReader.CaptureAsync(completed,catalog,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single(),deadline.Token).ConfigureAwait(false);
            var originals=new TakeoutOriginalJobRepository(database);job=originals.Ensure(source.Capture,Guid.NewGuid());var work=originals.Claim(job)!;
            work=originals.ObserveAcknowledgedBytes(originals.RetainAllocatedFileId(work,"first-unknown-outcome-id"),2);context=work.Lease.CapturedContext;
            Assert.ThrowsExactly<IOException>(()=>originals.RecoverInterruptedAfterExclusiveRestart());Assert.AreEqual(1,new DurableJobRepository(database).Summary(job).Running);
            // Close actual native state without returning the interrupted attempt.
        }
        using(var database=new ProcessorStateDatabase(state))
        {
            var originals=new TakeoutOriginalJobRepository(database);Assert.AreEqual(1,originals.RecoverInterruptedAfterExclusiveRestart());
            var retry=originals.Claim(job)!;Assert.AreEqual(2,retry.Lease.Attempt);Assert.AreEqual(context,retry.Lease.CapturedContext);Assert.AreEqual("first-unknown-outcome-id",retry.FileId);Assert.AreEqual(2L,retry.AcknowledgedBytes);
            Assert.AreEqual(0,new DurableJobRepository(database).Summary(job).Ready);Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount());
            Assert.AreEqual(import,retry.Capture.Inventory.ImportId);new DurableJobRepository(database).ReturnJoinedRead(retry.Lease);Assert.AreEqual(0,originals.RecoverInterruptedAfterExclusiveRestart());
        }
    }
    [TestMethod]
    public async Task OriginalActivityRequiresExplicitPersistedStartAndNeverTreatsProgressAsVerifiedOriginals()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var completed=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        var reviews=new TakeoutImportInventoryRepository(database);var before=reviews.Activity(completed.ImportId);Assert.AreEqual("ready",before.State);Assert.IsFalse(before.Requested);Assert.AreEqual(0L,before.Verified);
        reviews.RequestOriginals(completed.ImportId);Assert.AreEqual("copying",reviews.Activity(completed.ImportId).State);reviews.RequestOriginals(completed.ImportId);
        reviews.RecordIssue(completed.ImportId,"drive-unavailable");Assert.AreEqual("needs-attention",reviews.Activity(completed.ImportId).State);reviews.ClearIssue(completed.ImportId);Assert.AreEqual("copying",reviews.Activity(completed.ImportId).State);
        var index=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,TakeoutOriginalReaderTests.Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        using(index)
        {
            await using var source=await TakeoutOriginalReader.CaptureAsync(completed,index,index.ReadPage(TakeoutEntryKind.Media).Entries.Single(),deadline.Token).ConfigureAwait(false);
            var originals=new TakeoutOriginalJobRepository(database);var job=originals.Ensure(source.Capture,Guid.NewGuid());var progress=originals.ObserveAcknowledgedBytes(originals.RetainAllocatedFileId(originals.Claim(job)!,"first-id"),3);
            Assert.AreEqual(0L,reviews.Activity(completed.ImportId).Verified);Assert.AreEqual("copying",reviews.Activity(completed.ImportId).State);new DurableJobRepository(database).ReturnJoinedRead(progress.Lease);
        }
        new DurableJobRepository(database).Pause(completed.ImportId,cancelJob:true);Assert.AreEqual("canceled",reviews.Activity(completed.ImportId).State);Assert.ThrowsExactly<StaleStageLeaseException>(()=>reviews.RequestOriginals(completed.ImportId));
    }
}
