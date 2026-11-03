using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class TakeoutMetadataOperationTests
{
    [TestMethod]
    public async Task PerFileMetadataIssuesRetainFirstSourceAndIndexBindingWithoutMintingAnyWorkOrBackupReceipt()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);var input=fixture.Catalog.CaptureMetadataIssueInput(fixture.Completed,fixture.Sidecar);
        await fixture.State.ExecuteAsync(db=>
        {
            var issues=new TakeoutMetadataIssueRepository(db);var receipts=new DurableJobRepository(db).PendingReceiptCount();
            issues.Record(input,"invalid-metadata",fixture.Descriptor.Sha256);issues.Record(input,"invalid-metadata",fixture.Descriptor.Sha256);
            Assert.ThrowsExactly<InvalidDataException>(()=>issues.Record(input,"invalid-metadata",new string('b',64)));
            Assert.ThrowsExactly<InvalidDataException>(()=>issues.Record(input,"name-mismatch",fixture.Descriptor.Sha256));
            var page=issues.ReadPage(fixture.Completed.ImportId);Assert.AreEqual(1,page.Issues.Count);Assert.IsNull(page.Next);
            Assert.AreEqual(new TakeoutMetadataIssue(fixture.Sidecar.Key,"a.jpg.json","invalid-metadata",false),page.Issues.Single());
            Assert.AreEqual(receipts,new DurableJobRepository(db).PendingReceiptCount());Assert.IsNull(new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,fixture.Sidecar.Key));
            using var corrupt=db.Connection.CreateCommand();corrupt.CommandText="UPDATE processor_meta SET value=json_set(value,'$.entry.relativePath','changed.json') WHERE key=$key";corrupt.Parameters.AddWithValue("$key","takeout-metadata-item-v1:"+fixture.Completed.ImportId.ToString("D")+":"+fixture.Sidecar.Key);Assert.AreEqual(1,corrupt.ExecuteNonQuery());
            Assert.ThrowsExactly<InvalidDataException>(()=>issues.ReadPage(fixture.Completed.ImportId));return true;
        },fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PerFileMetadataIssuePagingUsesBoundedNativeKeysAndCannotBorrowAnotherStateOwner()
    {
        await using var fixture=await ImportedFixture.CreateAsync(70).ConfigureAwait(false);
        var entries=fixture.Catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries;Assert.AreEqual(71,entries.Count);
        var captures=entries.Select(entry=>fixture.Catalog.CaptureMetadataIssueInput(fixture.Completed,entry)).ToArray();
        await fixture.State.ExecuteAsync(db=>
        {
            var issues=new TakeoutMetadataIssueRepository(db);foreach(var input in captures)issues.Record(input,"input-unavailable");
            var first=issues.ReadPage(fixture.Completed.ImportId);Assert.AreEqual(60,first.Issues.Count);Assert.IsNotNull(first.Next);
            var second=issues.ReadPage(fixture.Completed.ImportId,first.Next);Assert.AreEqual(11,second.Issues.Count);Assert.IsNull(second.Next);
            var keys=first.Issues.Concat(second.Issues).Select(issue=>issue.Key).ToArray();Assert.AreEqual(71,keys.Distinct(StringComparer.Ordinal).Count());CollectionAssert.AreEqual(entries.Select(entry=>entry.Key).ToArray(),keys);
            Assert.IsTrue(first.Issues.All(issue=>issue.Retryable));Assert.ThrowsExactly<ArgumentException>(()=>issues.ReadPage(fixture.Completed.ImportId,limit:61));Assert.ThrowsExactly<ArgumentException>(()=>issues.ReadPage(fixture.Completed.ImportId,"../outside"));return true;
        },fixture.Token).ConfigureAwait(false);
        using var otherRoot=new WorkerProcessTests.WorkerTestDirectory();using var other=new ProcessorStateDatabase(otherRoot.Path);
        Assert.ThrowsExactly<InvalidOperationException>(()=>new TakeoutMetadataIssueRepository(other).Record(captures[0],"input-unavailable"));
    }
}
