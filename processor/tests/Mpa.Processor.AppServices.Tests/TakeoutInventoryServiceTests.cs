using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class TakeoutInventoryServiceTests
{
    [TestMethod]
    public async Task DesktopInventoryOwnerRunsActualMultipartSelectionAndReopensItsFirstReadyReceipt()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await Fixture.CreateAsync(false,deadline.Token).ConfigureAwait(false);
        await using var owner=new TakeoutInventoryService(fixture.State,fixture.Budget);
        var completed=await owner.RunAsync(fixture.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(ProcessorWorkState.Ready,owner.State);
        using(var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,fixture.Budget,deadline.Token).ConfigureAwait(false))
        {
            var sidecar=catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single();var match=catalog.Associate(sidecar.Key,"a.jpg",deadline.Token);
            Assert.AreEqual("matched",match.Status);Assert.AreEqual("album/a.jpg",match.Media!.RelativePath);
        }
        var first=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts().Single(),deadline.Token).ConfigureAwait(false);
        var reopened=await owner.RunAsync(fixture.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(completed.ImportId,reopened.ImportId);Assert.AreEqual(completed.Target,reopened.Target);
        var again=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts().Single(),deadline.Token).ConfigureAwait(false);Assert.AreEqual(first,again);
        await owner.PauseAsync().ConfigureAwait(false);Assert.AreEqual(ProcessorWorkState.Paused,owner.State);await owner.ResumeAsync().ConfigureAwait(false);Assert.AreEqual(ProcessorWorkState.Ready,owner.State);
        Assert.IsNull(owner.Failure);Assert.AreEqual("original",await File.ReadAllTextAsync(fixture.Original,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DesktopInventoryOwnerRejectsOriginalCancellationBeforeItsLinkedCallbackCanRun()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await Fixture.CreateAsync(true,deadline.Token).ConfigureAwait(false);
        await using var owner=new TakeoutInventoryService(fixture.State,fixture.Budget);using var caller=new CancellationTokenSource();var running=owner.RunAsync(fixture.Job,caller.Token);
        using var releaseCallback=new ManualResetEventSlim();var callbackEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Task? cancel=null;
        try
        {
            await fixture.Space.Entered.WaitAsync(deadline.Token).ConfigureAwait(false);
            using var registration=caller.Token.Register(()=>{callbackEntered.TrySetResult();if(!releaseCallback.Wait(TimeSpan.FromSeconds(20)))throw new TimeoutException("The actual cancellation callback was not released.");});
            try
            {
                cancel=Task.Run(()=>caller.Cancel(),CancellationToken.None);await callbackEntered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.IsTrue(caller.IsCancellationRequested);Assert.IsFalse(cancel.IsCompleted);
                fixture.Space.Release();await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);
                Assert.IsFalse(cancel.IsCompleted);Assert.AreEqual(ProcessorWorkState.Ready,owner.State);Assert.IsNull(owner.Failure);
                await fixture.AssertPendingAsync(deadline.Token).ConfigureAwait(false);
            }
            finally {releaseCallback.Set();if(cancel is not null)await cancel.ConfigureAwait(false);}
        }
        finally {fixture.Space.Release();releaseCallback.Set();}
        var retry=await owner.RunAsync(fixture.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(fixture.Job,retry.ImportId);
        Assert.AreEqual("original",await File.ReadAllTextAsync(fixture.Original,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DesktopInventoryPauseAndExitJoinActualIoBeforeAcknowledgingOrRetrying(bool exit)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await Fixture.CreateAsync(true,deadline.Token).ConfigureAwait(false);
        await using var owner=new TakeoutInventoryService(fixture.State,fixture.Budget);var running=owner.RunAsync(fixture.Job,deadline.Token);Task? closing=null;
        try
        {
            await fixture.Space.Entered.WaitAsync(deadline.Token).ConfigureAwait(false);closing=exit?owner.DisposeAsync().AsTask():owner.PauseAsync();
            Assert.AreEqual(exit?ProcessorWorkState.Stopping:ProcessorWorkState.Pausing,owner.State);Assert.IsFalse(closing.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);
            if(exit)Assert.ThrowsExactly<ObjectDisposedException>(()=>owner.RunAsync(fixture.Job,deadline.Token));
            else Assert.ThrowsExactly<ProcessorWorkStateConflictException>(()=>owner.RunAsync(fixture.Job,deadline.Token));
            fixture.Space.Release();await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await closing.ConfigureAwait(false);
            await fixture.AssertPendingAsync(deadline.Token).ConfigureAwait(false);Assert.IsNull(owner.Failure);
            if(exit)
            {
                Assert.IsTrue(owner.ExitConfirmed);Assert.AreEqual(ProcessorWorkState.Stopped,owner.State);
                await using var replacement=new TakeoutInventoryService(fixture.State,fixture.Budget);var result=await replacement.RunAsync(fixture.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(fixture.Job,result.ImportId);
            }
            else
            {
                Assert.AreEqual(ProcessorWorkState.Paused,owner.State);await owner.ResumeAsync().ConfigureAwait(false);var result=await owner.RunAsync(fixture.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(fixture.Job,result.ImportId);
            }
        }
        finally
        {
            fixture.Space.Release();try{await running.ConfigureAwait(false);}catch(OperationCanceledException){ }
            if(closing is not null)await closing.ConfigureAwait(false);
        }
        Assert.AreEqual("original",await File.ReadAllTextAsync(fixture.Original,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RealDesktopWorkGroupJoinsNativeAndImportOwnersBeforePauseAndResumesTheCapturedImport()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await Fixture.CreateAsync(true,deadline.Token).ConfigureAwait(false);
        await using var inventory=new TakeoutInventoryService(fixture.State,fixture.Budget);await using var native=new ProcessorWorkSupervisor();await using var group=new ProcessorWorkGroup(native,inventory);
        var running=inventory.RunAsync(fixture.Job,deadline.Token);Task? pause=null;
        try
        {
            await fixture.Space.Entered.WaitAsync(deadline.Token).ConfigureAwait(false);pause=group.PauseAsync();Assert.AreEqual(ProcessorWorkState.Pausing,group.State);Assert.IsFalse(pause.IsCompleted);
            fixture.Space.Release();await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await pause.ConfigureAwait(false);
            Assert.AreEqual(ProcessorWorkState.Paused,group.State);Assert.AreEqual(ProcessorWorkState.Paused,native.State);Assert.AreEqual(ProcessorWorkState.Paused,inventory.State);
            await fixture.AssertPendingAsync(deadline.Token).ConfigureAwait(false);await group.ResumeAsync().ConfigureAwait(false);
            Assert.AreEqual(ProcessorWorkState.Ready,native.State);Assert.AreEqual(ProcessorWorkState.Ready,inventory.State);var result=await inventory.RunAsync(fixture.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(fixture.Job,result.ImportId);
            Assert.IsNull(group.Failure);Assert.AreEqual("original",await File.ReadAllTextAsync(fixture.Original,deadline.Token).ConfigureAwait(false));
        }
        finally {fixture.Space.Release();try{await running.ConfigureAwait(false);}catch(OperationCanceledException){ }if(pause is not null)await pause.ConfigureAwait(false);}
    }

    [TestMethod]
    public async Task ChangedCapturedZipIsAVisibleOwnerFailureAndCannotBecomeReadyOrReplacePendingSelection()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await Fixture.CreateAsync(false,deadline.Token).ConfigureAwait(false);
        await using var owner=new TakeoutInventoryService(fixture.State,fixture.Budget);var bytes=await File.ReadAllBytesAsync(fixture.ZipPath,deadline.Token).ConfigureAwait(false);bytes[^1]^=1;await File.WriteAllBytesAsync(fixture.ZipPath,bytes,deadline.Token).ConfigureAwait(false);
        Exception? notified=null;owner.Failed+=issue=>notified=issue;
        var error=await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owner.RunAsync(fixture.Job,deadline.Token)).ConfigureAwait(false);Assert.AreSame(error,notified);
        Assert.AreSame(error,owner.Failure);Assert.AreEqual(ProcessorWorkState.Faulted,owner.State);Assert.ThrowsExactly<ProcessorWorkStateConflictException>(()=>owner.RunAsync(fixture.Job,deadline.Token));
        await fixture.AssertPendingAsync(deadline.Token).ConfigureAwait(false);await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);Assert.AreSame(error,owner.Failure);
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(fixture.ZipPath,deadline.Token).ConfigureAwait(false));Assert.AreEqual("original",await File.ReadAllTextAsync(fixture.Original,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DesktopStartupRecoversOnlyItsInterruptedInventoryAndKeepsAdmissionPausedUntilActivation()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));await using var fixture=await Fixture.CreateAsync(false,deadline.Token).ConfigureAwait(false);
        var old=await fixture.State.ExecuteAsync(db=>new TakeoutImportInventoryRepository(db).Claim(fixture.Job)!,deadline.Token).ConfigureAwait(false);
        await using var owner=await TakeoutInventoryService.CreatePausedAfterExclusiveRestartAsync(fixture.State,fixture.Budget,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Paused,owner.State);Assert.ThrowsExactly<ProcessorWorkStateConflictException>(()=>owner.RunAsync(fixture.Job,deadline.Token));await fixture.AssertPendingAsync(deadline.Token).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>{Assert.ThrowsExactly<StaleStageLeaseException>(()=>new TakeoutImportInventoryRepository(db).AssertCurrent(old));return true;},deadline.Token).ConfigureAwait(false);
        await owner.ResumeAsync().ConfigureAwait(false);var completed=await owner.RunAsync(fixture.Job,deadline.Token).ConfigureAwait(false);Assert.AreEqual(fixture.Job,completed.ImportId);Assert.IsNull(owner.Failure);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root=Path.Combine(Path.GetTempPath(),"mpa-owned-inventory-"+Guid.NewGuid().ToString("N"));
        private readonly ProcessorStateDatabase _database;
        internal ProcessorStateCoordinator State {get;}
        internal BlockingSpace Space {get;}
        internal StorageBudgetManager Budget {get;}
        internal Guid Job {get;}=Guid.NewGuid();
        internal string Original=>Path.Combine(_root,"folder","album","a.jpg");
        internal string ZipPath=>Path.Combine(_root,"sidecar.zip");
        private Fixture(bool block)
        {_database=new ProcessorStateDatabase(Path.Combine(_root,"state"));State=new(_database);Space=new(new ProcessorStateStorageReader(_database.StateDirectory),block);Budget=new(Space);}
        internal static async Task<Fixture> CreateAsync(bool block,CancellationToken token)
        {
            var fixture=new Fixture(block);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.Original)!);await File.WriteAllTextAsync(fixture.Original,"original",token).ConfigureAwait(false);
                var zip=fixture.ZipPath;
                await using(var file=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {using var archive=new ZipArchive(file,ZipArchiveMode.Create,leaveOpen:true);await using var sidecar=archive.CreateEntry("album/a.jpg.json").Open();await sidecar.WriteAsync("{\"title\":\"a.jpg\"}"u8.ToArray(),token).ConfigureAwait(false);}
                string hash;await using(var file=File.OpenRead(zip))hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(file,token).ConfigureAwait(false));
                using var folder=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),Path.Combine(fixture._root,"folder"));using var second=await TakeoutSelectedInput.OpenZipAsync(Guid.NewGuid(),zip,new FileInfo(zip).Length,hash,token).ConfigureAwait(false);
                await fixture.State.ExecuteAsync(db=>new TakeoutImportInventoryRepository(db).Ensure(fixture.Job,new(Guid.NewGuid(),"account-one",1,"destination-one",1),[folder.Binding,second.Binding]),token).ConfigureAwait(false);return fixture;
            }
            catch {await fixture.DisposeAsync().ConfigureAwait(false);throw;}
        }
        internal Task<bool> AssertPendingAsync(CancellationToken token)=>State.ExecuteAsync(db=>
        {var jobs=new DurableJobRepository(db);Assert.AreEqual(0,jobs.PendingReceiptCount());Assert.AreEqual(1,jobs.Summary(Job).Queued);Assert.AreEqual(0,jobs.Summary(Job).Running);Assert.IsNull(new TakeoutImportInventoryRepository(db).ReadCompleted(Job));return true;},token);
        public async ValueTask DisposeAsync()
        {Space.Release();await State.DisposeAsync().ConfigureAwait(false);_database.Dispose();Space.Dispose();if(Directory.Exists(_root))Directory.Delete(_root,recursive:true);}
    }
    private sealed class BlockingSpace(IStorageSpaceReader actual,bool block) : IStorageSpaceReader,IDisposable
    {
        private readonly ManualResetEventSlim _release=new();private readonly TaskCompletionSource _entered=new(TaskCreationOptions.RunContinuationsAsynchronously);private int _reads;
        internal Task Entered=>_entered.Task;
        public StorageSpaceSnapshot Read()
        {var result=actual.Read();if(block&&Interlocked.Increment(ref _reads)==1){_entered.TrySetResult();if(!_release.Wait(TimeSpan.FromSeconds(20)))throw new TimeoutException("Release the actual storage reader before joining inventory IO.");}return result;}
        internal void Release()=>_release.Set();
        public void Dispose()=>_release.Dispose();
    }
}
