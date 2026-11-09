using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class TakeoutMetadataOperationTests
{
    [TestMethod]
    [DataRow("name-mismatch")]
    [DataRow("missing-media")]
    [DataRow("ambiguous-media")]
    public async Task ExplicitAssociationRetainsFirstRawAndVerifiedOriginalWithoutRewritingAnyReceipt(string code)
    {
        await using var fixture=await ImportedFixture.CreateAsync(additionalOriginals:1).ConfigureAwait(false);
        var input=fixture.Catalog.CaptureMetadataIssueInput(fixture.Completed,fixture.Sidecar);
        var selection=await fixture.State.ExecuteAsync(db=>
        {
            var issues=new TakeoutMetadataIssueRepository(db);issues.Record(input,code,fixture.Descriptor.Sha256);
            var observations=new TakeoutImportedPhotoFactsRepository(db);observations.CompleteObservationPass(fixture.Completed.ImportId);Assert.IsTrue(observations.HasObservedImport(fixture.Completed.ImportId));
            var receipts=new DurableJobRepository(db).PendingReceiptCount();issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key);
            issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key);
            Assert.AreEqual(1L,issues.AssociationCount(fixture.Completed.ImportId));Assert.IsFalse(observations.HasObservedImport(fixture.Completed.ImportId));Assert.AreEqual(receipts,new DurableJobRepository(db).PendingReceiptCount());
            var alternative=new TakeoutOriginalJobRepository(db).ReadChoices(fixture.Completed.ImportId).Originals.Single(value=>value.Key!=fixture.Original.Capture.Entry.Key);
            Assert.ThrowsExactly<TakeoutMetadataSelectionException>(()=>issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,alternative.Key));
            Assert.ThrowsExactly<TakeoutMetadataSelectionException>(()=>issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,new string('b',64)));
            return issues.ReadAssociation(fixture.Completed,fixture.Sidecar.Key)!;
        },fixture.Token).ConfigureAwait(false);
        var associated=fixture.Catalog.AssociateMetadata(fixture.Completed,selection,fixture.Document);Assert.AreEqual("matched",associated.Status);Assert.AreEqual(fixture.Original.Capture.Entry,associated.Media);
        var changed=TakeoutMetadataDocument.ParseSource("{\"title\":\"a.jpg\",\"description\":\"Changed source\",\"photoTakenTime\":{\"timestamp\":\"1\"}}"u8.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(()=>fixture.Catalog.AssociateMetadata(fixture.Completed,selection,changed));
        await fixture.State.ExecuteAsync(db=>
        {
            var work=new TakeoutMetadataJobRepository(db).Claim(new TakeoutMetadataJobRepository(db).EnsureImported(selection.Original,associated,fixture.Descriptor))!;
            Assert.AreEqual(fixture.Original.AssetId,work.MediaId);new TakeoutMetadataJobRepository(db).ReturnJoinedOperation(work);
            using var corrupt=db.Connection.CreateCommand();corrupt.CommandText="UPDATE processor_meta SET value=json_set(value,'$.sourceSha256',$hash) WHERE key=$key";
            corrupt.Parameters.AddWithValue("$key","takeout-manual-metadata-v1:"+fixture.Completed.ImportId.ToString("D")+":"+fixture.Sidecar.Key);corrupt.Parameters.AddWithValue("$hash",new string('b',64));Assert.AreEqual(1,corrupt.ExecuteNonQuery());
            Assert.ThrowsExactly<InvalidDataException>(()=>new TakeoutMetadataIssueRepository(db).ReadAssociation(fixture.Completed,fixture.Sidecar.Key));Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);return true;
        },fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("invalid-metadata")]
    [DataRow("unsupported-metadata")]
    [DataRow("metadata-too-large")]
    [DataRow("input-unavailable")]
    public async Task ExplicitAssociationCannotOverrideAnUnsupportedOrUnavailableInput(string code)
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);var input=fixture.Catalog.CaptureMetadataIssueInput(fixture.Completed,fixture.Sidecar);
        await fixture.State.ExecuteAsync(db=>
        {
            var issues=new TakeoutMetadataIssueRepository(db);issues.Record(input,code,fixture.Descriptor.Sha256);
            Assert.ThrowsExactly<TakeoutMetadataSelectionException>(()=>issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key));
            Assert.ThrowsExactly<ArgumentException>(()=>issues.SelectOriginal(fixture.Completed.ImportId,"../outside",fixture.Original.Capture.Entry.Key));
            Assert.AreEqual(0L,issues.AssociationCount(fixture.Completed.ImportId));return true;
        },fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OriginalChoicePagesExposeOnlyCompleteSameImportReceiptsInBoundedNonoverlappingPages()
    {
        await using var fixture=await ImportedFixture.CreateAsync(additionalOriginals:62).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>
        {
            var originals=new TakeoutOriginalJobRepository(db);var first=originals.ReadChoices(fixture.Completed.ImportId);Assert.AreEqual(60,first.Originals.Count);Assert.IsNotNull(first.Next);
            var second=originals.ReadChoices(fixture.Completed.ImportId,first.Next);Assert.AreEqual(3,second.Originals.Count);Assert.IsNull(second.Next);
            var all=first.Originals.Concat(second.Originals).ToArray();Assert.AreEqual(63,all.Select(value=>value.Key).Distinct(StringComparer.Ordinal).Count());Assert.AreEqual(63,all.Select(value=>value.FileId).Distinct(StringComparer.Ordinal).Count());
            foreach(var choice in all){var receipt=originals.ReadVerified(fixture.Completed.ImportId,choice.Key)!;Assert.AreEqual(receipt.FileId,choice.FileId);Assert.AreEqual(receipt.Capture.Entry.RelativePath,choice.Name);}
            Assert.ThrowsExactly<InvalidOperationException>(()=>originals.ReadChoices(Guid.NewGuid()));Assert.ThrowsExactly<ArgumentException>(()=>originals.ReadChoices(fixture.Completed.ImportId,"../outside"));Assert.ThrowsExactly<ArgumentException>(()=>originals.ReadChoices(fixture.Completed.ImportId,limit:61));return true;
        },fixture.Token).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SelectedMetadataCapacityRequiresVerifiedBytesAndKeepsItsRawResultAndExactOriginalSelection(bool oversized)
    {
        await using var fixture=await ImportedFixture.CreateAsync(descriptionLength:oversized?70000:0).ConfigureAwait(false);
        var issueInput=fixture.Catalog.CaptureMetadataIssueInput(fixture.Completed,fixture.Sidecar);
        await fixture.State.ExecuteAsync(db=>
        {var issues=new TakeoutMetadataIssueRepository(db);issues.Record(issueInput,"name-mismatch",fixture.Descriptor.Sha256);issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key);return true;},fixture.Token).ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>
        {
            var issues=new TakeoutMetadataIssueRepository(db);
            if(oversized)
            {
                Assert.ThrowsExactly<CatalogChunkCapacityException>(()=>new TakeoutImportedPhotoFactsRepository(db).Retain(ready.Result,ready.Document));
                issues.ObserveProjectionCapacity(ready.Result,ready.Document);issues.ObserveProjectionCapacity(ready.Result,ready.Document);
                Assert.AreEqual("metadata-too-large",issues.Find(fixture.Completed,fixture.Sidecar.Key)!.Code);
            }
            else
            {
                Assert.ThrowsExactly<InvalidDataException>(()=>issues.ObserveProjectionCapacity(ready.Result,ready.Document));
                Assert.AreEqual("name-mismatch",issues.Find(fixture.Completed,fixture.Sidecar.Key)!.Code);
            }
            Assert.AreEqual(fixture.Original.Capture.Entry,issues.ReadAssociation(fixture.Completed,fixture.Sidecar.Key)!.Original.Capture.Entry);
            issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key);Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);return true;
        },fixture.Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(fixture.Raw,(await fixture.Storage.ReadVerifiedAsync(ready.Result,fixture.Token).ConfigureAwait(false)).CopyRawSource());Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task SelectionAndObservationWakeupCommitTogetherAndSurviveAReopenedNativeDatabase()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);var input=fixture.Catalog.CaptureMetadataIssueInput(fixture.Completed,fixture.Sidecar);
        await fixture.State.ExecuteAsync(db=>
        {
            var issues=new TakeoutMetadataIssueRepository(db);issues.Record(input,"missing-media",fixture.Descriptor.Sha256);
            var observations=new TakeoutImportedPhotoFactsRepository(db);observations.CompleteObservationPass(fixture.Completed.ImportId);
            using(var trigger=db.Connection.CreateCommand()){trigger.CommandText="CREATE TRIGGER fail_selection_wakeup BEFORE DELETE ON processor_meta WHEN old.key LIKE 'takeout-photo-import-observed-v1:%' BEGIN SELECT RAISE(ABORT,'synthetic selection transaction failure'); END";trigger.ExecuteNonQuery();}
            Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(()=>issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key));
            Assert.AreEqual(0L,issues.AssociationCount(fixture.Completed.ImportId));Assert.IsTrue(observations.HasObservedImport(fixture.Completed.ImportId));
            using(var drop=db.Connection.CreateCommand()){drop.CommandText="DROP TRIGGER fail_selection_wakeup";drop.ExecuteNonQuery();}
            issues.SelectOriginal(fixture.Completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key);return true;
        },fixture.Token).ConfigureAwait(false);
        fixture.Storage.Dispose();fixture.Catalog.Dispose();await fixture.State.DisposeAsync().ConfigureAwait(false);fixture.Database.Dispose();
        using var reopened=new ProcessorStateDatabase(fixture.StateDirectory);var completed=new TakeoutImportInventoryRepository(reopened).ReadCompleted(fixture.Completed.ImportId)!;
        var retained=new TakeoutMetadataIssueRepository(reopened).ReadAssociation(completed,fixture.Sidecar.Key)!;
        Assert.AreEqual(fixture.Original.FileId,retained.Original.FileId);Assert.AreEqual(fixture.Original.Version,retained.Original.Version);Assert.AreEqual(fixture.Descriptor.Sha256,retained.SourceSha256);
        Assert.IsFalse(new TakeoutImportedPhotoFactsRepository(reopened).HasObservedImport(completed.ImportId));Assert.AreEqual(1,new DurableJobRepository(reopened).Summary(fixture.Original.JobId).Ready);
        new TakeoutMetadataIssueRepository(reopened).SelectOriginal(completed.ImportId,fixture.Sidecar.Key,fixture.Original.Capture.Entry.Key);
        Assert.AreEqual(1L,new TakeoutMetadataIssueRepository(reopened).AssociationCount(completed.ImportId));
    }
}
