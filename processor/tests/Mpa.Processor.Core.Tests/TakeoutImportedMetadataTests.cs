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
        internal static async Task<ImportedFixture> CreateAsync()
        {
            var fixture=new ImportedFixture();try{await fixture.InitializeAsync().ConfigureAwait(false);return fixture;}catch{await fixture.DisposeAsync().ConfigureAwait(false);throw;}
        }
        private async Task InitializeAsync()
        {
            var input=Path.Combine(Root.Path,"input");Directory.CreateDirectory(input);await File.WriteAllBytesAsync(Path.Combine(input,"a.jpg"),new byte[]{1,2,3},Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(input,"a.jpg.json"),Raw,Token).ConfigureAwait(false);_database=new(Path.Combine(Root.Path,"state"));Budget=new(new MeasuredSpace(_database.StateDirectory));
            using(var selected=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),input))
            {
                var imports=new TakeoutImportInventoryRepository(_database);var id=Guid.NewGuid();imports.Ensure(id,new(Guid.NewGuid(),"account-one",1,"destination-one",1),[selected.Binding]);
                var work=imports.Claim(id)!;using var catalog=TakeoutAssociationCatalog.Open(_database,work,Budget,Token);catalog.AddPart(selected,Token);await catalog.SealAsync(Token).ConfigureAwait(false);imports.Complete(work,catalog);Completed=imports.ReadCompleted(id)!;
            }
            Catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(Completed,Budget,Token).ConfigureAwait(false);Sidecar=Catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single();
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
