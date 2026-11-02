using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutAssociationTests
{
    [TestMethod]
    public void TakeoutImportCapturesDestinationAndAllPartsAndClaimsOnlyTheRequestedJob()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));
        var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);using var source=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);
        var repository=new TakeoutImportInventoryRepository(database);var first=Guid.Parse("00000001-0000-0000-0000-000000000001");var second=Guid.Parse("00000002-0000-0000-0000-000000000002");var target=Target();
        Assert.IsTrue(repository.Ensure(first,target,[source.Binding]));Assert.IsFalse(repository.Ensure(first,target,[source.Binding]));Assert.IsTrue(repository.Ensure(second,target,[source.Binding]));
        Assert.ThrowsExactly<ConflictingJobItemException>(()=>repository.Ensure(first,target with{FolderId="replacement"},[source.Binding]));
        Assert.ThrowsExactly<ConflictingJobItemException>(()=>repository.Ensure(first,target,[source.Binding with{Location=Path.Combine(root.Path,"different")} ]));
        var work=repository.Claim(second)!;Assert.AreEqual(second,work.Lease.JobId);Assert.AreEqual(target,work.Target);Assert.AreEqual(source.Binding,work.Parts.Single());
        Assert.AreEqual(1,new DurableJobRepository(database).Summary(first).Queued);Assert.IsNull(repository.Claim(second));Assert.IsNull(repository.Claim(Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentException>(()=>new DurableJobRepository(database).ClaimNext(jobId:Guid.Empty));repository.AssertCurrent(work);
        Assert.IsNotNull(repository.Claim(first));Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
    }

    [TestMethod]
    public void TakeoutImportRetainsFullInt64RevisionsAndRejectsForeignOrPausedNativeOwners()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));using var foreign=new ProcessorStateDatabase(Path.Combine(root.Path,"foreign"));
        var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();
        var target=Target() with{AccountRevision=long.MaxValue,ConfigurationRevision=long.MaxValue};repository.Ensure(job,target,[input.Binding]);var work=repository.Claim(job)!;Assert.AreEqual(target,work.Target);
        StringAssert.Contains(work.Lease.CapturedContext,"\"AccountRevision\":\"9223372036854775807\"");
        Assert.ThrowsExactly<InvalidOperationException>(()=>new TakeoutImportInventoryRepository(foreign).AssertCurrent(work));
        var stopped=new DurableJobRepository(database).Pause(job).Single();Assert.AreEqual(work.Lease.LeaseId,stopped.LeaseId);Assert.ThrowsExactly<StaleStageLeaseException>(()=>repository.AssertCurrent(work));
        Assert.ThrowsExactly<ArgumentException>(()=>repository.Ensure(Guid.NewGuid(),Target(),[]));Assert.ThrowsExactly<ArgumentException>(()=>repository.Ensure(Guid.NewGuid(),Target() with{FolderId="root"},[input.Binding]));
    }

    [TestMethod]
    [DataRow("exact","matched","a.jpg")]
    [DataRow("supplemental","matched","a.jpg")]
    [DataRow("duplicate-after-extension","matched","a(1).jpg")]
    [DataRow("duplicate-before-extension","matched","a(1).jpg")]
    [DataRow("missing-numbered","missing-media",null)]
    [DataRow("name-mismatch","name-mismatch",null)]
    [DataRow("case-collision","ambiguous-media",null)]
    [DataRow("nfc-collision","ambiguous-media",null)]
    [DataRow("truncated","matched",null)]
    [DataRow("other-album","missing-media",null)]
    public async Task TakeoutSidecarMatchingKeepsExactNamesDuplicateVariantsAndAmbiguityExplicit(string scenario,string status,string? expected)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var media=new List<string>{"album/a.jpg"};var sidecar="album/a.jpg.json";var title="a.jpg";
        switch(scenario)
        {
            case "supplemental":sidecar="album/a.jpg.supplemental-metadata.json";break;
            case "duplicate-after-extension":media.Add("album/a(1).jpg");sidecar="album/a.jpg(1).json";break;
            case "duplicate-before-extension":media.Add("album/a(1).jpg");sidecar="album/a(1).jpg.json";break;
            case "missing-numbered":sidecar="album/a.jpg(1).json";break;
            case "name-mismatch":media.Add("album/b.jpg");title="b.jpg";break;
            case "case-collision":media.Add("album/A.jpg");break;
            case "nfc-collision":media=["album/é.jpg","album/e\u0301.jpg"];sidecar="album/é.jpg.json";title="é.jpg";break;
            case "truncated":title=new string('x',60)+".jpg";media=["album/"+title];sidecar="album/"+title[..42]+".json";expected=title;break;
            case "other-album":media=["other/a.jpg"];break;
        }
        var path=await ZipAsync(root.Path,"input.zip",media.Append(sidecar).Select(n=>(n,Encoding.UTF8.GetBytes(n))).ToArray(),deadline.Token).ConfigureAwait(false);
        using var source=await OpenZipAsync(path,deadline.Token).ConfigureAwait(false);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));
        var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[source.Binding]);var work=repository.Claim(job)!;
        using var catalog=TakeoutAssociationCatalog.Open(database,work,budget,deadline.Token);catalog.AddPart(source,deadline.Token);
        Assert.ThrowsExactly<InvalidOperationException>(()=>catalog.ReadPage(TakeoutEntryKind.Media));await catalog.SealAsync(deadline.Token).ConfigureAwait(false);
        var side=catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single();var result=catalog.Associate(side.Key,title,deadline.Token);Assert.AreEqual(status,result.Status);
        if(expected is null)Assert.IsNull(result.Media);else{Assert.IsNotNull(result.Media);Assert.AreEqual("album/"+expected,result.Media.RelativePath);}
        Assert.AreEqual(media.Count,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Count);Assert.AreEqual(media.Count,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Select(e=>e.MediaId).Distinct().Count());
        repository.Complete(work,catalog);Assert.AreEqual(1,new DurableJobRepository(database).Summary(job).Ready);Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount());
    }

    [TestMethod]
    public async Task TakeoutMultipartAssociationFeedsActualRetainedMediaIdentityIntoDurableWorker()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var folder=Path.Combine(root.Path,"first");Directory.CreateDirectory(Path.Combine(folder,"Takeout","Google Photos","album"));var media="unchanged original bytes"u8.ToArray();var raw="{\"title\":\"a.jpg\",\"photoTakenTime\":{\"timestamp\":\"-1\"},\"description\":\"preserved import\"}"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(folder,"Takeout","Google Photos","album","a.jpg"),media,deadline.Token).ConfigureAwait(false);
        var zip=await ZipAsync(root.Path,"second.zip",[("album/a.jpg.supplemental-metadata.json",raw)],deadline.Token).ConfigureAwait(false);
        using var first=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);using var second=await OpenZipAsync(zip,deadline.Token).ConfigureAwait(false);
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));await using var state=new ProcessorStateCoordinator(database);var budget=new StorageBudgetManager(new Space(database.StateDirectory));var job=Guid.NewGuid();
        var work=await state.ExecuteAsync(db=>{var jobs=new TakeoutImportInventoryRepository(db);jobs.Ensure(job,Target(),[first.Binding,second.Binding]);return jobs.Claim(job)!;},deadline.Token).ConfigureAwait(false);
        using var catalog=await state.ExecuteAsync(db=>TakeoutAssociationCatalog.Open(db,work,budget,deadline.Token),deadline.Token).ConfigureAwait(false);
        catalog.AddPart(first,deadline.Token);await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>catalog.SealAsync(deadline.Token)).ConfigureAwait(false);
        catalog.AddPart(second,deadline.Token);await catalog.SealAsync(deadline.Token).ConfigureAwait(false);
        var sidecar=catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single();var parsed=TakeoutMetadataDocument.ParseSource(raw);var association=catalog.Associate(sidecar.Key,parsed.Title,deadline.Token);Assert.AreEqual("matched",association.Status);Assert.IsNotNull(association.Media);
        string mediaHash;await using(var input=catalog.OpenCaptured(first,association.Media,1024))mediaHash=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,deadline.Token).ConfigureAwait(false));
        var metadata=await state.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);jobs.Ensure(Guid.NewGuid(),Guid.NewGuid(),association.Media.MediaId,mediaHash,new(raw.Length,Convert.ToHexStringLower(SHA256.HashData(raw)),"input.bin"));return jobs.ClaimNext()!;},deadline.Token).ConfigureAwait(false);
        using var staging=new TakeoutMetadataStageStorage(database,budget);
        await using(var input=catalog.OpenCaptured(second,sidecar,TakeoutMetadataDocument.MaximumSourceBytes))await staging.CaptureAsync(metadata,input,deadline.Token).ConfigureAwait(false);
        await using var supervisor=new ProcessorWorkSupervisor();var installation=await InstallationAsync(deadline.Token).ConfigureAwait(false);
        await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(state,metadata,staging,installation,deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false);
        await state.ExecuteAsync(db=>{new TakeoutImportInventoryRepository(db).Complete(work,catalog);var jobs=new DurableJobRepository(db);Assert.AreEqual(2,jobs.PendingReceiptCount());Assert.AreEqual(1,jobs.Summary(job).Ready);Assert.AreEqual(1,jobs.Summary(metadata.Lease.JobId).Ready);return true;},deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(association.Media.MediaId,metadata.MediaId);Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(media)),metadata.MediaInputSha256);
        await using(var input=catalog.OpenCaptured(first,association.Media,1024))CollectionAssert.AreEqual(media,await ReadAllAsync(input,deadline.Token).ConfigureAwait(false));
        await using(var input=catalog.OpenCaptured(second,sidecar,TakeoutMetadataDocument.MaximumSourceBytes))CollectionAssert.AreEqual(raw,await ReadAllAsync(input,deadline.Token).ConfigureAwait(false));
        Assert.AreEqual(0L,budget.Status().ReservedBytes);Assert.IsNull(supervisor.Failure);
    }

    [TestMethod]
    public async Task TakeoutDuplicateMediaAcrossPartsStayDistinctAndDoNotAcquireAnArbitrarySidecar()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var one=await ZipAsync(root.Path,"one.zip",[("album/a.jpg","same bytes"u8.ToArray()),("album/a.jpg.json","metadata"u8.ToArray())],deadline.Token).ConfigureAwait(false);
        var two=await ZipAsync(root.Path,"two.zip",[("album/a.jpg","same bytes"u8.ToArray())],deadline.Token).ConfigureAwait(false);
        using var first=await OpenZipAsync(one,deadline.Token).ConfigureAwait(false);using var second=await OpenZipAsync(two,deadline.Token).ConfigureAwait(false);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));
        var repository=new TakeoutImportInventoryRepository(database);var id=Guid.NewGuid();repository.Ensure(id,Target(),[first.Binding,second.Binding]);var work=repository.Claim(id)!;using var catalog=TakeoutAssociationCatalog.Open(database,work,budget);catalog.AddPart(first);catalog.AddPart(second);await catalog.SealAsync(deadline.Token).ConfigureAwait(false);
        var media=catalog.ReadPage(TakeoutEntryKind.Media).Entries;Assert.AreEqual(2,media.Count);Assert.AreNotEqual(media[0].Key,media[1].Key);Assert.AreNotEqual(media[0].MediaId,media[1].MediaId);
        var sidecar=catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single();var result=catalog.Associate(sidecar.Key,"a.jpg");Assert.AreEqual("ambiguous-media",result.Status);Assert.IsNull(result.Media);
        Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
    }

    [TestMethod]
    public async Task TakeoutPartialInventoryRecoversExactRowsAndReusesVerifiedZipWithoutRescanningItsEntries()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var zip=await ZipAsync(root.Path,"first.zip",[("album/a.jpg","original"u8.ToArray())],deadline.Token).ConfigureAwait(false);var folder=Path.Combine(root.Path,"second");Directory.CreateDirectory(Path.Combine(folder,"album"));await File.WriteAllTextAsync(Path.Combine(folder,"album","a.jpg.json"),"{}",deadline.Token).ConfigureAwait(false);
        using var first=await OpenZipAsync(zip,deadline.Token).ConfigureAwait(false);using var second=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[first.Binding,second.Binding]);var old=repository.Claim(job)!;
        using(var partial=TakeoutAssociationCatalog.Open(database,old,budget)){partial.AddPart(first);await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>partial.SealAsync(deadline.Token)).ConfigureAwait(false);}
        var index=IndexPath(database.StateDirectory,job);var originalAsset=ReadFirstAsset(index);
        // This owner launches no child. Its actual native index/read owners above
        // have joined before the exact paused inventory lease can be returned.
        var jobs=new DurableJobRepository(database);jobs.Pause(job);jobs.ReleaseStoppedAttempt(old.Lease);jobs.Resume(job);var retry=repository.Claim(job)!;
        Assert.AreEqual(2,retry.Lease.Attempt);Assert.AreEqual(old.Lease.CapturedContext,retry.Lease.CapturedContext);
        using var resumed=TakeoutAssociationCatalog.Open(database,retry,budget);
        using(var held=first.Enumerate(deadline.Token).GetEnumerator())
        {
            Assert.IsTrue(held.MoveNext()); // A second source enumeration would fail.
            resumed.AddPart(first,deadline.Token);Assert.AreEqual(1,resumed.ReusedVerifiedZipParts);
        }
        resumed.AddPart(second,deadline.Token);await resumed.SealAsync(deadline.Token).ConfigureAwait(false);Assert.AreEqual(originalAsset,resumed.ReadPage(TakeoutEntryKind.Media).Entries.Single().MediaId);
        Assert.ThrowsExactly<InvalidOperationException>(()=>resumed.AddPart(second));repository.Complete(retry,resumed);Assert.AreEqual(1,jobs.PendingReceiptCount());
    }

    [TestMethod]
    public async Task TakeoutChangedFolderReplayCannotOverwriteRetainedOriginalInventory()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);var path=Path.Combine(folder,"a.jpg");await File.WriteAllBytesAsync(path,"original"u8.ToArray(),deadline.Token).ConfigureAwait(false);
        using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[input.Binding]);var work=repository.Claim(job)!;
        using(var original=TakeoutAssociationCatalog.Open(database,work,budget))original.AddPart(input);
        var asset=ReadFirstAsset(IndexPath(database.StateDirectory,job));await File.WriteAllBytesAsync(path,"changed original with a different length"u8.ToArray(),deadline.Token).ConfigureAwait(false);
        using(var retry=TakeoutAssociationCatalog.Open(database,work,budget))Assert.ThrowsExactly<InvalidDataException>(()=>retry.AddPart(input));
        Assert.AreEqual(asset,ReadFirstAsset(IndexPath(database.StateDirectory,job)));Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
        CollectionAssert.AreEqual("changed original with a different length"u8.ToArray(),await File.ReadAllBytesAsync(path,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutNativeIndexedNameCorruptionCannotHideAnOriginalOrBecomeAReadyInventory()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var zip=await ZipAsync(root.Path,"input.zip",[("a.jpg","original"u8.ToArray())],deadline.Token).ConfigureAwait(false);
        using var input=await OpenZipAsync(zip,deadline.Token).ConfigureAwait(false);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[input.Binding]);var work=repository.Claim(job)!;
        using(var first=TakeoutAssociationCatalog.Open(database,work,budget))first.AddPart(input);
        using(var connection=Native(IndexPath(database.StateDirectory,job),false)){using var command=connection.CreateCommand();command.CommandText="UPDATE entry SET fold_name='SUBSTITUTED.JPG'";Assert.AreEqual(1,command.ExecuteNonQuery());}
        using var reopened=TakeoutAssociationCatalog.Open(database,work,budget);Assert.ThrowsExactly<InvalidDataException>(()=>reopened.AddPart(input));await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>reopened.SealAsync(deadline.Token)).ConfigureAwait(false);
        Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());Assert.AreEqual(1,new DurableJobRepository(database).Summary(job).Running);
    }

    [TestMethod]
    public async Task TakeoutPausedNativeJobRejectsLateSealedIndexAndLeavesTheOriginalSelectionIntact()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"a.jpg"),"original",deadline.Token).ConfigureAwait(false);
        using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[input.Binding]);var work=repository.Claim(job)!;
        using var catalog=TakeoutAssociationCatalog.Open(database,work,budget);catalog.AddPart(input);await catalog.SealAsync(deadline.Token).ConfigureAwait(false);new DurableJobRepository(database).Pause(job);
        Assert.ThrowsExactly<StaleStageLeaseException>(()=>repository.Complete(work,catalog));Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());Assert.IsTrue(File.Exists(IndexPath(database.StateDirectory,job)));Assert.AreEqual("original",await File.ReadAllTextAsync(Path.Combine(folder,"a.jpg"),deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutInventoryPagesHaveStableIdsAndBoundedContinuationWithoutClearingPriorRows()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var entries=Enumerable.Range(0,601).Select(i=>("album/image-"+i.ToString(System.Globalization.CultureInfo.InvariantCulture)+".jpg","original"u8.ToArray())).ToArray();var zip=await ZipAsync(root.Path,"input.zip",entries,deadline.Token).ConfigureAwait(false);
        using var input=await OpenZipAsync(zip,deadline.Token).ConfigureAwait(false);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));var repository=new TakeoutImportInventoryRepository(database);var id=Guid.NewGuid();repository.Ensure(id,Target(),[input.Binding]);var work=repository.Claim(id)!;
        using var catalog=TakeoutAssociationCatalog.Open(database,work,budget);catalog.AddPart(input,deadline.Token);await catalog.SealAsync(deadline.Token).ConfigureAwait(false);
        var all=new List<TakeoutCapturedEntry>();string? after=null;bool more;
        do{var page=catalog.ReadPage(TakeoutEntryKind.Media,after,250,deadline.Token);Assert.IsTrue(page.Entries.Count<=250);all.AddRange(page.Entries);more=page.HasMore;after=page.Entries.LastOrDefault()?.Key;}while(more);
        Assert.AreEqual(601,all.Count);Assert.AreEqual(601,all.Select(e=>e.Key).Distinct().Count());Assert.AreEqual(601,all.Select(e=>e.MediaId).Distinct().Count());
        CollectionAssert.AreEqual(all.Take(250).ToArray(),catalog.ReadPage(TakeoutEntryKind.Media).Entries.ToArray());Assert.ThrowsExactly<ArgumentException>(()=>catalog.ReadPage(TakeoutEntryKind.Media,limit:251));
    }

    [TestMethod]
    public async Task TakeoutInventoryCommitRetainsActualSealedOwnersUntilNativeCompletionReturns()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"a.jpg"),"original",deadline.Token).ConfigureAwait(false);
        using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var budget=new StorageBudgetManager(new Space(database.StateDirectory));
        var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[input.Binding]);var work=repository.Claim(job)!;
        using var catalog=TakeoutAssociationCatalog.Open(database,work,budget);catalog.AddPart(input,deadline.Token);await catalog.SealAsync(deadline.Token).ConfigureAwait(false);
        var commit=catalog.AcquireVerifiedInventory(work);
        try
        {
            Assert.IsGreaterThan(0L,commit.Result.Bytes);
            Assert.ThrowsExactly<InvalidOperationException>(()=>catalog.Dispose());
            Assert.ThrowsExactly<InvalidOperationException>(()=>catalog.ReadPage(TakeoutEntryKind.Media));
            Assert.ThrowsExactly<IOException>(()=>{using var writer=new FileStream(IndexPath(database.StateDirectory,job),FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
            Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
        }
        finally {commit.Dispose();}
        commit.Dispose();Assert.ThrowsExactly<ObjectDisposedException>(()=>_ = commit.Result);
        repository.Complete(work,catalog);Assert.AreEqual(1,new DurableJobRepository(database).Summary(job).Ready);Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount());
        Assert.AreEqual(1,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Count);
        catalog.Dispose();Assert.ThrowsExactly<ObjectDisposedException>(()=>catalog.AcquireVerifiedInventory(work));
        Assert.AreEqual("original",await File.ReadAllTextAsync(Path.Combine(folder,"a.jpg"),deadline.Token).ConfigureAwait(false));
    }

    private static TakeoutImportTarget Target()=>new(Guid.NewGuid(),"account-one",1,"destination-one",1);
    private static string IndexPath(string root,Guid job)=>Path.Combine(root,"takeout-associations","import-"+job.ToString("N"),"inventory.sqlite");
    private static Guid ReadFirstAsset(string path)
    {using var connection=Native(path,true);using var command=connection.CreateCommand();command.CommandText="SELECT payload FROM entry WHERE kind=0 ORDER BY key LIMIT 1";return JsonSerializer.Deserialize<TakeoutCapturedEntry>((byte[])command.ExecuteScalar()!)!.MediaId;}
    private static SqliteConnection Native(string path,bool readOnly)
    {var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWrite,Pooling=false}.ToString());connection.Open();return connection;}
    private static async Task<string> ZipAsync(string root,string name,(string Name,byte[] Bytes)[] entries,CancellationToken token)
    {
        var path=Path.Combine(root,name);await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);using var archive=new ZipArchive(file,ZipArchiveMode.Create,leaveOpen:true);
        foreach(var item in entries){var entry=archive.CreateEntry(item.Name,CompressionLevel.NoCompression);await using var output=entry.Open();await output.WriteAsync(item.Bytes,token).ConfigureAwait(false);}return path;
    }
    private static async Task<TakeoutSelectedInput> OpenZipAsync(string path,CancellationToken token)=>await TakeoutSelectedInput.OpenZipAsync(Guid.NewGuid(),path,new FileInfo(path).Length,await DigestAsync(path,token).ConfigureAwait(false),token).ConfigureAwait(false);
    private static async Task<string> DigestAsync(string path,CancellationToken token){await using var input=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token).ConfigureAwait(false));}
    private static async Task<byte[]> ReadAllAsync(Stream source,CancellationToken token){using var output=new MemoryStream();await source.CopyToAsync(output,token).ConfigureAwait(false);return output.ToArray();}
    private static async Task<TakeoutWorkerInstallation> InstallationAsync(CancellationToken token)
    {var runtime=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet"));var worker=Path.Combine(AppContext.BaseDirectory,"ProductWorker","Mpa.Processor.WorkerHost.dll");return new(runtime,await DigestAsync(runtime,token).ConfigureAwait(false),worker,await DigestAsync(worker,token).ConfigureAwait(false));}
    private sealed class Space(string root) : IStorageSpaceReader
    {public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,40*StorageBudgetManager.Gibibyte,checked(TakeoutMetadataStageStorage.MeasureOwnedBytes(root)+TakeoutAssociationCatalog.MeasureOwnedBytes(root)));}
}
