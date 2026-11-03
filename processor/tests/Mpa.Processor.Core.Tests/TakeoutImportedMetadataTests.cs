using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class TakeoutMetadataOperationTests
{
    [TestMethod]
    public async Task ImportedSidecarRunsInstalledWorkerAgainstTheVerifiedCanonicalPhotoAndRetainsLogicalProvenance()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var association=fixture.Catalog.AssociateMetadata(fixture.Sidecar.Key,fixture.Document);
        var job=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).EnsureImported(fixture.Original,association,fixture.Descriptor),fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(job,await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).EnsureImported(fixture.Original,association,fixture.Descriptor),fixture.Token).ConfigureAwait(false));
        var work=(await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).Claim(job),fixture.Token).ConfigureAwait(false))!;
        Assert.AreEqual(fixture.Original.AssetId,work.MediaId);Assert.AreEqual(fixture.Original.Capture.Sha256,work.MediaInputSha256);Assert.IsNotNull(work.Imported);
        Assert.AreEqual(fixture.Original.Capture.Entry.MediaId,work.Imported.Original.Capture.Entry.MediaId);Assert.AreEqual(fixture.Sidecar,work.Imported.Sidecar);
        using var selected=TakeoutSelectedInput.OpenFolder(fixture.Sidecar.PartId,fixture.Completed.Parts.Single().Location);
        await using(var source=fixture.Catalog.OpenCaptured(selected,fixture.Sidecar,TakeoutMetadataDocument.MaximumSourceBytes))
            await fixture.Storage.CaptureAsync(work,source,fixture.Token).ConfigureAwait(false);
        var bundle=await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false);
        var installation=await TakeoutWorkerInstallation.OpenInstalledAsync(bundle,fixture.Token).ConfigureAwait(false);
        var operation=await TakeoutMetadataWorkerOperation.CreateAsync(fixture.State,work,fixture.Storage,installation,fixture.Token).ConfigureAwait(false);
        await using var supervisor=new ProcessorWorkSupervisor();await supervisor.Start(operation,fixture.Token).ConfigureAwait(false);
        var receipt=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts().Single(value=>value.JobId==job),fixture.Token).ConfigureAwait(false);
        using var body=JsonDocument.Parse(receipt.Receipt);Assert.AreEqual(fixture.Original.AssetId.ToString("D"),body.RootElement.GetProperty("mediaId").GetString());
        var metadata=TakeoutMetadataDocument.ReadVerifiedArtifact(await File.ReadAllBytesAsync(ResultPath(fixture.StateDirectory,work),fixture.Token).ConfigureAwait(false),fixture.Descriptor);
        Assert.AreEqual("Imported detail",metadata.Description);Assert.AreEqual(1000L,metadata.CapturedUtc);Assert.AreEqual("Imported hint",metadata.ImportedNames.Single());
        CollectionAssert.AreEqual(fixture.Raw,metadata.CopyRawSource());CollectionAssert.AreEqual(fixture.Raw,await File.ReadAllBytesAsync(Path.Combine(fixture.Completed.Parts.Single().Location,"a.jpg.json"),fixture.Token).ConfigureAwait(false));
        await fixture.State.ExecuteAsync(db=>
        {var jobs=new DurableJobRepository(db);Assert.AreEqual(1,jobs.Summary(fixture.Original.JobId).Ready);Assert.AreEqual(0,jobs.Summary(fixture.Original.JobId).Queued);Assert.IsNull(new TakeoutMetadataJobRepository(db).Claim(job));return true;},fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(0L,new FileInfo(operation.Launch.NewDiagnosticPath).Length);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        var completed=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key),fixture.Token).ConfigureAwait(false);
        Assert.IsNotNull(completed);Assert.AreEqual(fixture.Original.AssetId,completed.Imported.Original.AssetId);
        File.Delete(Path.Combine(fixture.Completed.Parts.Single().Location,"a.jpg.json"));
        CollectionAssert.AreEqual(fixture.Raw,(await fixture.Storage.ReadVerifiedAsync(completed,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        await fixture.State.ExecuteAsync(db=>{new DurableJobRepository(db).MarkReceiptPublished(receipt.Id);Assert.IsNotNull(new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key));return true;},fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("artifact")]
    [DataRow("source")]
    [DataRow("marker")]
    [DataRow("receipt")]
    [DataRow("outbox")]
    public async Task HistoricalMetadataReaderRejectsChangedRetainedEvidenceWithoutRequeueingOriginals(string change)
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var association=fixture.Catalog.AssociateMetadata(fixture.Sidecar.Key,fixture.Document);
        var work=await fixture.State.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);var id=jobs.EnsureImported(fixture.Original,association,fixture.Descriptor);Assert.IsNull(jobs.ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key));return jobs.Claim(id)!;},fixture.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(fixture.Raw,false))await fixture.Storage.CaptureAsync(work,source,fixture.Token).ConfigureAwait(false);
        var installed=await TakeoutWorkerInstallation.OpenInstalledAsync(await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false),fixture.Token).ConfigureAwait(false);
        await using var supervisor=new ProcessorWorkSupervisor();await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(fixture.State,work,fixture.Storage,installed,fixture.Token).ConfigureAwait(false),fixture.Token).ConfigureAwait(false);
        var completed=(await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key),fixture.Token).ConfigureAwait(false))!;
        if(change is "receipt" or "outbox")
        {
            await fixture.State.ExecuteAsync(db=>
            {
                using var command=db.Connection.CreateCommand();
                command.CommandText=change=="outbox"?"DELETE FROM state_outbox WHERE job_id=$id":"UPDATE item_stage SET result_receipt=json_set(result_receipt,'$.sourceSha256',$hash) WHERE job_id=$id";
                command.Parameters.AddWithValue("$id",work.Lease.JobId.ToString("D"));if(change=="receipt")command.Parameters.AddWithValue("$hash",new string('b',64));command.ExecuteNonQuery();
                Assert.ThrowsExactly<InvalidDataException>(()=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key));return true;
            },fixture.Token).ConfigureAwait(false);
        }
        else
        {
            var path=change=="artifact"?ResultPath(fixture.StateDirectory,work):change=="source"?SourcePath(fixture.StateDirectory,work):Path.Combine(Path.GetDirectoryName(ResultPath(fixture.StateDirectory,work))!,"claim.json");
            var bytes=await File.ReadAllBytesAsync(path,fixture.Token).ConfigureAwait(false);var changed=bytes.ToArray();changed[0]^=1;await File.WriteAllBytesAsync(path,changed,fixture.Token).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Storage.ReadVerifiedAsync(completed,fixture.Token)).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path,bytes,fixture.Token).ConfigureAwait(false);
        }
        await fixture.State.ExecuteAsync(db=>{Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);Assert.IsNull(new TakeoutMetadataJobRepository(db).Claim(work.Lease.JobId));return true;},fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task ImportedSidecarCannotUseAnotherRawSnapshotOrUnmatchedTitleAndKeepsTheFirstBindingOnRetry()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);var association=fixture.Catalog.AssociateMetadata(fixture.Sidecar.Key,fixture.Document);
        var wrong=new ArtifactDescriptor(fixture.Raw.Length,new string('b',64),"input.bin");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).EnsureImported(fixture.Original,association,wrong),fixture.Token)).ConfigureAwait(false);
        var foreign=TakeoutMetadataDocument.ParseSource("{\"title\":\"other.jpg\",\"description\":\"not this photo\",\"photoTakenTime\":{\"timestamp\":\"1\"}}"u8.ToArray());
        var unmatched=fixture.Catalog.AssociateMetadata(fixture.Sidecar.Key,foreign);Assert.AreNotEqual("matched",unmatched.Status);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).EnsureImported(fixture.Original,unmatched,fixture.Descriptor),fixture.Token)).ConfigureAwait(false);
        var job=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).EnsureImported(fixture.Original,association,fixture.Descriptor),fixture.Token).ConfigureAwait(false);
        var first=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).Claim(job)!,fixture.Token).ConfigureAwait(false);
        var context=first.Lease.CapturedContext;await fixture.State.ExecuteAsync(db=>{new TakeoutMetadataJobRepository(db).ReturnJoinedOperation(first);return true;},fixture.Token).ConfigureAwait(false);
        var retry=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).Claim(job)!,fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(context,retry.Lease.CapturedContext);Assert.AreEqual(2,retry.Lease.Attempt);Assert.AreEqual(fixture.Original.AssetId,retry.MediaId);
        await fixture.State.ExecuteAsync(db=>{new TakeoutMetadataJobRepository(db).ReturnJoinedOperation(retry);return true;},fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImportedMetadataExclusiveRestartPreservesFirstBindingsAndCanceledPolicy(bool canceled)
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var association=fixture.Catalog.AssociateMetadata(fixture.Sidecar.Key,fixture.Document);
        var first=await fixture.State.ExecuteAsync(db=>
        {
            var jobs=new TakeoutMetadataJobRepository(db);var id=jobs.EnsureImported(fixture.Original,association,fixture.Descriptor);var work=jobs.Claim(id)!;
            if(canceled)new DurableJobRepository(db).Pause(id,cancelJob:true);return work;
        },fixture.Token).ConfigureAwait(false);
        fixture.Storage.Dispose();
        using var fresh=new TakeoutMetadataStageStorage(fixture.Database,fixture.Budget);
        Assert.AreEqual(1,await fixture.State.ExecuteAsync(_=>fresh.RecoverInterruptedAfterExclusiveRestart(),fixture.Token).ConfigureAwait(false));
        Assert.ThrowsExactly<InvalidOperationException>(()=>fresh.RecoverInterruptedAfterExclusiveRestart());
        await fixture.State.ExecuteAsync(db=>
        {
            var jobs=new TakeoutMetadataJobRepository(db);Assert.ThrowsExactly<StaleStageLeaseException>(()=>jobs.AssertCurrent(first));
            var retry=jobs.Claim(first.Lease.JobId);
            if(canceled){Assert.IsNull(retry);Assert.AreEqual(1,new DurableJobRepository(db).Summary(first.Lease.JobId).Canceled);}
            else
            {
                Assert.IsNotNull(retry);Assert.AreEqual(2,retry.Lease.Attempt);Assert.AreEqual(first.Lease.CapturedContext,retry.Lease.CapturedContext);
                Assert.AreEqual(first.Lease.CapturedItemContext,retry.Lease.CapturedItemContext);Assert.AreEqual(first.Imported!.Original.FileId,retry.Imported!.Original.FileId);
                jobs.ReturnJoinedOperation(retry);
            }
            Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);return true;
        },fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MetadataRecoveryCannotStealAnAdmittedCaptureOrChangeItsRetainedSource()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var association=fixture.Catalog.AssociateMetadata(fixture.Sidecar.Key,fixture.Document);
        var work=await fixture.State.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);return jobs.Claim(jobs.EnsureImported(fixture.Original,association,fixture.Descriptor))!;},fixture.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(fixture.Raw,false))await fixture.Storage.CaptureAsync(work,source,fixture.Token).ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidOperationException>(()=>fixture.Storage.RecoverInterruptedAfterExclusiveRestart());
        await fixture.State.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);jobs.AssertCurrent(work);jobs.ReturnJoinedOperation(work);return true;},fixture.Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(fixture.Raw,await File.ReadAllBytesAsync(SourcePath(fixture.StateDirectory,work),fixture.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task MetadataDisplayProgressRejectsImpossibleCountsAndNeverCreatesWorkerOrPublicationReceipts()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>
        {
            var repository=new TakeoutImportInventoryRepository(db);var originalReceipts=new DurableJobRepository(db).PendingReceiptCount();
            Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecordMetadataProgress(fixture.Completed,new(2,0,0,0,false)));
            Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecordMetadataProgress(fixture.Completed,new(1,1,1,0,false)));
            Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecordMetadataProgress(fixture.Completed,new(1,0,0,1,true)));
            Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecordMetadataProgress(fixture.Completed,new(1,0,0,0,true)));
            Assert.IsNull(repository.Activity(fixture.Completed.ImportId).Metadata);
            repository.RecordMetadataProgress(fixture.Completed,new(1,1,0,0,true));Assert.AreEqual(new TakeoutMetadataProgress(1,1,0,0,true),repository.Activity(fixture.Completed.ImportId).Metadata);
            Assert.IsNull(new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key));
            Assert.AreEqual(originalReceipts,new DurableJobRepository(db).PendingReceiptCount());return true;
        },fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MetadataDisplayProgressRetainsItsFirstInventoryBindingAndRejectsAlteredLocalPayload()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>
        {
            var repository=new TakeoutImportInventoryRepository(db);repository.RecordMetadataProgress(fixture.Completed,new(1,0,0,1,false));
            Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecordMetadataProgress(fixture.Completed,new(0,0,0,0,true)));
            using var update=db.Connection.CreateCommand();update.CommandText="UPDATE processor_meta SET value=json_set(value,'$.indexSha256',$hash) WHERE key=$key";
            update.Parameters.AddWithValue("$key","takeout-metadata-progress-v1:"+fixture.Completed.ImportId.ToString("D"));update.Parameters.AddWithValue("$hash",new string('b',64));Assert.AreEqual(1,update.ExecuteNonQuery());
            Assert.ThrowsExactly<InvalidDataException>(()=>repository.Activity(fixture.Completed.ImportId));Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);return true;
        },fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MetadataStagingKeepsItsExactItemBoundAndPreservesEveryRetainedSnapshotWhenFull()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var association=fixture.Catalog.AssociateMetadata(fixture.Sidecar.Key,fixture.Document);
        var first=await fixture.State.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);return jobs.Claim(jobs.EnsureImported(fixture.Original,association,fixture.Descriptor))!;},fixture.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(fixture.Raw,false))await fixture.Storage.CaptureAsync(first,source,fixture.Token).ConfigureAwait(false);
        var item=Path.GetDirectoryName(SourcePath(fixture.StateDirectory,first))!;var area=Path.GetDirectoryName(item)!;
        var marker=await File.ReadAllBytesAsync(Path.Combine(item,"claim.json"),fixture.Token).ConfigureAwait(false);
        // Retained private-item fixtures use the exact native ownership format.
        // These local markers are not worker receipts or original/provider grants.
        for(var ordinal=1;ordinal<1024;ordinal++)
        {
            var id=Guid.NewGuid();var retained=Path.Combine(area,"item-"+id.ToString("N")+"-"+id.ToString("N"));Directory.CreateDirectory(retained);
            var claim=System.Text.Json.Nodes.JsonNode.Parse(marker)!;claim["jobId"]=id.ToString("D");claim["itemId"]=id.ToString("D");
            await File.WriteAllBytesAsync(Path.Combine(retained,"claim.json"),JsonSerializer.SerializeToUtf8Bytes(claim),fixture.Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(retained,"source.bin"),fixture.Raw,fixture.Token).ConfigureAwait(false);
        }
        var next=await fixture.State.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);var id=Guid.NewGuid();jobs.Ensure(id,id,fixture.Original.AssetId,fixture.Original.Capture.Sha256,fixture.Descriptor);return jobs.Claim(id)!;},fixture.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(fixture.Raw,false))await Assert.ThrowsExactlyAsync<TakeoutMetadataCapacityException>(()=>fixture.Storage.CaptureAsync(next,source,fixture.Token)).ConfigureAwait(false);
        Assert.AreEqual(1024,Directory.GetDirectories(area).Length);CollectionAssert.AreEqual(fixture.Raw,await File.ReadAllBytesAsync(SourcePath(fixture.StateDirectory,first),fixture.Token).ConfigureAwait(false));
        await fixture.State.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);jobs.ReturnJoinedOperation(first);jobs.ReturnJoinedOperation(next);Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);Assert.AreEqual(0,new DurableJobRepository(db).Summary(next.Lease.JobId).Ready);return true;},fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    private sealed class ImportedFixture: IAsyncDisposable
    {
        internal WorkerProcessTests.WorkerTestDirectory Root{get;}=new();
        private readonly CancellationTokenSource _deadline=new(TimeSpan.FromSeconds(30));
        private ProcessorStateDatabase _database=null!;
        internal ProcessorStateDatabase Database=>_database;
        internal ProcessorStateCoordinator State{get;private set;}=null!;
        internal StorageBudgetManager Budget{get;private set;}=null!;
        internal TakeoutMetadataStageStorage Storage{get;private set;}=null!;
        internal TakeoutCompletedInventory Completed{get;private set;}=null!;
        internal TakeoutAssociationCatalog Catalog{get;private set;}=null!;
        internal TakeoutVerifiedOriginal Original{get;private set;}=null!;
        internal TakeoutCapturedEntry Sidecar{get;private set;}=null!;
        internal byte[] Raw{get;}=Encoding.UTF8.GetBytes("{\"title\":\"a.jpg\",\"description\":\"Imported detail\",\"photoTakenTime\":{\"timestamp\":\"1\"},\"people\":[{\"name\":\"Imported hint\"}]}");
        internal TakeoutMetadataDocument Document=>TakeoutMetadataDocument.ParseSource(Raw);
        internal ArtifactDescriptor Descriptor=>new(Raw.Length,Convert.ToHexStringLower(SHA256.HashData(Raw)),"input.bin");
        internal CancellationToken Token=>_deadline.Token;
        internal string StateDirectory=>_database.StateDirectory;
        internal static async Task<ImportedFixture> CreateAsync(int additionalSidecars=0)
        {
            var fixture=new ImportedFixture();try{await fixture.InitializeAsync(additionalSidecars).ConfigureAwait(false);return fixture;}catch{await fixture.DisposeAsync().ConfigureAwait(false);throw;}
        }
        private async Task InitializeAsync(int additionalSidecars)
        {
            var input=Path.Combine(Root.Path,"input");Directory.CreateDirectory(input);await File.WriteAllBytesAsync(Path.Combine(input,"a.jpg"),new byte[]{1,2,3},Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(input,"a.jpg.json"),Raw,Token).ConfigureAwait(false);
            for(var ordinal=0;ordinal<additionalSidecars;ordinal++)await File.WriteAllBytesAsync(Path.Combine(input,"extra-"+ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)+".json"),Raw,Token).ConfigureAwait(false);
            _database=new(Path.Combine(Root.Path,"state"));Budget=new(new MeasuredSpace(_database.StateDirectory));
            using(var selected=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),input))
            {
                var imports=new TakeoutImportInventoryRepository(_database);var id=Guid.NewGuid();imports.Ensure(id,new(Guid.NewGuid(),"account-one",1,"destination-one",1),[selected.Binding]);
                var work=imports.Claim(id)!;using var catalog=TakeoutAssociationCatalog.Open(_database,work,Budget,Token);catalog.AddPart(selected,Token);await catalog.SealAsync(Token).ConfigureAwait(false);imports.Complete(work,catalog);Completed=imports.ReadCompleted(id)!;
            }
            Catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(Completed,Budget,Token).ConfigureAwait(false);Sidecar=Catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single(entry=>entry.RelativePath=="a.jpg.json");
            await using(var source=await TakeoutOriginalReader.CaptureAsync(Completed,Catalog,Catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single(),Token).ConfigureAwait(false))
            {
                var originals=new TakeoutOriginalJobRepository(_database);var id=originals.Ensure(source.Capture,Guid.NewGuid());var work=originals.ObserveAcknowledgedBytes(originals.RetainAllocatedFileId(originals.Claim(id)!,"first-verified-file"),3);
                originals.ConfirmVerified(work,"account-one","destination-one","first-verified-file",3,7,source.Capture.Sha256);Original=originals.ReadVerified(Completed.ImportId,source.Capture.Entry.Key)!;
            }
            Storage=new(_database,Budget);State=new(_database);
        }
        public async ValueTask DisposeAsync()
        {Storage?.Dispose();Catalog?.Dispose();if(State is not null)await State.DisposeAsync().ConfigureAwait(false);_database?.Dispose();_deadline.Dispose();Root.Dispose();}
    }
}
