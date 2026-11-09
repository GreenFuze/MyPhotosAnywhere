using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void FirstUnresolvedInventoryStartSurvivesLaterFailureRestartAndCompletedListings()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); WorkspaceObservationContext context; WorkspaceObservationAttempt started;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
            started = repository.StartInventory(repository.BeginScan(context), "first-unresolved");
            Assert.AreEqual("first-unresolved", started.FirstUnresolvedCursor); Assert.AreEqual("first-unresolved", started.InventoryStartCursor);
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => repository.StartInventory(started, "substitute"));
        }
        using var database = new ProcessorStateDatabase(root.Path); var recovered = new WorkspaceObservationRepository(database, new(new Space()));
        Assert.AreEqual(started, recovered.LastAttempt()); var next = recovered.BeginScan(context);
        Assert.AreEqual("first-unresolved", next.FirstUnresolvedCursor); Assert.IsNull(next.InventoryStartCursor);
        var current = recovered.StartInventory(next, "new-current"); Assert.AreEqual("first-unresolved", current.FirstUnresolvedCursor);
        var checkpoint = recovered.CompleteScan(current, "latest-checkpoint", []); recovered.AssertCompletedScan(checkpoint);
        var following = recovered.BeginScan(context); Assert.AreEqual("first-unresolved", following.FirstUnresolvedCursor);
        Assert.IsNull(following.InventoryStartCursor); // A completed listing is not general failed-history verification.
        Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => recovered.StartInventory(started, "late"));
    }

    [TestMethod]
    public void LegacyAttemptEncodingAndThreeMaximumOpaqueCursorsKeepTheirSeparateBounds()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var context = ObservationContext(fixture); var repository = new WorkspaceObservationRepository(fixture.Database, fixture.Budget);
        var legacy = repository.BeginScan(context);
        var text = (string)fixture.Scalar("SELECT value FROM processor_meta WHERE key='workspace_observation_attempt_v1'");
        Assert.IsFalse(text.Contains("FirstUnresolvedCursor", StringComparison.Ordinal)); Assert.IsFalse(text.Contains("InventoryStartCursor", StringComparison.Ordinal));
        Assert.AreEqual(legacy, repository.LastAttempt());
        var first = new string('ש', 4096); var latest = new string('א', 4096); var end = new string('ב', 4096);
        repository.StartInventory(legacy, first); var following = repository.StartInventory(repository.BeginScan(context), latest);
        var checkpoint = repository.CompleteScan(following, end, []);
        Assert.AreEqual(first, repository.LastAttempt()!.FirstUnresolvedCursor); Assert.AreEqual(latest, repository.LastAttempt()!.InventoryStartCursor);
        Assert.AreEqual(end, checkpoint.Cursor); repository.AssertCompletedScan(checkpoint);
        var next = repository.BeginScan(context);
        Assert.ThrowsExactly<ArgumentException>(() => repository.StartInventory(next, latest + "x"));
        Assert.ThrowsExactly<ArgumentException>(() => repository.StartInventory(next, "bad\nstart"));
        Assert.AreEqual(next, repository.LastAttempt()); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }
}
