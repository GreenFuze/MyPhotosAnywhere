using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutInventoryRecoveryTests
{
    [TestMethod]
    public async Task ActualTerminatedInventoryProcessReleasesNativeOwnersAndRetainsItsFirstCapturedRowsForRestart()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var state=Path.Combine(root.Path,"state");var one=Path.Combine(root.Path,"one");var two=Path.Combine(root.Path,"two");Directory.CreateDirectory(one);Directory.CreateDirectory(two);
        await File.WriteAllTextAsync(Path.Combine(one,"a.jpg"),"original",deadline.Token).ConfigureAwait(false);await File.WriteAllTextAsync(Path.Combine(two,"a.jpg.json"),"{}",deadline.Token).ConfigureAwait(false);
        var job=Guid.NewGuid();var target=Target();TakeoutInputBinding[] bindings;
        using(var first=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),one))using(var second=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),two))bindings=[first.Binding,second.Binding];
        using(var initial=new ProcessorStateDatabase(state))new TakeoutImportInventoryRepository(initial).Ensure(job,target,bindings);
        await File.WriteAllTextAsync(Path.Combine(root.Path,"inventory-job.txt"),job.ToString("D"),deadline.Token).ConfigureAwait(false);
        var runtime=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet"));
        var start=new ProcessStartInfo(runtime){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"WorkerFixture","Mpa.Processor.TestWorkerHost.dll"));start.ArgumentList.Add("takeout-inventory-crash");start.ArgumentList.Add(root.Path);
        using var process=new Process{StartInfo=start};Assert.IsTrue(process.Start());var stderr=process.StandardError.ReadToEndAsync(deadline.Token);Guid retained;
        try
        {
            Assert.AreEqual("takeout-inventory-ready",await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false));
            Assert.ThrowsExactly<IOException>(()=>{using var blocked=new ProcessorStateDatabase(state);});
            Assert.ThrowsExactly<IOException>(()=>{using var blocked=new FileStream(Path.Combine(state,"takeout-associations","owner.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);});
            retained=ReadMedia(Index(state,job));process.Kill(entireProcessTree:true);await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            Assert.AreNotEqual(0,process.ExitCode);Assert.AreEqual("",await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false));Assert.AreEqual("",await stderr.ConfigureAwait(false));
        }
        finally
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);await stderr.ConfigureAwait(false);
        }
        using(var restarted=new ProcessorStateDatabase(state))
        {
            var repository=new TakeoutImportInventoryRepository(restarted);var jobs=new DurableJobRepository(restarted);Assert.AreEqual(1,jobs.Summary(job).Running);Assert.AreEqual(0,jobs.PendingReceiptCount());
            Assert.AreEqual(1,repository.RecoverInterruptedAfterExclusiveRestart());Assert.AreEqual(0,repository.RecoverInterruptedAfterExclusiveRestart());var work=repository.Claim(job)!;
            Assert.AreEqual(2,work.Lease.Attempt);Assert.AreEqual(target,work.Target);CollectionAssert.AreEqual(bindings,work.Parts.ToArray());
            using var catalog=TakeoutAssociationCatalog.Open(restarted,work,Budget(state));foreach(var part in work.Parts){using var source=TakeoutSelectedInput.OpenFolder(part.PartId,part.Location);catalog.AddPart(source,deadline.Token);}
            await catalog.SealAsync(deadline.Token).ConfigureAwait(false);Assert.AreEqual(retained,catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single().MediaId);Assert.AreEqual(1,catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Count);
            repository.Complete(work,catalog);Assert.AreEqual(1,jobs.Summary(job).Ready);Assert.AreEqual(1,jobs.PendingReceiptCount());
        }
        Assert.AreEqual("original",await File.ReadAllTextAsync(Path.Combine(one,"a.jpg"),deadline.Token).ConfigureAwait(false));Assert.AreEqual("{}",await File.ReadAllTextAsync(Path.Combine(two,"a.jpg.json"),deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RecoveryCannotInvalidateALiveNativeCatalogOwner()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));using var source=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);
        var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[source.Binding]);var work=repository.Claim(job)!;
        using var catalog=TakeoutAssociationCatalog.Open(database,work,Budget(database.StateDirectory));Assert.ThrowsExactly<IOException>(()=>repository.RecoverInterruptedAfterExclusiveRestart());repository.AssertCurrent(work);
        Assert.AreEqual(1,new DurableJobRepository(database).Summary(job).Running);Assert.AreEqual(0,new DurableJobRepository(database).PendingReceiptCount());
        catalog.AddPart(source);await catalog.SealAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public void UnknownInterruptedStagesCannotBorrowInventoryRecoveryOrPartiallyResetOtherAttempts()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));using var source=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);
        var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[source.Binding]);var original=repository.Claim(job)!;
        var jobs=new DurableJobRepository(database);jobs.AddItem(job,new(Guid.NewGuid(),new string('a',64),[new("unknown-native-stage",new string('b',64),[])]));var unknown=jobs.ClaimNext(TakeoutImportInventoryRepository.Kind,"unknown-native-stage",job)!;Assert.IsNotNull(unknown);
        Assert.ThrowsExactly<InvalidDataException>(()=>repository.RecoverInterruptedAfterExclusiveRestart());repository.AssertCurrent(original);Assert.AreEqual(2,jobs.Summary(job).Running);Assert.AreEqual(0,jobs.PendingReceiptCount());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void JoinedInventoryRecoveryRetainsPauseOrCancelPolicyAndNeverChangesCapturedSelection(bool cancel)
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));using var source=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);
        var repository=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();repository.Ensure(job,Target(),[source.Binding]);var work=repository.Claim(job)!;var jobs=new DurableJobRepository(database);jobs.Pause(job,cancelJob:cancel);
        Assert.AreEqual(1,repository.RecoverInterruptedAfterExclusiveRestart());Assert.IsNull(repository.Claim(job));Assert.AreEqual(0,jobs.PendingReceiptCount());Assert.ThrowsExactly<StaleStageLeaseException>(()=>repository.AssertCurrent(work));
        if(cancel){Assert.IsTrue(jobs.Summary(job).IsCanceled);Assert.AreEqual(1,jobs.Summary(job).Canceled);}
        else{Assert.IsTrue(jobs.Summary(job).Paused);jobs.Resume(job);var retry=repository.Claim(job)!;Assert.AreEqual(work.Lease.CapturedContext,retry.Lease.CapturedContext);Assert.AreEqual(2,retry.Lease.Attempt);}
    }
    private static TakeoutImportTarget Target()=>new(Guid.NewGuid(),"account-one",1,"destination-one",1);
    private static string Index(string state,Guid job)=>Path.Combine(state,"takeout-associations","import-"+job.ToString("N"),"inventory.sqlite");
    private static StorageBudgetManager Budget(string state)=>new(new Space(state));
    private sealed class Space(string state) : IStorageSpaceReader
    {public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,40*StorageBudgetManager.Gibibyte,TakeoutAssociationCatalog.MeasureOwnedBytes(state));}
    private static Guid ReadMedia(string path)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT payload FROM entry WHERE kind=0 LIMIT 1";
        return JsonSerializer.Deserialize<TakeoutCapturedEntry>((byte[])command.ExecuteScalar()!)!.MediaId;
    }
}
