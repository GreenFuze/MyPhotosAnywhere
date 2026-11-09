using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutCompletedInventoryTests
{
    [TestMethod]
    public async Task CompletedImportReopensAfterActualDatabaseRestartWithoutScanningRequeuingOrChangingItsFirstReceipt()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);var original=Path.Combine(folder,"a.jpg");await File.WriteAllTextAsync(original,"original",deadline.Token).ConfigureAwait(false);
        var state=Path.Combine(root.Path,"state");var job=Guid.NewGuid();var part=Guid.NewGuid();Guid media;string receipt;
        using(var database=new ProcessorStateDatabase(state))
        {
            using var source=TakeoutSelectedInput.OpenFolder(part,folder);var jobs=new TakeoutImportInventoryRepository(database);jobs.Ensure(job,Target(),[source.Binding]);
            Assert.IsNull(jobs.ReadCompleted(job));var work=jobs.Claim(job)!;using var catalog=TakeoutAssociationCatalog.Open(database,work,Budget(state));catalog.AddPart(source,deadline.Token);await catalog.SealAsync(deadline.Token).ConfigureAwait(false);
            media=catalog.ReadPage(TakeoutEntryKind.Media).Entries.Single().MediaId;jobs.Complete(work,catalog);receipt=new DurableJobRepository(database).ReadPendingReceipts().Single().Receipt;
        }
        var before=await DigestAsync(Index(state,job),deadline.Token).ConfigureAwait(false);
        using(var restarted=new ProcessorStateDatabase(state))
        {
            var jobs=new TakeoutImportInventoryRepository(restarted);var completed=jobs.ReadCompleted(job)!;Assert.IsNotNull(completed);Assert.AreEqual(job,completed.ImportId);Assert.AreEqual(part,completed.Parts.Single().PartId);
            // A completed selection remains readable after local pause/cancel. It
            // does not acquire new runnable work or a current cloud write grant.
            var durable=new DurableJobRepository(restarted);durable.Pause(job);durable.Pause(job,cancelJob:true);
            using var source=TakeoutSelectedInput.OpenFolder(part,folder);
            using(var heldEnumeration=source.Enumerate(deadline.Token).GetEnumerator())
            {
                Assert.IsTrue(heldEnumeration.MoveNext());
                using var reopened=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(state),deadline.Token).ConfigureAwait(false);
                var entry=reopened.ReadPage(TakeoutEntryKind.Media).Entries.Single();Assert.AreEqual(media,entry.MediaId);
                Assert.ThrowsExactly<InvalidOperationException>(()=>reopened.AddPart(source));await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>reopened.SealAsync(deadline.Token)).ConfigureAwait(false);
                Assert.ThrowsExactly<InvalidOperationException>(()=>reopened.AcquireVerifiedInventory(null!));
                await Assert.ThrowsExactlyAsync<IOException>(()=>TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(state),deadline.Token)).ConfigureAwait(false);
            }
            using(var reopened=await TakeoutAssociationCatalog.OpenCompletedAsync(jobs.ReadCompleted(job)!,Budget(state),deadline.Token).ConfigureAwait(false))
            {
                await using var input=reopened.OpenCaptured(source,reopened.ReadPage(TakeoutEntryKind.Media).Entries.Single(),1024);using var reader=new StreamReader(input);Assert.AreEqual("original",await reader.ReadToEndAsync(deadline.Token).ConfigureAwait(false));
            }
            Assert.IsNull(jobs.Claim(job));Assert.AreEqual(1,durable.Summary(job).Ready);Assert.AreEqual(receipt,durable.ReadPendingReceipts().Single().Receipt);
            durable.MarkReceiptPublished(durable.ReadPendingReceipts().Single().Id);Assert.IsNotNull(jobs.ReadCompleted(job));Assert.AreEqual(0,durable.PendingReceiptCount());
        }
        Assert.AreEqual(before,await DigestAsync(Index(state,job),deadline.Token).ConfigureAwait(false));Assert.AreEqual("original",await File.ReadAllTextAsync(original,deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CompletedInventoryRejectsChangedNativeBytesAndRetainsItsReadyReceiptAndOriginal()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"a.jpg"),"original",deadline.Token).ConfigureAwait(false);
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var job=Guid.NewGuid();var completed=await CompleteAsync(database,job,folder,deadline.Token).ConfigureAwait(false);var index=Index(database.StateDirectory,job);
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=index,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString()))
        {connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE entry SET fold_name='SUBSTITUTED.JPG'";Assert.AreEqual(1,command.ExecuteNonQuery());}
        var corrupt=await DigestAsync(index,deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(database.StateDirectory),deadline.Token)).ConfigureAwait(false);
        Assert.AreEqual(corrupt,await DigestAsync(index,deadline.Token).ConfigureAwait(false));Assert.AreEqual(1,new DurableJobRepository(database).Summary(job).Ready);Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount());
        Assert.AreEqual("original",await File.ReadAllTextAsync(Path.Combine(folder,"a.jpg"),deadline.Token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CanceledCompletedOpenRetiresItsOwnersBeforeAnActualRetry()
    {
        using var root=new WorkerProcessTests.WorkerTestDirectory();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var folder=Path.Combine(root.Path,"input");Directory.CreateDirectory(folder);
        using var database=new ProcessorStateDatabase(Path.Combine(root.Path,"state"));var job=Guid.NewGuid();var completed=await CompleteAsync(database,job,folder,deadline.Token).ConfigureAwait(false);
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(database.StateDirectory),canceled.Token)).ConfigureAwait(false);
        using var retry=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,Budget(database.StateDirectory),deadline.Token).ConfigureAwait(false);Assert.AreEqual(0,retry.ReadPage(TakeoutEntryKind.Media).Entries.Count);
        Assert.AreEqual(1,new DurableJobRepository(database).PendingReceiptCount());
    }
    private static async Task<TakeoutCompletedInventory> CompleteAsync(ProcessorStateDatabase database,Guid job,string folder,CancellationToken token)
    {
        using var source=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),folder);var jobs=new TakeoutImportInventoryRepository(database);jobs.Ensure(job,Target(),[source.Binding]);var work=jobs.Claim(job)!;
        using var catalog=TakeoutAssociationCatalog.Open(database,work,Budget(database.StateDirectory),token);catalog.AddPart(source,token);await catalog.SealAsync(token).ConfigureAwait(false);jobs.Complete(work,catalog);return jobs.ReadCompleted(job)!;
    }
    private static TakeoutImportTarget Target()=>new(Guid.NewGuid(),"account-one",1,"destination-one",1);
    private static string Index(string state,Guid job)=>Path.Combine(state,"takeout-associations","import-"+job.ToString("N"),"inventory.sqlite");
    private static StorageBudgetManager Budget(string state)=>new(new Space(state));
    private static async Task<string> DigestAsync(string path,CancellationToken token){await using var file=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(file,token).ConfigureAwait(false));}
    private sealed class Space(string state) : IStorageSpaceReader
    {public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,40*StorageBudgetManager.Gibibyte,TakeoutAssociationCatalog.MeasureOwnedBytes(state));}
}
