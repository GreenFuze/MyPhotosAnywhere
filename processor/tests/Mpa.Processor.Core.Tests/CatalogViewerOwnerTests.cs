using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CatalogViewerSessionTests
{
    [TestMethod]
    public async Task BrowserScopedSessionsReplayIdempotentlyEnforceCapacityAndExpireWithoutChangingAnotherBrowser()
    {
        using var fixture = new Fixture(); fixture.Add(137); await using var state = new ProcessorStateCoordinator(fixture.Database); var time = new ViewerClock();
        await using var sessions = new CatalogViewerSessions(state, fixture.DirectoryPath, fixture.Database.InstallationId, fixture.Budget, time);
        var browser = Guid.NewGuid(); var other = Guid.NewGuid(); var selected = fixture.Catalog.ReadPage().Assets[20].Id;
        var request = new CatalogViewerRequest(Guid.NewGuid(), selected, null); var first = await sessions.OpenAsync(browser, request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(137L, first.Count); Assert.AreEqual(selected, first.Entries.Single(entry => entry.Position == first.Position).AssetId);
        await state.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ApplyBatch(fixture.Source, 1, 1, [Fact("newer", 999)]), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(137L, (await sessions.OpenAsync(browser, request, CancellationToken.None).ConfigureAwait(false)).Count);
        await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(() => sessions.ReadAsync(other, request.Id, first.Position, CancellationToken.None)).ConfigureAwait(false);
        await sessions.CloseAsync(other, request.Id, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(137L, (await sessions.ReadAsync(browser, request.Id, 100, CancellationToken.None).ConfigureAwait(false)).Count);
        var second = request with { Id = Guid.NewGuid() }; await sessions.OpenAsync(browser, second, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CatalogViewerCapacityException>(() => sessions.OpenAsync(browser, request with { Id = Guid.NewGuid() }, CancellationToken.None)).ConfigureAwait(false);
        var independent = await sessions.OpenAsync(other, request, CancellationToken.None).ConfigureAwait(false); Assert.AreEqual(138L, independent.Count);
        await sessions.CloseBrowserAsync(browser, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(138L, (await sessions.ReadAsync(other, request.Id, independent.Position, CancellationToken.None).ConfigureAwait(false)).Count);
        time.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsExactlyAsync<CatalogViewerUnavailableException>(() => sessions.ReadAsync(other, request.Id, independent.Position, CancellationToken.None)).ConfigureAwait(false);
        await sessions.OpenAsync(browser, request with { Id = Guid.NewGuid() }, CancellationToken.None).ConfigureAwait(false);
        await sessions.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(sessions.ExitConfirmed); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.DirectoryPath, "viewer-sessions")));
    }

    [TestMethod]
    public void ReadOnlySnapshotDoesNotBlockNewMetadataAndPreventsPrematureStateLeaseRelease()
    {
        using var fixture = new Fixture(); fixture.Add(10); var selected = fixture.Catalog.ReadPage().Assets[3].Id;
        using var snapshot = fixture.Database.OpenReadSnapshot();
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("newer", 999)]);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Database.Dispose());
        Assert.ThrowsExactly<IOException>(() => { using var duplicate = new ProcessorStateDatabase(fixture.DirectoryPath); });
        using var index = CatalogViewerSessionIndex.Capture(snapshot, fixture.IndexPath, selected, null, fixture.Budget, CancellationToken.None);
        Assert.AreEqual(10L, index.Count); Assert.AreEqual(11, fixture.Catalog.ReadPage().Assets.Count);
        index.Dispose(); snapshot.Dispose();
        using var another = fixture.Database.OpenReadSnapshot();
        using var current = CatalogViewerSessionIndex.Capture(another, fixture.IndexPath, selected, null, fixture.Budget, CancellationToken.None); Assert.AreEqual(11L, current.Count);
    }

    [TestMethod]
    public async Task ExitCancelsAndJoinsActualSnapshotWorkWhileOrdinaryStateReadsStayUsable()
    {
        using var fixture = new Fixture(); fixture.Add(1000); await using var state = new ProcessorStateCoordinator(fixture.Database);
        await using var sessions = new CatalogViewerSessions(state, fixture.DirectoryPath, fixture.Database.InstallationId, fixture.Budget);
        var selected = fixture.Catalog.ReadPage().Assets[3].Id; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(); var reads = 0;
        fixture.Space.Observed = () =>
        {
            if (++reads != 5) return; entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Synthetic snapshot write was not released.");
        };
        var opening = sessions.OpenAsync(Guid.NewGuid(), new(Guid.NewGuid(), selected, null), CancellationToken.None);
        Task? exit = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var facts = await state.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ReadPage(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(60, facts.Assets.Count); exit = sessions.DisposeAsync().AsTask(); Assert.IsFalse(exit.IsCompleted); Assert.IsFalse(sessions.ExitConfirmed);
        }
        finally { release.Set(); }
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => opening).ConfigureAwait(false);
        await (exit ?? throw new InvalidOperationException("Exit test did not reach its ownership check.")).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(sessions.ExitConfirmed); fixture.Space.Observed = null; Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.DirectoryPath, "viewer-sessions")));
    }

    [TestMethod]
    public void StaleDirectoryRecoveryValidatesTheWholeInventoryBeforeRemovingGeneratedFiles()
    {
        using var fixture = new Fixture(); var root = Path.Combine(fixture.DirectoryPath, "viewer-sessions"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "owner.json"), JsonSerializer.Serialize(new { format = "mpa-viewer-sessions", major = 1, installationId = fixture.Database.InstallationId.ToString("D") }));
        var generated = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N") + ".sqlite"); File.WriteAllText(generated, "synthetic stale generated metadata"); File.WriteAllText(generated + "-journal", "synthetic stale journal");
        var unknown = Path.Combine(root, "unrecognized.jpg"); File.WriteAllText(unknown, "synthetic preserved original");
        Assert.ThrowsExactly<InvalidDataException>(() => { using var owner = new CatalogSessionDirectory(fixture.DirectoryPath, fixture.Database.InstallationId); });
        Assert.AreEqual("synthetic stale generated metadata", File.ReadAllText(generated)); Assert.AreEqual("synthetic preserved original", File.ReadAllText(unknown));
        File.Delete(unknown);
        using var recovered = new CatalogSessionDirectory(fixture.DirectoryPath, fixture.Database.InstallationId);
        Assert.IsFalse(File.Exists(generated)); Assert.IsFalse(File.Exists(generated + "-journal")); Assert.IsFalse(File.Exists(recovered.NewIndexPath()));
        recovered.Dispose(); Assert.IsFalse(Directory.Exists(root));
    }
    private sealed class ViewerClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp = checked(_timestamp + (long)duration.TotalMilliseconds);
    }
}
