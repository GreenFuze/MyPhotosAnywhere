using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly string[] ReconciledHistoryCursors = ["fresh", "history-end", "final-end"];
    [TestMethod]
    public async Task ActualMetadataScanReplaysFailedHistoryBeforeDownloadsAndKeepsItsEndAsTheFinalFence()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var prepared = await fixture.PreparedAsync().ConfigureAwait(false);
        var context = new WorkspaceObservationContext(prepared.Plan.Scope.LibraryId, "permission-A", "metadata", 1, null);
        await fixture.Setup.Accounts.State.ExecuteAsync(database => new WorkspaceObservationRepository(database, fixture.Budget)
            .StartInventory(new WorkspaceObservationRepository(database, fixture.Budget).BeginScan(context), "earliest"), CancellationToken.None).ConfigureAwait(false);
        var present = false; var reads = 0; var cursors = new List<string>();
        fixture.Setup.Accounts.Readers.DocumentStart = (_, _) => Task.FromResult(new DriveChangeCursor("permission-A", null, "fresh"));
        fixture.Setup.Accounts.Readers.MetadataInventory = (_, _, _, _) => Task.FromResult(new DriveWorkspaceMetadataPage(
            present ? new([new("historical-policy", 1, 2, "application/json")]) : new([]), null, false));
        fixture.Setup.Accounts.Readers.MetadataRead = (_, candidate, _) =>
        { reads++; return Task.FromResult(new DriveWorkspaceObject("permission-A", candidate.FileId, "metadata", 1, "{}"u8.ToArray())); };
        fixture.Setup.Accounts.Readers.DocumentChanges = (cursor, _) =>
        {
            cursors.Add(cursor.Token);
            DriveObservedChange[] events = cursor.Token == "earliest" ? [new("file", "historical-policy", false, new("application/json", new(["metadata"]), false, 1, 2, true))] : [];
            return Task.FromResult(new DriveChangePage(new(events.ToList()), null, cursor with { Token = cursor.Token == "earliest" ? "history-end" : "final-end" }));
        };
        var source = new CatalogWorkspaceDocumentSource(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service, new("permission-A", "metadata"), null, 1, allMetadata: true);
        await using var scanner = new WorkspaceObservationService(fixture.Setup.Accounts.State, fixture.Budget, context, source);
        await Assert.ThrowsExactlyAsync<WorkspaceInventoryIncompleteException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, reads);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var repository = new WorkspaceObservationRepository(database, fixture.Budget);
            Assert.AreEqual("historical-policy", repository.KnownIdentities(context).Single()); Assert.IsNull(repository.LastAttempt()!.Completed);
            Assert.AreEqual("earliest", repository.LastAttempt()!.FirstUnresolvedCursor); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        present = true; cursors.Clear(); var verified = false;
        var complete = await scanner.ScanAsync((document, _) => { Assert.AreEqual("historical-policy", document.FileId); verified = true; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(verified); Assert.AreEqual(1, reads);
        CollectionAssert.AreEqual(ReconciledHistoryCursors, cursors);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var repository = new WorkspaceObservationRepository(database, fixture.Budget); repository.AssertCompletedScan(complete);
            Assert.AreEqual("earliest", repository.LastAttempt()!.FirstUnresolvedCursor); // Raw replay is not signed-head verification.
            Assert.AreEqual(1L, repository.KnownHistory(context).Single().Occurrences); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Allocations); Assert.AreEqual(0, fixture.Creates);
    }

    [TestMethod]
    public async Task ActualUnknownRemovalStaysClosedAfterANewerEmptyHistoryResponse()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync().ConfigureAwait(false); var prepared = await fixture.PreparedAsync().ConfigureAwait(false);
        var context = new WorkspaceObservationContext(prepared.Plan.Scope.LibraryId, "permission-A", "metadata", 1, null); var removed = true;
        fixture.Setup.Accounts.Readers.MetadataInventory = (_, _, _, _) => Task.FromResult(new DriveWorkspaceMetadataPage(new([]), null, false));
        fixture.Setup.Accounts.Readers.DocumentStart = (_, _) => Task.FromResult(new DriveChangeCursor("permission-A", null, "start"));
        var calls = 0;
        fixture.Setup.Accounts.Readers.DocumentChanges = (cursor, _) =>
        {
            calls++; return Task.FromResult(new DriveChangePage(new(removed && calls == 2 ? [new("file", "unknown-removal", true, null)] : []), null, cursor with { Token = "end" }));
        };
        var source = new CatalogWorkspaceDocumentSource(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service, new("permission-A", "metadata"), null, 1, allMetadata: true);
        await using var scanner = new WorkspaceObservationService(fixture.Setup.Accounts.State, fixture.Budget, context, source);
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryRecoveryRequiredException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        removed = false;
        await Assert.ThrowsExactlyAsync<WorkspaceHistoryRecoveryRequiredException>(() => scanner.ScanAsync((_, _) => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var repository = new WorkspaceObservationRepository(database, fixture.Budget); Assert.IsNull(repository.LastCheckpoint());
            Assert.IsTrue(repository.KnownHistory(context).Single().RecoveryRequired); Assert.IsNull(repository.LastAttempt()!.Completed); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Allocations); Assert.AreEqual(0, fixture.Creates);
    }

    [TestMethod]
    public async Task DisconnectDuringActualHistoryReadJoinsTheAccountAndNeverAcceptsItsLateEnd()
    {
        await using var fixture = new Fixture(); var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.DocumentChanges = async (cursor, _) =>
        { fixture.Readers.Started.TrySetResult(); await fixture.Readers.ReleaseRead.Task.ConfigureAwait(false); return new([], null, cursor with { Token = "late-end" }); };
        var source = new CatalogWorkspaceDocumentSource(fixture.State, fixture.Service, new("permission-A", "workspace-A"), null, account.Revision, allMetadata: true);
        var inventory = new WorkspaceDocumentInventory("permission-A", "workspace-A", null, new([]), new([]), new("permission-A", null, "listing-end"));
        var observer = new HistoryMemoryObserver(); var reading = source.ReplayHistoryAsync(inventory, new("permission-A", null, "retained"), observer, CancellationToken.None);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(account.PermissionId, account.Revision, CancellationToken.None).ConfigureAwait(false);
        var cleaning = fixture.Service.CleanCredentialsAsync(CancellationToken.None);
        try { Assert.IsFalse(cleaning.IsCompleted); Assert.AreEqual(0, fixture.Vault.Deleted.Count); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => reading).ConfigureAwait(false);
        Assert.AreEqual(1, await cleaning.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)); Assert.AreEqual(1, fixture.Readers.DisposalAttempts);
    }
    private sealed class HistoryMemoryObserver : IWorkspaceHistoryObserver
    {
        public Task PageAsync(WorkspaceHistoryPageEvidence page, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
