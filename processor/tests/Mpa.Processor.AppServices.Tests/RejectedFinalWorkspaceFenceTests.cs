using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualFinalFenceRetainsAllObservedRemovalsAndAncestorChangesBeforeRejectingTheRead(bool removal)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        var changes = fixture.Setup.Accounts.Readers.DocumentChanges!; var calls = 0;
        fixture.Setup.Accounts.Readers.DocumentChanges = async (cursor, token) =>
        {
            var actual = await changes(cursor, token).ConfigureAwait(false);
            if (++calls != 3) return actual;
            DriveObservedChange[] observed = removal
                ? [new("file", "unknown-removal-one", true, null), new("file", "unknown-removal-two", true, null)]
                : [new("file", "possible-workspace-ancestor", false,
                    new("application/vnd.google-apps.folder", new(["outside-parent"]), false, 3, null, null))];
            return new(new(observed), null, actual.AtEnd);
        };
        var before = (fixture.Creates, fixture.Allocations);
        await using var current = CatalogReconciler(fixture);
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => current.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3, calls); Assert.IsNull(current.Current); Assert.AreEqual(before, (fixture.Creates, fixture.Allocations));
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new WorkspaceObservationRepository(database, fixture.Budget); var attempt = journal.LastAttempt()!;
            Assert.IsNull(attempt.Completed); Assert.IsFalse(attempt.HistoryReplay!.AtEnd); Assert.IsFalse(attempt.HistoryReplay.Active);
            var history = journal.KnownHistory(attempt.Context); Assert.AreEqual(removal ? 2 : 1, history.Count);
            Assert.IsTrue(history.All(item => item.RecoveryRequired == removal));
            Assert.ThrowsExactly<WorkspaceObservationConflictException>(() => journal.CompleteScan(attempt, "cannot-admit-rejected-read", journal.Known(attempt.Context)));
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        // A newer API response can coalesce the earlier records. It cannot erase
        // removals already seen. A folder change requires actual full refetch only.
        fixture.Setup.Accounts.Readers.DocumentChanges = changes;
        if (removal)
            await Assert.ThrowsExactlyAsync<WorkspaceHistoryRecoveryRequiredException>(() => current.ReconcileAsync(CancellationToken.None)).ConfigureAwait(false);
        else Assert.IsNotNull(await current.ReconcileAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(before, (fixture.Creates, fixture.Allocations)); Assert.AreEqual(2, fixture.Cloud.Count);
    }
}
