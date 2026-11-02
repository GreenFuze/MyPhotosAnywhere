using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed partial class TakeoutMetadataOperationTests
{
    private const string Valid="{\"title\":\"שלום.jpg\",\"description\":\"preserve human source\",\"photoTakenTime\":{\"timestamp\":\"-1\"},\"geoData\":{\"latitude\":31.7,\"longitude\":35.2},\"unknown\":{\"keep\":true}}";
    private static readonly string MediaHash=new('c',64);

    [TestMethod]
    public async Task TakeoutOwnedOperationCommitsVerifiedReceiptOnlyAfterActualChildAndIoJoin()
        =>await RunVerifiedMetadataAsync(appHost:false).ConfigureAwait(false);

    [TestMethod]
    public async Task TakeoutAppHostRunsTheSameInstalledAnalysisWithoutPassingSdkHostArguments()
        =>await RunVerifiedMetadataAsync(appHost:true).ConfigureAwait(false);

    [TestMethod]
    public async Task TakeoutInstalledDescriptorRunsThePinnedFirstPartyBundleAndJoinsItsActualChild()
        =>await RunVerifiedMetadataAsync(appHost:true,installedDescriptor:true).ConfigureAwait(false);

    private static async Task RunVerifiedMetadataAsync(bool appHost,bool installedDescriptor=false)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        await using var state=new ProcessorStateCoordinator(database);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var budget=new StorageBudgetManager(new MeasuredSpace(root.Path));using var storage=new TakeoutMetadataStageStorage(database,budget);
        var work=await AdmitAsync(state,Encoding.UTF8.GetBytes(Valid),deadline.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(Encoding.UTF8.GetBytes(Valid),writable:false))await storage.CaptureAsync(work,source,deadline.Token).ConfigureAwait(false);
        var installation=installedDescriptor?await TakeoutWorkerInstallation.OpenInstalledAsync(await CopyInstalledBundleAsync(root.Path,deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false):
            appHost?await AppHostInstallationAsync(deadline.Token).ConfigureAwait(false):await InstallationAsync(deadline.Token).ConfigureAwait(false);
        var operation=await TakeoutMetadataWorkerOperation.CreateAsync(state,work,storage,installation,deadline.Token).ConfigureAwait(false);
        await using var supervisor=new ProcessorWorkSupervisor();await supervisor.Start(operation,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Ready,supervisor.State);Assert.IsNull(supervisor.Failure);Assert.AreEqual(0L,budget.Status().ReservedBytes);
        var receipt=await state.ExecuteAsync(db=>
        {
            var jobs=new DurableJobRepository(db);Assert.AreEqual(1,jobs.Summary(work.Lease.JobId).Ready);Assert.IsNull(new TakeoutMetadataJobRepository(db).ClaimNext());
            return jobs.ReadPendingReceipts().Single();
        },deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(work.Lease.LeaseId,receipt.Id);using var parsed=JsonDocument.Parse(receipt.Receipt);
        Assert.AreEqual(work.MediaId.ToString("D"),parsed.RootElement.GetProperty("mediaId").GetString());
        Assert.AreEqual(MediaHash,parsed.RootElement.GetProperty("mediaInputSha256").GetString());
        Assert.AreEqual(TakeoutMetadataDocument.RecipeSha256,parsed.RootElement.GetProperty("recipeSha256").GetString());
        var result=ResultPath(root.Path,work);var bytes=await File.ReadAllBytesAsync(result,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)),parsed.RootElement.GetProperty("artifact").GetProperty("sha256").GetString());
        var metadata=TakeoutMetadataDocument.ReadVerifiedArtifact(bytes,work.Sidecar);Assert.AreEqual(-1000L,metadata.CapturedUtc);Assert.AreEqual(31.7,metadata.Latitude);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Valid),metadata.CopyRawSource());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Valid),await File.ReadAllBytesAsync(SourcePath(root.Path,work),deadline.Token).ConfigureAwait(false));
        Assert.AreEqual(0L,new FileInfo(operation.Launch.NewDiagnosticPath).Length);
        using var released=File.Open(result,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }

    [TestMethod]
    [DataRow("host")]
    [DataRow("assembly")]
    [DataRow("foreign-name")]
    [DataRow("missing-manifest")]
    public async Task TakeoutInstalledBundleRejectsChangedOrMissingFilesWithoutSdkFallback(string change)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bundle=await CopyInstalledBundleAsync(root.Path,deadline.Token).ConfigureAwait(false);var manifest=Path.Combine(bundle,"worker.json");
        if(change=="missing-manifest")
        {File.Delete(manifest);await Assert.ThrowsExactlyAsync<FileNotFoundException>(()=>TakeoutWorkerInstallation.OpenInstalledAsync(bundle,deadline.Token)).ConfigureAwait(false);return;}
        if(change=="foreign-name")
        {
            var text=await File.ReadAllTextAsync(manifest,deadline.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(manifest,text.Replace("Mpa.Processor.WorkerHost.dll","substituted.dll",StringComparison.Ordinal),deadline.Token).ConfigureAwait(false);
        }
        else
        {
            var path=Path.Combine(bundle,change=="host"?HostName():"Mpa.Processor.WorkerHost.dll");
            await using var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);var value=file.ReadByte();file.Position=0;file.WriteByte((byte)(value^255));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutWorkerInstallation.OpenInstalledAsync(bundle,deadline.Token)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TakeoutMalformedSidecarFailsOnlyItsMetadataAndDoesNotRequeueSuccessfulMediaUpload()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        await using var state=new ProcessorStateCoordinator(database);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var upload=await state.ExecuteAsync(db=>
        {
            var jobs=new DurableJobRepository(db);var job=Guid.NewGuid();var hash=new string('a',64);
            jobs.CreateWithInitialItem(job,"original-upload","{}"u8,new(Guid.NewGuid(),hash,[new("upload",hash,[])]));var lease=jobs.ClaimNext("original-upload")!;
            jobs.Complete(lease,"{\"verifiedOriginal\":true}"u8);return job;
        },deadline.Token).ConfigureAwait(false);
        var budget=new StorageBudgetManager(new MeasuredSpace(root.Path));using var storage=new TakeoutMetadataStageStorage(database,budget);
        await using var supervisor=new ProcessorWorkSupervisor();var raw="{\"title\":\"a.jpg\",\"photoTakenTime\":{\"timestamp\":\"bad\"}}"u8.ToArray();
        var bad=await AdmitAsync(state,raw,deadline.Token).ConfigureAwait(false);using(var source=new MemoryStream(raw,false))await storage.CaptureAsync(bad,source,deadline.Token).ConfigureAwait(false);
        var installation=await InstallationAsync(deadline.Token).ConfigureAwait(false);
        await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(state,bad,storage,installation,deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false);
        var good=await AdmitAsync(state,Encoding.UTF8.GetBytes(Valid),deadline.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(Encoding.UTF8.GetBytes(Valid),false))await storage.CaptureAsync(good,source,deadline.Token).ConfigureAwait(false);
        await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(state,good,storage,installation,deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false);
        await state.ExecuteAsync(db=>
        {
            var jobs=new DurableJobRepository(db);Assert.AreEqual(1,jobs.Summary(bad.Lease.JobId).Unsupported);Assert.AreEqual(0,jobs.RetryFailed(bad.Lease.JobId));
            Assert.AreEqual(1,jobs.Summary(good.Lease.JobId).Ready);Assert.AreEqual(1,jobs.Summary(upload).Ready);Assert.AreEqual(0,jobs.Summary(upload).Queued);
            Assert.AreEqual(2,jobs.PendingReceiptCount());return true;
        },deadline.Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(raw,await File.ReadAllBytesAsync(SourcePath(root.Path,bad),deadline.Token).ConfigureAwait(false));Assert.IsFalse(File.Exists(ResultPath(root.Path,bad)));Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task TakeoutDurablePauseFencesAnActualCompletedChildAndRetryKeepsOriginalSnapshot()
    {
        await FencedCompletionAsync(cancelToken:false).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TakeoutCallerCancellationFencesAnActualCompletedChildBeforeNativeReceiptAdmission()
    {
        await FencedCompletionAsync(cancelToken:true).ConfigureAwait(false);
    }

    private static async Task FencedCompletionAsync(bool cancelToken)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        await using var state=new ProcessorStateCoordinator(database);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));using var canceled=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var budget=new StorageBudgetManager(new MeasuredSpace(root.Path));using var storage=new TakeoutMetadataStageStorage(database,budget);
        var work=await AdmitAsync(state,Encoding.UTF8.GetBytes(Valid),deadline.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(Encoding.UTF8.GetBytes(Valid),false))await storage.CaptureAsync(work,source,deadline.Token).ConfigureAwait(false);
        var installation=await InstallationAsync(deadline.Token).ConfigureAwait(false);var operation=await TakeoutMetadataWorkerOperation.CreateAsync(state,work,storage,installation,deadline.Token).ConfigureAwait(false);
        await using var actual=new ActualAttempt(operation);await actual.StartAsync(deadline.Token).ConfigureAwait(false);
        var run=operation.ExecuteAsync(actual.Worker,canceled.Token);
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // ExecuteAsync has already admitted its initial serial native check. This
        // next real SQLite delegate fences/blocks the final receipt transaction.
        var fence=state.ExecuteAsync(db=>
        {
            if(!cancelToken)Assert.AreEqual(work.Lease.LeaseId,new DurableJobRepository(db).Pause(work.Lease.JobId).Single().LeaseId);
            entered.SetResult();if(!release.Wait(TimeSpan.FromSeconds(30)))throw new TimeoutException("Metadata test did not release its actual serial state owner.");return true;
        },deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await actual.Worker.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.IsTrue(File.Exists(ResultPath(root.Path,work)));Assert.IsFalse(run.IsCompleted);
            Assert.IsFalse(actual.Worker.RetirementConfirmed);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async()=>await operation.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            Assert.ThrowsExactly<IOException>(()=>{using var stream=File.Open(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ResultPath(root.Path,work))!,"input.bin"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
            Assert.ThrowsExactly<IOException>(()=>{using var stream=File.Open(ResultPath(root.Path,work),FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
            Assert.ThrowsExactly<IOException>(()=>{using var stream=File.Open(SourcePath(root.Path,work),FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
            Assert.ThrowsExactly<InvalidOperationException>(storage.Dispose);
            if(cancelToken)await canceled.CancelAsync().ConfigureAwait(false);
        }
        finally {release.Set();await fence.ConfigureAwait(false);}
        if(cancelToken)await Assert.ThrowsAsync<OperationCanceledException>(()=>run).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<StaleStageLeaseException>(()=>run).ConfigureAwait(false);
        await actual.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(actual.Worker.RetirementConfirmed);Assert.AreEqual(0L,budget.Status().ReservedBytes);
        var retry=await state.ExecuteAsync(db=>
        {
            var jobs=new DurableJobRepository(db);Assert.AreEqual(0,jobs.PendingReceiptCount());Assert.AreEqual(0,jobs.Summary(work.Lease.JobId).Ready);Assert.AreEqual(1,jobs.Summary(work.Lease.JobId).Queued);
            if(!cancelToken){Assert.IsTrue(jobs.Summary(work.Lease.JobId).Paused);Assert.IsNull(new TakeoutMetadataJobRepository(db).ClaimNext());jobs.Resume(work.Lease.JobId);}
            return new TakeoutMetadataJobRepository(db).ClaimNext()!;
        },deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(work.Lease.CapturedItemContext,retry.Lease.CapturedItemContext);Assert.AreEqual(2,retry.Lease.Attempt);Assert.AreNotEqual(work.Lease.LeaseId,retry.Lease.LeaseId);
        await using var supervisor=new ProcessorWorkSupervisor();
        // No source provider or original is reopened for this exact captured retry.
        await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(state,retry,storage,installation,deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false);
        var receipt=await state.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts().Single(),deadline.Token).ConfigureAwait(false);Assert.AreEqual(retry.Lease.LeaseId,receipt.Id);
        Assert.IsTrue(File.Exists(ResultPath(root.Path,work)));Assert.IsTrue(File.Exists(ResultPath(root.Path,retry)));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Valid),await File.ReadAllBytesAsync(SourcePath(root.Path,work),deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TakeoutCaptureRetainsPartialBytesAndResumesOnlyTheOriginalVerifiedPrefix()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        await using var state=new ProcessorStateCoordinator(database);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var raw=JsonSerializer.SerializeToUtf8Bytes(new {title="a.jpg",description=new string('x',32*1024),creationTime=new {timestamp="0"}});
        var work=await AdmitAsync(state,raw,deadline.Token).ConfigureAwait(false);var budget=new StorageBudgetManager(new MeasuredSpace(root.Path));using var storage=new TakeoutMetadataStageStorage(database,budget);
        using(var interrupted=new PrefixFailureStream(raw,8192))await Assert.ThrowsExactlyAsync<IOException>(()=>storage.CaptureAsync(work,interrupted,deadline.Token)).ConfigureAwait(false);
        var partial=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SourcePath(root.Path,work))!,"source.pending");
        var retained=await File.ReadAllBytesAsync(partial,deadline.Token).ConfigureAwait(false);CollectionAssert.AreEqual(raw[..8192],retained);
        var changed=raw.ToArray();changed[100]^=1;
        using(var replacement=new MemoryStream(changed,false))await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>storage.CaptureAsync(work,replacement,deadline.Token)).ConfigureAwait(false);
        CollectionAssert.AreEqual(retained,await File.ReadAllBytesAsync(partial,deadline.Token).ConfigureAwait(false));
        using(var original=new MemoryStream(raw,false))await storage.CaptureAsync(work,original,deadline.Token).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(partial));CollectionAssert.AreEqual(raw,await File.ReadAllBytesAsync(SourcePath(root.Path,work),deadline.Token).ConfigureAwait(false));
        await using var supervisor=new ProcessorWorkSupervisor();await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(state,work,storage,await InstallationAsync(deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task TakeoutStagingCountsRetainedBytesAndRefusesUnownedInventoryAndForeignInstallation()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        await using var state=new ProcessorStateCoordinator(database);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var budget=new StorageBudgetManager(new MeasuredSpace(root.Path));
        var storage=new TakeoutMetadataStageStorage(database,budget);
        using(storage)
        {
            using var foreignRoot=new WorkerProcessTests.WorkerTestDirectory();using var foreign=new ProcessorStateDatabase(foreignRoot.Path);
            var foreignJobs=new TakeoutMetadataJobRepository(foreign);var raw=Encoding.UTF8.GetBytes(Valid);
            foreignJobs.Ensure(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),MediaHash,new(raw.Length,Convert.ToHexStringLower(SHA256.HashData(raw)),"input.bin"));
            using(var source=new MemoryStream(raw,false))await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>storage.CaptureAsync(foreignJobs.ClaimNext()!,source,deadline.Token)).ConfigureAwait(false);
            var before=TakeoutMetadataStageStorage.MeasureOwnedBytes(root.Path);var work=await AdmitAsync(state,Encoding.UTF8.GetBytes(Valid),deadline.Token).ConfigureAwait(false);
            using(var source=new MemoryStream(Encoding.UTF8.GetBytes(Valid),false))await storage.CaptureAsync(work,source,deadline.Token).ConfigureAwait(false);
            Assert.IsGreaterThan(before+Encoding.UTF8.GetByteCount(Valid),TakeoutMetadataStageStorage.MeasureOwnedBytes(root.Path));
            Assert.ThrowsExactly<IOException>(()=>new TakeoutMetadataStageStorage(database,budget));
        }
        using(var recovered=new TakeoutMetadataStageStorage(database,budget))Assert.IsGreaterThan(0L,TakeoutMetadataStageStorage.MeasureOwnedBytes(root.Path));
        var unknown=System.IO.Path.Combine(root.Path,"takeout-metadata-stages","unknown.bin");await File.WriteAllBytesAsync(unknown,new byte[]{42},deadline.Token).ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidDataException>(()=>TakeoutMetadataStageStorage.MeasureOwnedBytes(root.Path));
        Assert.ThrowsExactly<InvalidDataException>(()=>new TakeoutMetadataStageStorage(database,budget));Assert.AreEqual(42,File.ReadAllBytes(unknown).Single());
    }

    [TestMethod]
    public async Task TakeoutLowSpaceAndChangedInstallationCannotLaunchOrForgetCapturedInput()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        await using var state=new ProcessorStateCoordinator(database);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reader=new MeasuredSpace(root.Path);var budget=new StorageBudgetManager(reader);using var storage=new TakeoutMetadataStageStorage(database,budget);
        var work=await AdmitAsync(state,Encoding.UTF8.GetBytes(Valid),deadline.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(Encoding.UTF8.GetBytes(Valid),false))await storage.CaptureAsync(work,source,deadline.Token).ConfigureAwait(false);
        var installation=await InstallationAsync(deadline.Token).ConfigureAwait(false);reader.AvailableBytes=2*StorageBudgetManager.Gibibyte;
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>TakeoutMetadataWorkerOperation.CreateAsync(state,work,storage,installation,deadline.Token)).ConfigureAwait(false);
        Assert.AreEqual(0,Directory.GetDirectories(System.IO.Path.GetDirectoryName(SourcePath(root.Path,work))!).Length);
        reader.AvailableBytes=40*StorageBudgetManager.Gibibyte;
        var runtime=RuntimePath();var bad=new TakeoutWorkerInstallation(runtime,await DigestAsync(runtime,deadline.Token).ConfigureAwait(false),WorkerPath(),new string('a',64));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutMetadataWorkerOperation.CreateAsync(state,work,storage,bad,deadline.Token)).ConfigureAwait(false);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Valid),await File.ReadAllBytesAsync(SourcePath(root.Path,work),deadline.Token).ConfigureAwait(false));
        await state.ExecuteAsync(db=>{new TakeoutMetadataJobRepository(db).AssertCurrent(work);Assert.AreEqual(0,new DurableJobRepository(db).PendingReceiptCount());return true;},deadline.Token).ConfigureAwait(false);
        await using var supervisor=new ProcessorWorkSupervisor();await supervisor.Start(await TakeoutMetadataWorkerOperation.CreateAsync(state,work,storage,installation,deadline.Token).ConfigureAwait(false),deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(0L,budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task TakeoutAttemptRejectsSubstitutedOutputIdentityAndRetainsItsExactInputLease()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var database=new ProcessorStateDatabase(root.Path);
        await using var state=new ProcessorStateCoordinator(database);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var budget=new StorageBudgetManager(new MeasuredSpace(root.Path));using var storage=new TakeoutMetadataStageStorage(database,budget);
        var work=await AdmitAsync(state,Encoding.UTF8.GetBytes(Valid),deadline.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(Encoding.UTF8.GetBytes(Valid),false))await storage.CaptureAsync(work,source,deadline.Token).ConfigureAwait(false);
        using(var attempt=await storage.OpenAttemptAsync(work,deadline.Token).ConfigureAwait(false))
        {
            Assert.ThrowsExactly<ProtocolFormatException>(()=>attempt.OpenResult(new(1,new string('a',64),"facts-"+Guid.NewGuid().ToString("N")+".json")));
            Assert.ThrowsExactly<ProtocolFormatException>(()=>attempt.OpenResult(new(TakeoutMetadataDocument.MaximumArtifactBytes+1,new string('a',64),"facts-"+work.Lease.LeaseId.ToString("N")+".json")));
            Assert.ThrowsExactly<IOException>(()=>{using var file=File.Open(System.IO.Path.Combine(attempt.Path,"input.bin"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
            Assert.ThrowsExactly<InvalidOperationException>(storage.Dispose);
        }
        Assert.AreEqual(0L,budget.Status().ReservedBytes);Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
    }

    private static Task<TakeoutMetadataWork> AdmitAsync(ProcessorStateCoordinator state,byte[] raw,CancellationToken token)=>state.ExecuteAsync(db=>
    {
        var repository=new TakeoutMetadataJobRepository(db);repository.Ensure(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),MediaHash,new(raw.Length,Convert.ToHexStringLower(SHA256.HashData(raw)),"input.bin"));return repository.ClaimNext()!;
    },token);
    private static string SourcePath(string root,TakeoutMetadataWork work)=>System.IO.Path.Combine(root,"takeout-metadata-stages","item-"+work.Lease.JobId.ToString("N")+"-"+work.Lease.ItemId.ToString("N"),"source.bin");
    private static string ResultPath(string root,TakeoutMetadataWork work)=>System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SourcePath(root,work))!,"attempt-"+work.Lease.LeaseId.ToString("N"),"facts-"+work.Lease.LeaseId.ToString("N")+".json");
    private static string RuntimePath()=>System.IO.Path.GetFullPath(System.IO.Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet"));
    private static string WorkerPath()=>System.IO.Path.Combine(AppContext.BaseDirectory,"ProductWorker","Mpa.Processor.WorkerHost.dll");
    private static async Task<string> DigestAsync(string path,CancellationToken token)
    {await using var file=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(file,token).ConfigureAwait(false));}
    private static async Task<TakeoutWorkerInstallation> InstallationAsync(CancellationToken token)=>new(RuntimePath(),await DigestAsync(RuntimePath(),token).ConfigureAwait(false),WorkerPath(),await DigestAsync(WorkerPath(),token).ConfigureAwait(false));
    private static async Task<TakeoutWorkerInstallation> AppHostInstallationAsync(CancellationToken token)
    {
        var host=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(WorkerPath())!,OperatingSystem.IsWindows()?"Mpa.Processor.WorkerHost.exe":"Mpa.Processor.WorkerHost");
        return TakeoutWorkerInstallation.ForAppHost(host,await DigestAsync(host,token).ConfigureAwait(false),WorkerPath(),await DigestAsync(WorkerPath(),token).ConfigureAwait(false));
    }
    private static string HostName()=>OperatingSystem.IsWindows()?"Mpa.Processor.WorkerHost.exe":"Mpa.Processor.WorkerHost";
    private static async Task<string> CopyInstalledBundleAsync(string root,CancellationToken token)
    {
        var bundle=Path.Combine(root,"installed-takeout-worker");Directory.CreateDirectory(bundle);
        foreach(var source in Directory.EnumerateFiles(Path.GetDirectoryName(WorkerPath())!))
            File.Copy(source,Path.Combine(bundle,Path.GetFileName(source)),overwrite:false);
        await File.WriteAllBytesAsync(Path.Combine(bundle,"worker.json"),JsonSerializer.SerializeToUtf8Bytes(new
        {format="mpa-installed-takeout-worker-1",host=HostName(),hostSha256=await DigestAsync(Path.Combine(bundle,HostName()),token).ConfigureAwait(false),
            assembly="Mpa.Processor.WorkerHost.dll",assemblySha256=await DigestAsync(Path.Combine(bundle,"Mpa.Processor.WorkerHost.dll"),token).ConfigureAwait(false)}),token).ConfigureAwait(false);
        return bundle;
    }
    private sealed class MeasuredSpace(string root) : IStorageSpaceReader
    {
        internal long AvailableBytes { get; set; }=40*StorageBudgetManager.Gibibyte;
        public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,AvailableBytes,TakeoutMetadataStageStorage.MeasureOwnedBytes(root));
    }
    private sealed class PrefixFailureStream(byte[] bytes,int prefix) : MemoryStream(bytes,writable:false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {if(Position>=prefix)throw new IOException("Synthetic selected-sidecar read interruption.");return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length,prefix-Position)],cancellationToken);}
    }
    private sealed class ActualAttempt(TakeoutMetadataWorkerOperation operation) : IAsyncDisposable
    {
        private FramedWorkerProcess? _worker;
        private bool _closed;
        internal FramedWorkerProcess Worker=>_worker??throw new InvalidOperationException("No actual metadata child exists.");
        internal async Task StartAsync(CancellationToken token)
        {try {_worker=await FramedWorkerProcess.StartAsync(operation.Launch,token).ConfigureAwait(false);}catch(WorkerStartCleanupException error){_worker=error.RetainedWorker;throw;}}
        public async ValueTask DisposeAsync()
        {
            if(_closed)return;var failures=new List<Exception>();
            if(_worker is not null)
            {
                try {await _worker.DisposeAsync().ConfigureAwait(false);}
                catch(Exception error){failures.Add(error);try {await _worker.JoinAfterFailedStopAsync().ConfigureAwait(false);}catch(Exception cleanup){failures.Add(cleanup);}}
                if(!_worker.RetirementConfirmed)throw new AggregateException("Actual metadata child still owns retained resources.",failures);
            }
            try {await operation.DisposeAsync().ConfigureAwait(false);_closed=true;}catch(Exception error){failures.Add(error);}
            if(failures.Count==1)ExceptionDispatchInfo.Capture(failures[0]).Throw();if(failures.Count>1)throw new AggregateException("Metadata test retirement failures were retained.",failures);
        }
    }
}
