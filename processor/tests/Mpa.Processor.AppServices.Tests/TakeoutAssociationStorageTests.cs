using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class TakeoutAssociationStorageTests
{
    [TestMethod]
    public async Task TakeoutRetainedAssociationIndexesAreIncludedInTheActualEngineVolumeBudget()
    {
        var root=Path.Combine(Path.GetTempPath(),"mpa-takeout-index-accounting-"+Guid.NewGuid().ToString("N"));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var state=Path.Combine(root,"state");var originals=Path.Combine(root,"originals");Directory.CreateDirectory(originals);
            var original=Path.Combine(originals,"a.jpg");await File.WriteAllTextAsync(original,"retained original",deadline.Token).ConfigureAwait(false);
            using var database=new ProcessorStateDatabase(state);using var input=TakeoutSelectedInput.OpenFolder(Guid.NewGuid(),originals);
            var jobs=new TakeoutImportInventoryRepository(database);var job=Guid.NewGuid();jobs.Ensure(job,new(Guid.NewGuid(),"account-one",1,"destination-one",1),[input.Binding]);var work=jobs.Claim(job)!;
            var reader=new ProcessorStateStorageReader(state);var budget=new StorageBudgetManager(reader);var before=reader.Read().OwnedCacheBytes;
            using(var catalog=TakeoutAssociationCatalog.Open(database,work,budget,deadline.Token))
            {
                catalog.AddPart(input,deadline.Token);await catalog.SealAsync(deadline.Token).ConfigureAwait(false);
                var measured=TakeoutAssociationCatalog.MeasureOwnedBytes(state);Assert.IsGreaterThan(0L,measured);
                Assert.AreEqual(before+measured,reader.Read().OwnedCacheBytes);Assert.AreEqual(0L,budget.Status().ReservedBytes);
                Assert.AreEqual(1,catalog.ReadPage(TakeoutEntryKind.Media,token:deadline.Token).Entries.Count);
            }
            var retained=TakeoutAssociationCatalog.MeasureOwnedBytes(state);Assert.AreEqual(before+retained,reader.Read().OwnedCacheBytes);
            var unknown=Path.Combine(state,"takeout-associations","import-"+job.ToString("N"),"never-owned-original.jpg");
            await File.WriteAllBytesAsync(unknown,new byte[]{42},deadline.Token).ConfigureAwait(false);
            Assert.ThrowsExactly<InvalidDataException>(()=>reader.Read());Assert.AreEqual(42,File.ReadAllBytes(unknown).Single());
            Assert.AreEqual("retained original",await File.ReadAllTextAsync(original,deadline.Token).ConfigureAwait(false));
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,recursive:true);}
    }
}
