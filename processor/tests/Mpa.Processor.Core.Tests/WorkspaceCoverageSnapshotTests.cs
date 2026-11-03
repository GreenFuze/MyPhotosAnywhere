using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CatalogCoverageSnapshotTests
{
    [TestMethod]
    public void WorkspaceExclusionAndPinUseTheSameObservationWhileEarlierSnapshotsRemainStable()
    {
        using var fixture = new Fixture(); fixture.Create(); PrepareWorkspaceChild(fixture);
        using var earlier = CatalogFactsSnapshot.Open(fixture.Database); var before = earlier.ReadCoverageSources().Single();
        PinWorkspace(fixture); var child = fixture.Queue.ClaimNext()!; Assert.IsTrue(fixture.Queue.TryExcludeManagedWorkspace(child));
        Assert.AreEqual(before, earlier.ReadCoverageSources().Single()); Assert.AreEqual("scanning", before.State);
        var current = fixture.Read(); Assert.AreEqual("complete", current.State); Assert.AreEqual(1L, current.Checkpoint!.ObservedItems);
        Assert.AreEqual(0L, current.Checkpoint.FailedItems);
    }

    [TestMethod]
    public void ExcludedWorkspaceParentRepairIsNotCountedAsAPhotoObservation()
    {
        using var fixture = new Fixture(); var job = fixture.Create(); PrepareWorkspaceChild(fixture);
        var child = fixture.Queue.ClaimNext()!; fixture.Queue.RecordFailure(child, "failed-media");
        child = fixture.Queue.Advance(child, null, 0, 1, false); fixture.Queue.CompleteFolder(child);
        PinWorkspace(fixture); Assert.AreEqual(1, fixture.Jobs.RetryFailed(job));
        var repair = fixture.Queue.ClaimRepair()!; Assert.IsTrue(fixture.Queue.TryExcludeManagedWorkspace(repair));
        var coverage = fixture.Read(); Assert.AreEqual("complete", coverage.State); Assert.AreEqual(1L, coverage.Checkpoint!.ObservedItems);
        Assert.AreEqual(0L, coverage.Checkpoint.FailedItems);
    }

    [TestMethod]
    [DataRow("UPDATE item_stage SET result_receipt=json_set(result_receipt,'$.reason','another-reason') WHERE result_receipt LIKE '%mpa-source-excluded-workspace-1%'")]
    [DataRow("UPDATE item_stage SET result_receipt=json_set(result_receipt,'$.folderId','another-folder') WHERE result_receipt LIKE '%mpa-source-excluded-workspace-1%'")]
    [DataRow("UPDATE item_stage SET result_receipt=json_set(result_receipt,'$.sourceId','00000000-0000-4000-8000-000000000001') WHERE result_receipt LIKE '%mpa-source-excluded-workspace-1%'")]
    [DataRow("DELETE FROM processor_meta WHERE key IN ('library_controller_v1','library_controller_v1:sha256')")]
    public void UnscopedOrAlteredExclusionsNeverClaimCompleteCoverage(string corruption)
    {
        using var fixture = new Fixture(); fixture.Create(); PrepareWorkspaceChild(fixture); PinWorkspace(fixture);
        Assert.IsTrue(fixture.Queue.TryExcludeManagedWorkspace(fixture.Queue.ClaimNext()!)); fixture.Sql(corruption);
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Read());
    }

    [TestMethod]
    public void CompletedWorkspaceWithPriorPhotosStillBlocksCoverageAndPreservesItsFacts()
    {
        using var fixture = new Fixture(); fixture.Create(); PrepareWorkspaceChild(fixture); var child = fixture.Queue.ClaimNext()!;
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("prior-original")]); child = fixture.Queue.Advance(child, null, 1, 0, false);
        fixture.Queue.CompleteFolder(child); PinWorkspace(fixture);
        using var snapshot = CatalogFactsSnapshot.Open(fixture.Database);
        Assert.ThrowsExactly<InvalidDataException>(() => snapshot.ReadCoverageSources()); Assert.AreEqual(1, snapshot.ReadPage("").Assets.Count);
    }

    [TestMethod]
    [DataRow("UPDATE processor_meta SET value='corrupt' WHERE key='library_controller_v1:sha256'")]
    [DataRow("DELETE FROM processor_meta WHERE key='library_controller_v1'")]
    public void CapturedBoundaryCannotIgnoreAnIncompleteOrCorruptController(string corruption)
    {
        using var fixture = new Fixture(); fixture.Create(); PinWorkspace(fixture); fixture.Sql(corruption);
        Assert.ThrowsExactly<InvalidDataException>(() => CatalogFactsSnapshot.Open(fixture.Database));
    }

    private static void PrepareWorkspaceChild(Fixture fixture)
    {
        var root = fixture.Queue.ClaimNext()!; fixture.Queue.EnqueueChild(root, "metadata-workspace", null);
        root = fixture.Queue.Advance(root, null, 1, 0, false); fixture.Queue.CompleteFolder(root);
    }
    private static void PinWorkspace(Fixture fixture)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        new LibraryAuthorityRepository(fixture.Database, new(new WorkspaceSpace())).Bootstrap(Guid.NewGuid(), 1,
            new("account-A", "metadata-workspace"), 1, key.ExportSubjectPublicKeyInfo());
    }
    private sealed class WorkspaceSpace : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
}
