using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class TakeoutMetadataOperationTests
{
    [TestMethod]
    public async Task ImportedObservationsAreAtomicSnapshotBoundAndConflictingSourcesNeverChooseAHiddenWinner()
    {
        await using var fixture=await ImportedFixture.CreateAsync(conflictingSidecar:true).ConfigureAwait(false);
        var bundle=await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false);
        var first=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,bundle).ConfigureAwait(false);
        var asset=new DriveCatalogAsset(fixture.Original.AssetId,"account-one","first-verified-file",7,"Human filename.jpg","image/jpeg",40,30,null,null,9000,Array.AsReadOnly(new[]{new DriveCatalogSource(Guid.NewGuid(),1,0)}));
        using var old=await fixture.State.ExecuteAsync(db=>db.OpenReadSnapshot(),fixture.Token).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>
        {
            using(var trigger=db.Connection.CreateCommand()){trigger.CommandText="CREATE TRIGGER fail_imported_revision BEFORE UPDATE ON discovery_revision BEGIN SELECT RAISE(ABORT,'synthetic revision failure'); END";trigger.ExecuteNonQuery();}
            var facts=new TakeoutImportedPhotoFactsRepository(db);var receipts=new DurableJobRepository(db).PendingReceiptCount();
            Assert.ThrowsExactly<SqliteException>(()=>facts.Retain(first.Result,first.Document));
            using(var count=db.Connection.CreateCommand()){count.CommandText="SELECT count(*) FROM processor_meta WHERE key LIKE 'takeout-photo-observation-v1:%'";Assert.AreEqual(0L,(long)count.ExecuteScalar()!);}
            using(var drop=db.Connection.CreateCommand()){drop.CommandText="DROP TRIGGER fail_imported_revision";drop.ExecuteNonQuery();}
            Assert.IsTrue(facts.Retain(first.Result,first.Document));using var revision=db.Connection.CreateCommand();revision.CommandText="SELECT revision FROM discovery_revision WHERE id=1";var before=(long)revision.ExecuteScalar()!;
            Assert.IsFalse(facts.Retain(first.Result,first.Document));Assert.AreEqual(before,(long)revision.ExecuteScalar()!);Assert.AreEqual(receipts,new DurableJobRepository(db).PendingReceiptCount());return true;
        },fixture.Token).ConfigureAwait(false);
        DriveCatalogAsset Read(ProcessorStateReadSnapshot snapshot)=>TakeoutImportedPhotoFactsRepository.Apply(snapshot.Connection,snapshot.Transaction,fixture.Completed.Target.LibraryId,asset,fixture.Token);
        Assert.IsNull(Read(old).ImportedMetadata);using var single=await fixture.State.ExecuteAsync(db=>db.OpenReadSnapshot(),fixture.Token).ConfigureAwait(false);
        Assert.AreEqual("Imported detail",Read(single).ImportedMetadata!.Description);Assert.AreEqual(1000L,Read(single).EffectiveUtc);
        var changedOriginal=TakeoutImportedPhotoFactsRepository.Apply(single.Connection,single.Transaction,fixture.Completed.Target.LibraryId,asset with{DriveVersion=8},fixture.Token);
        Assert.IsNull(changedOriginal.ImportedMetadata);Assert.AreEqual("original-version-changed",changedOriginal.ImportedAttention!.Code);Assert.AreEqual(asset.Name,changedOriginal.Name);Assert.AreEqual(9000L,changedOriginal.EffectiveUtc);
        var extra=fixture.Catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Single(entry=>entry.RelativePath.EndsWith("supplemental-metadata.json",StringComparison.Ordinal));var second=await CompleteImportedObservationAsync(fixture,extra,bundle).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>new TakeoutImportedPhotoFactsRepository(db).Retain(second.Result,second.Document),fixture.Token).ConfigureAwait(false);
        using var both=await fixture.State.ExecuteAsync(db=>db.OpenReadSnapshot(),fixture.Token).ConfigureAwait(false);var conflict=Read(both);
        Assert.IsNull(conflict.ImportedMetadata);Assert.AreEqual(2L,conflict.ImportedAttention!.Candidates);Assert.AreEqual("conflicting-metadata",conflict.ImportedAttention.Code);Assert.AreEqual(9000L,conflict.EffectiveUtc);Assert.AreEqual("Human filename.jpg",conflict.Name);
        var codec=new DriveCatalogFacts(fixture.Completed.Target.LibraryId);var wire=codec.Encode(asset.Id.ToString("N")[..2],[conflict]);Assert.AreEqual(conflict.ImportedAttention,codec.Read(wire,asset.Id.ToString("N")[..2]).Assets.Single().ImportedAttention);
        Assert.AreEqual("Imported detail",Read(single).ImportedMetadata!.Description,"A captured snapshot cannot observe a later conflicting result.");
        await fixture.State.ExecuteAsync(db=>
        {
            using var corrupt=db.Connection.CreateCommand();corrupt.CommandText="UPDATE processor_meta SET value=json_set(value,'$.description','changed') WHERE key LIKE $key";corrupt.Parameters.AddWithValue("$key",TakeoutImportedPhotoFactsRepository.Prefix+asset.Id.ToString("D")+":"+first.Result.JobId.ToString("D")+":%");Assert.AreEqual(1,corrupt.ExecuteNonQuery());
            Assert.ThrowsExactly<InvalidDataException>(()=>new TakeoutImportedPhotoFactsRepository(db).Retain(first.Result,first.Document));return true;
        },fixture.Token).ConfigureAwait(false);
        using var changed=await fixture.State.ExecuteAsync(db=>db.OpenReadSnapshot(),fixture.Token).ConfigureAwait(false);Assert.ThrowsExactly<InvalidDataException>(()=>Read(changed));
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }
    [TestMethod]
    public async Task ImportedFactsTooLargeForAPhotoRecordRemainRetainedWithoutTruncatingTheMetadataOrHumanFilename()
    {
        await using var fixture=await ImportedFixture.CreateAsync(descriptionLength:63000).ConfigureAwait(false);var bundle=await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,bundle).ConfigureAwait(false);
        var facts=await fixture.State.ExecuteAsync(db=>{Assert.IsTrue(new TakeoutImportedPhotoFactsRepository(db).Retain(ready.Result,ready.Document));return ready.Result.DescribeImportedFacts(ready.Document);},fixture.Token).ConfigureAwait(false);
        Assert.IsTrue(facts.Encode().Length<=ImportedPhotoMetadata.MaximumEncodedBytes);Assert.AreEqual(63000,facts.Description.Length);
        var asset=new DriveCatalogAsset(fixture.Original.AssetId,"account-one","first-verified-file",7,new string('h',4096),"image/jpeg",40,30,null,null,9000,Array.AsReadOnly(new[]{new DriveCatalogSource(Guid.NewGuid(),1,0)}));
        using var snapshot=await fixture.State.ExecuteAsync(db=>db.OpenReadSnapshot(),fixture.Token).ConfigureAwait(false);
        var projected=TakeoutImportedPhotoFactsRepository.Apply(snapshot.Connection,snapshot.Transaction,fixture.Completed.Target.LibraryId,asset,fixture.Token);
        Assert.IsNull(projected.ImportedMetadata);Assert.AreEqual("metadata-too-large",projected.ImportedAttention!.Code);Assert.AreEqual(1L,projected.ImportedAttention.Candidates);Assert.AreEqual(asset.Name,projected.Name);Assert.AreEqual(9000L,projected.EffectiveUtc);
        var codec=new DriveCatalogFacts(fixture.Completed.Target.LibraryId);Assert.AreEqual(projected.ImportedAttention,codec.Read(codec.Encode(asset.Id.ToString("N")[..2],[projected]),asset.Id.ToString("N")[..2]).Assets.Single().ImportedAttention);
        Assert.AreEqual(63000,(await fixture.Storage.ReadVerifiedAsync(ready.Result,fixture.Token).ConfigureAwait(false)).Description.Length);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }
    private static async Task<(TakeoutVerifiedMetadata Result,TakeoutMetadataDocument Document)> CompleteImportedObservationAsync(ImportedFixture fixture,TakeoutCapturedEntry entry,string bundle)
    {
        var raw=await File.ReadAllBytesAsync(Path.Combine(fixture.Completed.Parts.Single().Location,entry.RelativePath),fixture.Token).ConfigureAwait(false);var document=TakeoutMetadataDocument.ParseSource(raw);
        var descriptor=new ArtifactDescriptor(raw.Length,document.SourceSha256,"input.bin");var association=fixture.Catalog.AssociateMetadata(entry.Key,document);
        var work=await fixture.State.ExecuteAsync(db=>{var jobs=new TakeoutMetadataJobRepository(db);return jobs.Claim(jobs.EnsureImported(fixture.Original,association,descriptor))!;},fixture.Token).ConfigureAwait(false);
        using(var source=new MemoryStream(raw,false))await fixture.Storage.CaptureAsync(work,source,fixture.Token).ConfigureAwait(false);
        var installed=await TakeoutWorkerInstallation.OpenInstalledAsync(bundle,fixture.Token).ConfigureAwait(false);
        await using var native=new ProcessorWorkSupervisor();await native.Start(await TakeoutMetadataWorkerOperation.CreateAsync(fixture.State,work,fixture.Storage,installed,fixture.Token).ConfigureAwait(false),fixture.Token).ConfigureAwait(false);
        var ready=(await fixture.State.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(fixture.Completed.ImportId,entry.Key),fixture.Token).ConfigureAwait(false))!;
        return(ready,await fixture.Storage.ReadVerifiedAsync(ready,fixture.Token).ConfigureAwait(false));
    }
}
