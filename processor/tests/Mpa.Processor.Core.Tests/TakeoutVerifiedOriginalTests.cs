using System.Text;
using System.Text.Json.Nodes;
using Mpa.Processor.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutVerifiedOriginalTests
{
    [TestMethod]
    public async Task HistoricalOriginalReaderNeedsActualReadyReceiptAndKeepsLogicalAndCanonicalIdentitiesSeparate()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var inventory=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(inventory,TakeoutOriginalReaderTests.Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        var entry=catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single();var originals=new TakeoutOriginalJobRepository(database);
        Assert.IsNull(originals.ReadVerified(inventory.ImportId,entry.Key));
        await using var source=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,entry,deadline.Token).ConfigureAwait(false);
        var job=originals.Ensure(source.Capture,Guid.NewGuid());Assert.IsNull(originals.ReadVerified(inventory.ImportId,entry.Key));
        var work=originals.ObserveAcknowledgedBytes(originals.RetainAllocatedFileId(originals.Claim(job)!,"first-id"),3);
        Assert.IsNull(originals.ReadVerified(inventory.ImportId,entry.Key));
        originals.ConfirmVerified(work,inventory.Target.AccountId,inventory.Target.FolderId,"first-id",3,7,source.Capture.Sha256);
        var ready=originals.ReadVerified(inventory.ImportId,entry.Key);Assert.IsNotNull(ready);Assert.AreEqual(job,ready.JobId);
        Assert.AreEqual(entry.MediaId,ready.Capture.Entry.MediaId);Assert.AreEqual(source.Capture.Sha256,ready.Capture.Sha256);
        Assert.AreEqual(new DriveAssetIdentity(inventory.Target.LibraryId).Derive(inventory.Target.AccountId,"first-id"),ready.AssetId);
        Assert.AreEqual(7L,ready.Version);Assert.AreEqual("first-id",ready.FileId);Assert.IsNull(originals.Claim(job));
        var jobs=new DurableJobRepository(database);jobs.MarkReceiptPublished(jobs.ReadPendingReceipts().Single(value=>value.JobId==job).Id);
        Assert.AreEqual(ready.AssetId,originals.ReadVerified(inventory.ImportId,entry.Key)!.AssetId);Assert.AreEqual(1,jobs.Summary(job).Ready);
    }

    [TestMethod]
    [DataRow("fileId")]
    [DataRow("sha256")]
    [DataRow("mediaId")]
    [DataRow("folderId")]
    [DataRow("bytes")]
    [DataRow("unknown-field")]
    [DataRow("outbox")]
    public async Task HistoricalOriginalCannotSubstituteItsFirstIdentityOrManufactureMissingAtomicEvidence(string field)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var inventory=await TakeoutOriginalReaderTests.InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(inventory,TakeoutOriginalReaderTests.Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        var entry=catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single();await using var source=await TakeoutOriginalReader.CaptureAsync(inventory,catalog,entry,deadline.Token).ConfigureAwait(false);
        var originals=new TakeoutOriginalJobRepository(database);var job=originals.Ensure(source.Capture,Guid.NewGuid());
        var work=originals.ObserveAcknowledgedBytes(originals.RetainAllocatedFileId(originals.Claim(job)!,"first-id"),3);
        originals.ConfirmVerified(work,inventory.Target.AccountId,inventory.Target.FolderId,"first-id",3,7,source.Capture.Sha256);
        var receipt=new DurableJobRepository(database).ReadPendingReceipts().Single(value=>value.JobId==job);using var command=database.Connection.CreateCommand();
        command.Parameters.AddWithValue("$job",job.ToString("D"));
        if(field=="outbox")command.CommandText="DELETE FROM state_outbox WHERE job_id=$job";
        else
        {
            var body=JsonNode.Parse(receipt.Receipt)!;
            body[field=="unknown-field"?"unowned":field]=field switch{"bytes"=>"4","mediaId"=>Guid.NewGuid().ToString("D"),"sha256"=>new string('a',64),_=>"substituted"};
            var canonical=Encoding.UTF8.GetString(new CanonicalJson().Encode(Encoding.UTF8.GetBytes(body.ToJsonString())));
            command.CommandText="UPDATE item_stage SET result_receipt=$receipt WHERE job_id=$job; UPDATE state_outbox SET receipt=$receipt WHERE job_id=$job";
            command.Parameters.AddWithValue("$receipt",canonical);
        }
        command.ExecuteNonQuery();Assert.ThrowsExactly<InvalidDataException>(()=>originals.ReadVerified(inventory.ImportId,entry.Key));
        Assert.AreEqual(1,new DurableJobRepository(database).Summary(job).Ready);Assert.IsNull(originals.Claim(job));
    }
}
