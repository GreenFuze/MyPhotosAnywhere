using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class TakeoutMetadataOperationTests
{
    [TestMethod]
    public async Task JoinedReplayRetirementRemovesOnlyItsVerifiedDuplicateAndRetainedMetadataSurvivesReopen()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var paths=ReplayPaths(fixture,ready.Result);var before=ReplayFiles(paths.Item);var size=TakeoutMetadataStageStorage.MeasureOwnedBytes(fixture.StateDirectory);
        var receipts=await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts().Select(value=>value.Receipt).ToArray(),fixture.Token).ConfigureAwait(false);
        var document=await fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(paths.Replay));Assert.AreEqual(size-fixture.Raw.Length,TakeoutMetadataStageStorage.MeasureOwnedBytes(fixture.StateDirectory));
        var after=ReplayFiles(paths.Item);Assert.AreEqual(before.Count-1,after.Count);
        foreach(var pair in before.Where(pair=>pair.Key!=paths.Replay))Assert.AreEqual(pair.Value,after[pair.Key],pair.Key);
        CollectionAssert.AreEqual(fixture.Raw,document.CopyRawSource());CollectionAssert.AreEqual(fixture.Raw,await File.ReadAllBytesAsync(Path.Combine(paths.Item,"source.bin"),fixture.Token).ConfigureAwait(false));
        // No cloud backup is inferred and no raw/result, item directory or attempt
        // marker is retired. A removed external sidecar does not cause re-analysis.
        File.Delete(Path.Combine(fixture.Completed.Parts.Single().Location,fixture.Sidecar.RelativePath));
        fixture.Storage.Dispose();using var fresh=new TakeoutMetadataStageStorage(fixture.Database,fixture.Budget);
        CollectionAssert.AreEqual(fixture.Raw,(await fresh.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false)).CopyRawSource());
        CollectionAssert.AreEquivalent(after.Select(pair=>pair.Key+":"+pair.Value).ToArray(),ReplayFiles(paths.Item).Select(pair=>pair.Key+":"+pair.Value).ToArray());
        CollectionAssert.AreEqual(receipts,await fixture.State.ExecuteAsync(db=>new DurableJobRepository(db).ReadPendingReceipts().Select(value=>value.Receipt).ToArray(),fixture.Token).ConfigureAwait(false));
        await fixture.State.ExecuteAsync(db=>{Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);Assert.IsNull(new TakeoutMetadataJobRepository(db).Claim(ready.Result.JobId));return true;},fixture.Token).ConfigureAwait(false);
        Assert.AreEqual(1,Directory.GetDirectories(paths.Item).Length);Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow("replay")]
    [DataRow("source")]
    [DataRow("artifact")]
    [DataRow("item-marker")]
    [DataRow("attempt-marker")]
    [DataRow("unknown")]
    [DataRow("pending")]
    [DataRow("diagnostics")]
    public async Task ReplayRetirementPreservesEveryByteWhenEvidenceIsChangedUnknownOrUnresolved(string damage)
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var paths=ReplayPaths(fixture,ready.Result);var attempt=Path.GetDirectoryName(paths.Replay)!;
        var path=damage switch
        {
            "source"=>Path.Combine(paths.Item,"source.bin"),"artifact"=>Path.Combine(attempt,ready.Result.Artifact.RelativePath),
            "item-marker"=>Path.Combine(paths.Item,"claim.json"),"attempt-marker"=>Path.Combine(attempt,"claim.json"),
            "unknown"=>Path.Combine(attempt,"unexpected.bin"),"pending"=>Path.Combine(paths.Item,"source.pending"),"diagnostics"=>Path.Combine(attempt,"diagnostics.log"),_=>paths.Replay
        };
        var existed=File.Exists(path);var original=existed?await File.ReadAllBytesAsync(path,fixture.Token).ConfigureAwait(false):[];var bytes=original.ToArray();
        if(bytes.Length==0)bytes=[9];else bytes[0]^=1;
        await File.WriteAllBytesAsync(path,bytes,fixture.Token).ConfigureAwait(false);var before=ReplayFiles(paths.Item);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Select(pair=>pair.Key+":"+pair.Value).ToArray(),ReplayFiles(paths.Item).Select(pair=>pair.Key+":"+pair.Value).ToArray());
        Assert.IsTrue(File.Exists(paths.Replay));
        // Accounting correctly rejects this deliberately corrupt inventory too.
        // Restore only the synthetic damage after proving nothing was removed,
        // then verify no reservation survived the failed retirement.
        if(existed)await File.WriteAllBytesAsync(path,original,fixture.Token).ConfigureAwait(false);else File.Delete(path);
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        await fixture.State.ExecuteAsync(db=>{Assert.AreEqual(1,new DurableJobRepository(db).Summary(fixture.Original.JobId).Ready);Assert.AreEqual(1,new DurableJobRepository(db).Summary(ready.Result.JobId).Ready);return true;},fixture.Token).ConfigureAwait(false);
        fixture.Storage.Dispose(); // Failed retirement released its actual item use.
    }

    [TestMethod]
    public async Task ReplayRetirementRequiresTheStillValidAtomicReadyReceiptBeforeTouchingFiles()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var paths=ReplayPaths(fixture,ready.Result);var before=ReplayFiles(paths.Item);
        await fixture.State.ExecuteAsync(db=>
        {using var remove=db.Connection.CreateCommand();remove.CommandText="DELETE FROM state_outbox WHERE job_id=$id";remove.Parameters.AddWithValue("$id",ready.Result.JobId.ToString("D"));Assert.AreEqual(1,remove.ExecuteNonQuery());return true;},fixture.Token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Select(pair=>pair.Key+":"+pair.Value).ToArray(),ReplayFiles(paths.Item).Select(pair=>pair.Key+":"+pair.Value).ToArray());
        Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);fixture.Storage.Dispose();
    }

    [TestMethod]
    public async Task CanceledOrForeignOwnerReplayRetirementCannotConsumeTheNativeAdmissionOrDeleteAnyFile()
    {
        await using var fixture=await ImportedFixture.CreateAsync().ConfigureAwait(false);
        var ready=await CompleteImportedObservationAsync(fixture,fixture.Sidecar,await CopyInstalledBundleAsync(fixture.Root.Path,fixture.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var paths=ReplayPaths(fixture,ready.Result);var before=ReplayFiles(paths.Item);
        using(var canceled=new CancellationTokenSource())
        {await canceled.CancelAsync().ConfigureAwait(false);await Assert.ThrowsAsync<OperationCanceledException>(()=>fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,canceled.Token)).ConfigureAwait(false);}
        using var otherRoot=new WorkerProcessTests.WorkerTestDirectory();using var otherDatabase=new ProcessorStateDatabase(otherRoot.Path);await using var otherState=new ProcessorStateCoordinator(otherDatabase);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,otherState,fixture.Token)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(before.Select(pair=>pair.Key+":"+pair.Value).ToArray(),ReplayFiles(paths.Item).Select(pair=>pair.Key+":"+pair.Value).ToArray());
        await fixture.Storage.ReadVerifiedAndRetireReplayAsync(ready.Result,fixture.State,fixture.Token).ConfigureAwait(false);Assert.IsFalse(File.Exists(paths.Replay));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    private static (string Item,string Replay) ReplayPaths(ImportedFixture fixture,TakeoutVerifiedMetadata ready)
    {
        var item=Path.Combine(fixture.StateDirectory,"takeout-metadata-stages","item-"+ready.JobId.ToString("N")+"-"+ready.JobId.ToString("N"));
        return(item,Path.Combine(item,"attempt-"+ready.LeaseId.ToString("N"),"input.bin"));
    }
    private static Dictionary<string,string> ReplayFiles(string item)=>Directory.EnumerateFiles(item,"*",SearchOption.AllDirectories)
        .ToDictionary(path=>path,path=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),StringComparer.Ordinal);
}
