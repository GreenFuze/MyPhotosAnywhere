using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutOriginalReaderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualFolderAndZipOriginalsRemainOwnedFromCompleteHashThroughBoundedVerifiedReplay(bool zip)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var payload=RandomNumberGenerator.GetBytes(1024*1024+19);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var completed=await InventoryAsync(database,root.Path,payload,zip,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        var entry=catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single();var original=completed.Parts.Single().Location;
        var reader=await TakeoutOriginalReader.CaptureAsync(completed,catalog,entry,deadline.Token).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(0L,reader.Position);Assert.IsFalse(reader.ReplayVerified);Assert.AreEqual(entry,reader.Capture.Entry);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)),reader.Capture.Sha256);Assert.AreEqual("a.jpg",reader.Capture.Name);Assert.AreEqual("image/jpeg",reader.Capture.MimeType);
            Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount()); // inventory, not backup
            await Assert.ThrowsExactlyAsync<ArgumentException>(()=>reader.ReadAsync(Memory<byte>.Empty,deadline.Token).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(()=>reader.ReadAsync(new byte[TakeoutOriginalReader.MaximumReadBytes+1],deadline.Token).AsTask()).ConfigureAwait(false);
            var bytes=new byte[65537];using var actual=new MemoryStream();
            int count;while((count=await reader.ReadAsync(bytes,deadline.Token).ConfigureAwait(false))!=0)actual.Write(bytes,0,count);
            CollectionAssert.AreEqual(payload,actual.ToArray());Assert.AreEqual((long)payload.Length,reader.Position);Assert.IsTrue(reader.ReplayVerified);
            Assert.AreEqual(0,await reader.ReadAsync(bytes,deadline.Token).ConfigureAwait(false));Assert.IsTrue(reader.ReplayVerified);
            if(OperatingSystem.IsWindows())Assert.ThrowsExactly<IOException>(()=>File.Open(zip?original:Path.Combine(original,"a.jpg"),FileMode.Open,FileAccess.ReadWrite,FileShare.None));
        }
        finally {await reader.DisposeAsync().ConfigureAwait(false);}
        await reader.DisposeAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>reader.ReadAsync(new byte[1],deadline.Token).AsTask()).ConfigureAwait(false);
        using var exclusive=File.Open(zip?original:Path.Combine(original,"a.jpg"),FileMode.Open,FileAccess.Read,FileShare.None);
        Assert.AreEqual(1,new DurableJobRepository(database).Summary(completed.ImportId).Ready);
    }

    [TestMethod]
    public async Task ForgedRowsAndForeignCompletedCatalogsCannotCaptureAnyOriginal()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var first=await InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        var second=await InventoryAsync(database,root.Path,[4,5,6],false,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(first,Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        var entry=catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutOriginalReader.CaptureAsync(second,catalog,entry,deadline.Token)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutOriginalReader.CaptureAsync(first,catalog,entry with{MediaId=Guid.NewGuid()},deadline.Token)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutOriginalReader.CaptureAsync(first,catalog,entry with{RelativePath="elsewhere.jpg"},deadline.Token)).ConfigureAwait(false);
        Assert.AreEqual(2,new DurableJobRepository(database).PendingReceiptCount());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedSelectedInputCannotSilentlyRecaptureTheHistoricalOriginal(bool zip)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var completed=await InventoryAsync(database,root.Path,[1,2,3],zip,deadline.Token).ConfigureAwait(false);
        var part=completed.Parts.Single();var path=zip?part.Location:Path.Combine(part.Location,"a.jpg");
        await File.WriteAllBytesAsync(path,[4,5,6,7],deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutOriginalReader.CaptureAsync(completed,catalog,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single(),deadline.Token)).ConfigureAwait(false);
        CollectionAssert.AreEqual(new byte[]{4,5,6,7},await File.ReadAllBytesAsync(path,deadline.Token).ConfigureAwait(false));
        using var exclusive=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.None);
    }

    [TestMethod]
    public async Task OriginalCancellationAndPartialDisposalNeverBecomeVerifiedBackupEvidence()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var completed=await InventoryAsync(database,root.Path,[1,2,3],false,deadline.Token).ConfigureAwait(false);
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);
        var entry=catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single();using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>TakeoutOriginalReader.CaptureAsync(completed,catalog,entry,canceled.Token)).ConfigureAwait(false);
        await using(var reader=await TakeoutOriginalReader.CaptureAsync(completed,catalog,entry,deadline.Token).ConfigureAwait(false))
        {
            Assert.AreEqual(1,await reader.ReadAsync(new byte[1],deadline.Token).ConfigureAwait(false));Assert.IsFalse(reader.ReplayVerified);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>reader.ReadAsync(new byte[1],canceled.Token).AsTask()).ConfigureAwait(false);
            Assert.IsFalse(reader.ReplayVerified);
        }
        Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount());
        using var exclusive=File.Open(Path.Combine(completed.Parts.Single().Location,"a.jpg"),FileMode.Open,FileAccess.Read,FileShare.None);
    }

    [TestMethod]
    public void InternalRewindRequiresActualNonemptyEofAndPreservesPublicNoSeekContract()
    {
        var releases=0;using var stream=new TakeoutInputReadStream(new MemoryStream([1,2,3]),3,null,()=>releases++);
        Assert.IsFalse(stream.CanSeek);Assert.ThrowsExactly<NotSupportedException>(()=>stream.Seek(0,SeekOrigin.Begin));
        Assert.AreEqual(3,stream.Read(new byte[3]));Assert.AreEqual(0,stream.Read(Span<byte>.Empty));
        Assert.ThrowsExactly<InvalidOperationException>(()=>stream.TryRewindVerified());
        // The rejected replay poisoned this attempt; retry must use a fresh owner.
        Assert.ThrowsExactly<InvalidDataException>(()=>stream.Read(new byte[1]));Assert.AreEqual(0,releases);
    }

    internal static async Task<TakeoutCompletedInventory> InventoryAsync(ProcessorStateDatabase database,string root,byte[] payload,bool zip,CancellationToken token)
    {
        var path=Path.Combine(root,Guid.NewGuid().ToString("N"));TakeoutSelectedInput selected;
        if(zip)
        {
            path+=".zip";using(var archive=ZipFile.Open(path,ZipArchiveMode.Create))await using(var output=archive.CreateEntry("a.jpg").Open())await output.WriteAsync(payload,token).ConfigureAwait(false);
            await using var bytes=File.OpenRead(path);selected=await TakeoutSelectedInput.OpenZipAsync(Guid.NewGuid(),path,bytes.Length,Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes,token).ConfigureAwait(false)),token).ConfigureAwait(false);
        }
        else {Directory.CreateDirectory(path);await File.WriteAllBytesAsync(Path.Combine(path,"a.jpg"),payload,token).ConfigureAwait(false);selected=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),path);}
        using(selected)
        {
            var jobs=new TakeoutImportInventoryRepository(database);var id=Guid.NewGuid();jobs.Ensure(id,new(Guid.NewGuid(),"account-one",1,"destination-one",1),[selected.Binding]);var work=jobs.Claim(id)!;
            using var catalog=TakeoutAssociationCatalog.Open(database,work,Budget(database.StateDirectory),token);catalog.AddPart(selected,token);await catalog.SealAsync(token).ConfigureAwait(false);jobs.Complete(work,catalog);return jobs.ReadCompleted(id)!;
        }
    }
    internal static StorageBudgetManager Budget(string state)=>new(new Space(state));
    private sealed class Space(string state) : IStorageSpaceReader
    {public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,40*StorageBudgetManager.Gibibyte,TakeoutAssociationCatalog.MeasureOwnedBytes(state));}
}
