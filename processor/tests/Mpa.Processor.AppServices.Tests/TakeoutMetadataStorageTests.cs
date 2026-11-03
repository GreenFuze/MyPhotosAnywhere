using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class TakeoutMetadataStorageTests
{
    [TestMethod]
    public async Task TakeoutRetainedSnapshotsAreIncludedInTheActualEngineVolumeBudget()
    {
        var root=Path.Combine(Path.GetTempPath(),"mpa-takeout-accounting-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var database=new ProcessorStateDatabase(root);var reader=new ProcessorStateStorageReader(root);
            var jobs=new TakeoutMetadataJobRepository(database);var raw=Encoding.UTF8.GetBytes("{\"title\":\"a.jpg\",\"creationTime\":{\"timestamp\":\"0\"}}");
            jobs.Ensure(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),new string('a',64),new ArtifactDescriptor(raw.Length,Convert.ToHexStringLower(SHA256.HashData(raw)),"input.bin"));
            var work=jobs.ClaimNext()!;var before=reader.Read().OwnedCacheBytes;var budget=new StorageBudgetManager(reader);
            using var storage=new TakeoutMetadataStageStorage(database,budget);
            using(var source=new MemoryStream(raw,false))await storage.CaptureAsync(work,source,CancellationToken.None).ConfigureAwait(false);
            var measured=TakeoutMetadataStageStorage.MeasureOwnedBytes(root);Assert.IsGreaterThan(raw.LongLength,measured);
            Assert.AreEqual(before+measured,reader.Read().OwnedCacheBytes);Assert.AreEqual(0L,budget.Status().ReservedBytes);
            var unknown=Path.Combine(root,"takeout-metadata-stages","never-owned-original.jpg");await File.WriteAllBytesAsync(unknown,new byte[]{42}).ConfigureAwait(false);
            Assert.ThrowsExactly<InvalidDataException>(()=>reader.Read());Assert.AreEqual(42,File.ReadAllBytes(unknown).Single());
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,recursive:true);}
    }
}
