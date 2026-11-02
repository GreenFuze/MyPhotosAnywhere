using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutSelectedInputTests
{
    private const string Sidecar="{\"title\":\"שלום.jpg\",\"photoTakenTime\":{\"timestamp\":\"-1\"},\"description\":\"keep original\"}";

    [TestMethod]
    public async Task TakeoutSelectedFolderReadsOnlyItsOwnOriginalEntriesAndRetainsNativeLeases()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var folder=Path.Combine(root.Path,"selected");Directory.CreateDirectory(Path.Combine(folder,"album"));var path=Path.Combine(folder,"album","שלום.jpg");
        await File.WriteAllBytesAsync(path,"original bytes"u8.ToArray(),deadline.Token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(folder,"album","שלום.jpg.json"),Sidecar,deadline.Token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(folder,"album","extra.xmp"),"retained unsupported XMP",deadline.Token).ConfigureAwait(false);
        using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);var entries=input.Enumerate(deadline.Token).ToArray();Assert.AreEqual(3,entries.Length);
        var media=entries.Single(e=>e.Kind==TakeoutEntryKind.Media);Assert.AreEqual("album/שלום.jpg",media.RelativePath);Assert.AreEqual(14L,media.Bytes);
        using var foreign=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);
        Assert.ThrowsExactly<InvalidOperationException>(()=>foreign.OpenRead(media,1024));
        await using(var stream=input.OpenRead(media,1024))
        {
            Assert.IsFalse(stream.CanWrite);Assert.IsFalse(stream.CanSeek);Assert.ThrowsExactly<NotSupportedException>(()=>stream.WriteByte(1));
            Assert.ThrowsExactly<InvalidOperationException>(input.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(()=>input.OpenRead(entries.Single(e=>e.Kind==TakeoutEntryKind.JsonSidecar),128*1024));
            Assert.ThrowsExactly<IOException>(()=>{using var writer=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
            var bytes=await ReadAllAsync(stream,deadline.Token).ConfigureAwait(false);CollectionAssert.AreEqual("original bytes"u8.ToArray(),bytes);
        }
        CollectionAssert.AreEqual("original bytes"u8.ToArray(),await File.ReadAllBytesAsync(path,deadline.Token).ConfigureAwait(false));
        Assert.AreEqual(media.Key,input.Enumerate(deadline.Token).Single(e=>e.Kind==TakeoutEntryKind.Media).Key);
        Assert.AreEqual("retained unsupported XMP",await File.ReadAllTextAsync(Path.Combine(folder,"album","extra.xmp"),deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutSelectedZipStreamsVerifiedEntriesAndKeepsDuplicateNamesAsDistinctInputs()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path=await ZipAsync(root.Path,[("album/שלום.jpg","first"u8.ToArray()),("album/שלום.jpg","second"u8.ToArray()),("album/שלום.jpg.json",Encoding.UTF8.GetBytes(Sidecar))],deadline.Token).ConfigureAwait(false);
        using var input=await OpenZipAsync(path,deadline.Token).ConfigureAwait(false);var entries=input.Enumerate(deadline.Token).ToArray();Assert.AreEqual(3,entries.Length);
        Assert.AreEqual(3,entries.Select(e=>e.Key).Distinct(StringComparer.Ordinal).Count());Assert.AreEqual(entries[0].RelativePath,entries[1].RelativePath);Assert.AreNotEqual(entries[0].Key,entries[1].Key);
        await using(var read=input.OpenRead(entries[0],128*1024))
        {Assert.AreEqual(0,await read.ReadAsync(Memory<byte>.Empty,deadline.Token).ConfigureAwait(false));CollectionAssert.AreEqual("first"u8.ToArray(),await ReadAllAsync(read,deadline.Token).ConfigureAwait(false));}
        await using(var read=input.OpenRead(entries[1],128*1024))CollectionAssert.AreEqual("second"u8.ToArray(),await ReadAllAsync(read,deadline.Token).ConfigureAwait(false));
        Assert.ThrowsExactly<IOException>(()=>{using var writer=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
    }

    [TestMethod]
    public async Task TakeoutZip64IsBoundedBeforeManagedInventoryAndReadsTheSameOriginalBytes()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path=await ZipAsync(root.Path,[("a.jpg","original"u8.ToArray())],deadline.Token).ConfigureAwait(false);
        var bytes=ToZip64(await File.ReadAllBytesAsync(path,deadline.Token).ConfigureAwait(false));await File.WriteAllBytesAsync(path,bytes,deadline.Token).ConfigureAwait(false);
        using var input=await OpenZipAsync(path,deadline.Token).ConfigureAwait(false);var entry=input.Enumerate(deadline.Token).Single();
        await using var read=input.OpenRead(entry,1024);CollectionAssert.AreEqual("original"u8.ToArray(),await ReadAllAsync(read,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutZip64WithUnsaturatedLegacyFieldsStillUsesItsExactBoundedDirectory()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path=await ZipAsync(root.Path,[("a.jpg","original"u8.ToArray())],deadline.Token).ConfigureAwait(false);
        var original=await File.ReadAllBytesAsync(path,deadline.Token).ConfigureAwait(false);var legacy=original.AsSpan(Signature(original,0x06054b50)).ToArray();
        var bytes=ToZip64(original);legacy.CopyTo(bytes.AsSpan(bytes.Length-legacy.Length));await File.WriteAllBytesAsync(path,bytes,deadline.Token).ConfigureAwait(false);
        using var input=await OpenZipAsync(path,deadline.Token).ConfigureAwait(false);var entry=input.Enumerate(deadline.Token).Single();
        await using var read=input.OpenRead(entry,1024);CollectionAssert.AreEqual("original"u8.ToArray(),await ReadAllAsync(read,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutFolderRejectsChangedInventoryInsteadOfBindingMetadataToReplacementBytes()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var path=Path.Combine(root.Path,"a.jpg");await File.WriteAllBytesAsync(path,"original"u8.ToArray()).ConfigureAwait(false);
        using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),root.Path);var old=input.Enumerate().Single();
        await File.WriteAllBytesAsync(path,"replacement longer"u8.ToArray()).ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidDataException>(()=>input.OpenRead(old,1024));CollectionAssert.AreEqual("replacement longer"u8.ToArray(),await File.ReadAllBytesAsync(path).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutFolderRejectsAnActualNativeDirectoryLinkAndKeepsTheOriginalUntouched()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var original=Path.GetFullPath(Path.Combine(root.Path,"original"));var selected=Path.GetFullPath(Path.Combine(root.Path,"selected"));var link=Path.Combine(selected,"linked");
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;var prefix=Path.GetFullPath(root.Path)+Path.DirectorySeparatorChar;
        Assert.IsTrue(original.StartsWith(prefix,comparison)&&selected.StartsWith(prefix,comparison)&&link.StartsWith(prefix,comparison));Directory.CreateDirectory(original);Directory.CreateDirectory(selected);
        var path=Path.Combine(original,"a.jpg");await File.WriteAllBytesAsync(path,"untouched original"u8.ToArray(),deadline.Token).ConfigureAwait(false);
        if(OperatingSystem.IsWindows())
        {
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[]{"/d","/c","mklink","/J",link,original})start.ArgumentList.Add(argument);
            using var process=Process.Start(start)??throw new InvalidOperationException("Could not start the owned native junction fixture.");
            try {await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);}
            catch {if(!process.HasExited)process.Kill();await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);throw;}
            Assert.AreEqual(0,process.ExitCode,await process.StandardError.ReadToEndAsync(deadline.Token).ConfigureAwait(false));
        }
        else Directory.CreateSymbolicLink(link,original);
        try
        {
            Assert.IsTrue((File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0);
            using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),selected);Assert.ThrowsExactly<IOException>(()=>input.Enumerate(deadline.Token).ToArray());
            CollectionAssert.AreEqual("untouched original"u8.ToArray(),await File.ReadAllBytesAsync(path,deadline.Token).ConfigureAwait(false));
        }
        finally
        {
            // Unlink the exact synthetic junction before the ordinary recursive
            // fixture cleanup visits its target. Never recurse through a link.
            var retained=new DirectoryInfo(link);Assert.IsTrue((retained.Attributes&FileAttributes.ReparsePoint)!=0);
            Assert.IsTrue(string.Equals(original,retained.ResolveLinkTarget(false)?.FullName,comparison));
            Directory.Delete(link,recursive:false);Assert.IsFalse(Directory.Exists(link));
            CollectionAssert.AreEqual("untouched original"u8.ToArray(),await File.ReadAllBytesAsync(path,deadline.Token).ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task TakeoutMetadataByteLimitIsCheckedBeforeAnyInputReaderIsAdmitted()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var oversized=new byte[TakeoutMetadataDocument.MaximumSourceBytes+1];
        await File.WriteAllBytesAsync(Path.Combine(root.Path,"a.jpg.json"),oversized).ConfigureAwait(false);await File.WriteAllBytesAsync(Path.Combine(root.Path,"a.jpg"),"original"u8.ToArray()).ConfigureAwait(false);
        using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),root.Path);var entries=input.Enumerate().ToArray();
        Assert.ThrowsExactly<InvalidDataException>(()=>input.OpenRead(entries.Single(e=>e.Kind==TakeoutEntryKind.JsonSidecar),TakeoutMetadataDocument.MaximumSourceBytes));
        await using var read=input.OpenRead(entries.Single(e=>e.Kind==TakeoutEntryKind.Media),1024);CollectionAssert.AreEqual("original"u8.ToArray(),await ReadAllAsync(read,CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(oversized.Length,new FileInfo(Path.Combine(root.Path,"a.jpg.json")).Length);
    }

    [TestMethod]
    public async Task TakeoutZipRejectsChangedCapturedHashAndReleasesTheActualArchiveReader()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var path=await ZipAsync(root.Path,[("a.jpg","original"u8.ToArray())],CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutSelectedInput.OpenZipAsync(Guid.NewGuid(),path,new FileInfo(path).Length,new string('a',64),CancellationToken.None)).ConfigureAwait(false);
        using var released=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.IsGreaterThan(22L,released.Length);
    }

    [TestMethod]
    [DataRow("../a.jpg")]
    [DataRow("/a.jpg")]
    [DataRow("a/../b.jpg")]
    [DataRow("C:/a.jpg")]
    [DataRow("a\\b.jpg")]
    [DataRow("a./b.jpg")]
    [DataRow("CON.jpg")]
    [DataRow("a//b.jpg")]
    public async Task TakeoutZipRejectsUnsafeLogicalPathsWithoutExtractingAnything(string name)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var path=await ZipAsync(root.Path,[(name,"original"u8.ToArray())],CancellationToken.None).ConfigureAwait(false);
        var before=await File.ReadAllBytesAsync(path).ConfigureAwait(false);using var input=await OpenZipAsync(path,CancellationToken.None).ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidDataException>(()=>input.Enumerate().ToArray());Assert.AreEqual(1,Directory.GetFiles(root.Path).Length);Assert.AreEqual(0,Directory.GetDirectories(root.Path).Length);
        CollectionAssert.AreEqual(before,await File.ReadAllBytesAsync(path).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(0xa000)]
    [DataRow(0x1000)]
    public async Task TakeoutZipRefusesSymlinkAndSpecialNativeEntries(int mode)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var path=Path.Combine(root.Path,"input.zip");
        using(var file=File.Create(path))using(var archive=new ZipArchive(file,ZipArchiveMode.Create))
        {var entry=archive.CreateEntry("a.jpg",CompressionLevel.NoCompression);entry.ExternalAttributes=unchecked(mode<<16);using var output=entry.Open();output.Write("original"u8);}
        using var input=await OpenZipAsync(path,CancellationToken.None).ConfigureAwait(false);Assert.ThrowsExactly<InvalidDataException>(()=>input.Enumerate().ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TakeoutZipReadsRejectWrongCrcOrDeclaredExpansionEvenWhenArchiveHashMatches(bool changedSize)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var path=await ZipAsync(root.Path,[("a.jpg","original"u8.ToArray())],CancellationToken.None).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);var central=Signature(bytes,0x02014b50);
        var at=central+(changedSize?24:16);var value=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value+1);
        await File.WriteAllBytesAsync(path,bytes).ConfigureAwait(false);using var input=await OpenZipAsync(path,CancellationToken.None).ConfigureAwait(false);var entry=input.Enumerate().Single();
        await using var read=input.OpenRead(entry,1024);await Assert.ThrowsAsync<InvalidDataException>(()=>ReadAllAsync(read,CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(8,1)]
    [DataRow(10,99)]
    public async Task TakeoutZipPreflightRejectsEncryptedAndUnsupportedEntriesBeforeOpeningThem(int field,int value)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var path=await ZipAsync(root.Path,[("a.jpg","original"u8.ToArray())],CancellationToken.None).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(Signature(bytes,0x02014b50)+field,2),(ushort)value);await File.WriteAllBytesAsync(path,bytes).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>OpenZipAsync(path,CancellationToken.None)).ConfigureAwait(false);
        using var released=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }

    [TestMethod]
    [DataRow("directory-bytes")]
    [DataRow("entry-count")]
    [DataRow("physical-range")]
    public async Task TakeoutZipPreflightRejectsFalseCentralDirectoryBudgetsBeforeManagedAllocation(string kind)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var path=await ZipAsync(root.Path,[("a.jpg","original"u8.ToArray())],CancellationToken.None).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        if(kind=="entry-count")
        {
            bytes=ToZip64(bytes);var record=Signature(bytes,0x06064b50);BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(record+24,8),100001);BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(record+32,8),100001);
        }
        else
        {
            var end=Signature(bytes,0x06054b50);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end+(kind=="directory-bytes"?12:16),4),kind=="directory-bytes"?64*1024*1024u:(uint)bytes.Length);
        }
        await File.WriteAllBytesAsync(path,bytes).ConfigureAwait(false);await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>OpenZipAsync(path,CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TakeoutEntryAsyncDisposalJoinsActualOutstandingReadBeforeReleasingSourceOwnership()
    {
        var releases=0;await using var input=new GatedRead("read"u8.ToArray());var stream=new TakeoutInputReadStream(input,4,null,()=>releases++);
        var buffer=new byte[4];var read=stream.ReadAsync(buffer.AsMemory()).AsTask();await input.Entered.Task.ConfigureAwait(false);
        var disposal=stream.DisposeAsync().AsTask();Assert.IsFalse(disposal.IsCompleted);Assert.AreEqual(0,releases);Assert.IsFalse(input.Retired);
        try
        {await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async()=>await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)).ConfigureAwait(false);}
        finally {input.AllowRead.TrySetResult();}
        Assert.AreEqual(4,await read.ConfigureAwait(false));await disposal.ConfigureAwait(false);Assert.IsTrue(input.Retired);Assert.AreEqual(1,releases);CollectionAssert.AreEqual("read"u8.ToArray(),buffer);
        await stream.DisposeAsync().ConfigureAwait(false);Assert.AreEqual(1,releases);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TakeoutFolderAndZipOriginalStreamsFeedTheActualDurableMetadataWorkerWithoutChangingInputs(bool zip)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var media="untouched original photo"u8.ToArray();var raw=Encoding.UTF8.GetBytes(Sidecar);var sourceRoot=Path.Combine(root.Path,"selected");Directory.CreateDirectory(sourceRoot);
        var archive=await ZipAsync(sourceRoot,[("album/שלום.jpg",media),("album/שלום.jpg.json",raw)],deadline.Token).ConfigureAwait(false);
        var folder=Path.Combine(sourceRoot,"folder");Directory.CreateDirectory(Path.Combine(folder,"album"));await File.WriteAllBytesAsync(Path.Combine(folder,"album","שלום.jpg"),media,deadline.Token).ConfigureAwait(false);await File.WriteAllBytesAsync(Path.Combine(folder,"album","שלום.jpg.json"),raw,deadline.Token).ConfigureAwait(false);
        using var input=zip?await OpenZipAsync(archive,deadline.Token).ConfigureAwait(false):TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);
        var entries=input.Enumerate(deadline.Token).ToArray();var original=entries.Single(e=>e.Kind==TakeoutEntryKind.Media);var sidecar=entries.Single(e=>e.Kind==TakeoutEntryKind.JsonSidecar);
        string originalHash;
        await using(var source=input.OpenRead(original,1024))originalHash=Convert.ToHexStringLower(await SHA256.HashDataAsync(source,deadline.Token).ConfigureAwait(false));
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));await using var state=new ProcessorStateCoordinator(database);
        var work=await state.ExecuteAsync(db=>
        {
            var jobs=new TakeoutMetadataJobRepository(db);jobs.Ensure(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),originalHash,new(raw.Length,Convert.ToHexStringLower(SHA256.HashData(raw)),"input.bin"));return jobs.ClaimNext()!;
        },deadline.Token).ConfigureAwait(false);
        var budget=new StorageBudgetManager(new Space(database.StateDirectory));using var staging=new TakeoutMetadataStageStorage(database,budget);
        await using(var source=input.OpenRead(sidecar,TakeoutMetadataDocument.MaximumSourceBytes))await staging.CaptureAsync(work,source,deadline.Token).ConfigureAwait(false);
        var runtime=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet"));var worker=Path.Combine(AppContext.BaseDirectory,"ProductWorker","Mpa.Processor.WorkerHost.dll");
        var installation=new TakeoutWorkerInstallation(runtime,await DigestAsync(runtime,deadline.Token).ConfigureAwait(false),worker,await DigestAsync(worker,deadline.Token).ConfigureAwait(false));
        await using var supervisor=new ProcessorWorkSupervisor();await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(state,work,staging,installation,deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false);
        await state.ExecuteAsync(db=>{var jobs=new DurableJobRepository(db);Assert.AreEqual(1,jobs.Summary(work.Lease.JobId).Ready);Assert.AreEqual(1,jobs.PendingReceiptCount());return true;},deadline.Token).ConfigureAwait(false);
        await using(var source=input.OpenRead(original,1024))CollectionAssert.AreEqual(media,await ReadAllAsync(source,deadline.Token).ConfigureAwait(false));
        await using(var source=input.OpenRead(sidecar,TakeoutMetadataDocument.MaximumSourceBytes))CollectionAssert.AreEqual(raw,await ReadAllAsync(source,deadline.Token).ConfigureAwait(false));
        Assert.AreEqual(0L,budget.Status().ReservedBytes);Assert.IsNull(supervisor.Failure);
    }

    private static async Task<TakeoutSelectedInput> OpenZipAsync(string path,CancellationToken token)=>await TakeoutSelectedInput.OpenZipAsync(Guid.NewGuid(),path,new FileInfo(path).Length,await DigestAsync(path,token).ConfigureAwait(false),token).ConfigureAwait(false);
    private static async Task<string> DigestAsync(string path,CancellationToken token)
    {await using var stream=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token).ConfigureAwait(false));}
    private static async Task<byte[]> ReadAllAsync(Stream source,CancellationToken token)
    {using var output=new MemoryStream();await source.CopyToAsync(output,token).ConfigureAwait(false);return output.ToArray();}
    private static async Task<string> ZipAsync(string root,(string Name,byte[] Bytes)[] entries,CancellationToken token)
    {
        var path=Path.Combine(root,"input.zip");
        await using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            using var archive=new ZipArchive(file,ZipArchiveMode.Create,leaveOpen:true);
            foreach(var item in entries){var entry=archive.CreateEntry(item.Name,CompressionLevel.NoCompression);await using var output=entry.Open();await output.WriteAsync(item.Bytes,token).ConfigureAwait(false);}
        }
        return path;
    }
    private static int Signature(byte[] bytes,uint signature)
    {for(var at=0;at<=bytes.Length-4;at++)if(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4))==signature)return at;throw new InvalidDataException("Missing synthetic ZIP signature.");}
    private static byte[] ToZip64(byte[] bytes)
    {
        var end=Signature(bytes,0x06054b50);var output=new byte[bytes.Length+76];bytes.AsSpan(0,end).CopyTo(output);
        var record=output.AsSpan(end,56);BinaryPrimitives.WriteUInt32LittleEndian(record,0x06064b50);BinaryPrimitives.WriteUInt64LittleEndian(record[4..],44);BinaryPrimitives.WriteUInt16LittleEndian(record[12..],45);BinaryPrimitives.WriteUInt16LittleEndian(record[14..],45);
        var count=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end+10,2));var size=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end+12,4));var start=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end+16,4));
        BinaryPrimitives.WriteUInt64LittleEndian(record[24..],count);BinaryPrimitives.WriteUInt64LittleEndian(record[32..],count);BinaryPrimitives.WriteUInt64LittleEndian(record[40..],size);BinaryPrimitives.WriteUInt64LittleEndian(record[48..],start);
        var locator=output.AsSpan(end+56,20);BinaryPrimitives.WriteUInt32LittleEndian(locator,0x07064b50);BinaryPrimitives.WriteUInt64LittleEndian(locator[8..],(ulong)end);BinaryPrimitives.WriteUInt32LittleEndian(locator[16..],1);
        bytes.AsSpan(end).CopyTo(output.AsSpan(end+76));var eocd=output.AsSpan(end+76);BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..],ushort.MaxValue);BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..],ushort.MaxValue);BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..],uint.MaxValue);BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..],uint.MaxValue);return output;
    }
    private sealed class Space(string root) : IStorageSpaceReader
    {public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,40*StorageBudgetManager.Gibibyte,TakeoutMetadataStageStorage.MeasureOwnedBytes(root));}
    private sealed class GatedRead(byte[] bytes) : MemoryStream(bytes,writable:false)
    {
        internal TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowRead { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Retired { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {Entered.TrySetResult();await AllowRead.Task.WaitAsync(TimeSpan.FromSeconds(30),cancellationToken).ConfigureAwait(false);return await base.ReadAsync(buffer,cancellationToken).ConfigureAwait(false);}
        public override async ValueTask DisposeAsync(){await base.DisposeAsync().ConfigureAwait(false);Retired=true;GC.SuppressFinalize(this);}
    }
}
