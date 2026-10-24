using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class CatalogPreparationServiceTests
{
    [TestMethod]
    public async Task ConsumerUsesOnlyTheFrozenCopyAfterTheLiveDatabaseHasReleasedItsSnapshot()
    {
        await using var fixture = new Fixture(); FrozenCatalogSnapshot? borrowed = null;
        var count = await fixture.Service.RunAsync((snapshot, token) =>
        {
            token.ThrowIfCancellationRequested(); borrowed = snapshot; fixture.Database.Dispose();
            Assert.AreEqual("original", snapshot.ReadPage("", cancellationToken: token).Assets.Single().FileId); return Task.FromResult(snapshot.Count);
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, count); Assert.IsNotNull(borrowed); Assert.ThrowsExactly<ObjectDisposedException>(() => borrowed.ReadPage(""));
        await fixture.Service.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(fixture.Service.ExitConfirmed); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ExitCancelsAndJoinsTheActualConsumerBeforeReleasingGeneratedFiles(bool consumerChecksCancellation)
    {
        await using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Task? stopping = null;
        var run = fixture.Service.RunAsync(async (snapshot, token) =>
        { entered.TrySetResult(); await release.Task.ConfigureAwait(false); if (consumerChecksCancellation) token.ThrowIfCancellationRequested(); return snapshot.Count; }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = fixture.Service.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(fixture.Service.ExitConfirmed);
            Assert.AreEqual(1, Directory.GetFiles(fixture.CapturePath, "*.sqlite").Length);
            Assert.AreEqual(1, await fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ReadPage().Assets.Count, CancellationToken.None).ConfigureAwait(false));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => run).ConfigureAwait(false); Assert.IsNotNull(stopping); await stopping.ConfigureAwait(false);
        Assert.IsTrue(fixture.Service.ExitConfirmed); Assert.IsFalse(Directory.Exists(fixture.CapturePath));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => fixture.Service.RunAsync((_, _) => Task.FromResult(1), CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task QueuedConsumersSerializeAndEachCaptureGetsItsOwnObservation()
    {
        await using var fixture = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var secondEntered = false;
        var first = fixture.Service.RunAsync(async (snapshot, _) =>
        {
            entered.TrySetResult(); await release.Task.ConfigureAwait(false);
            await fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ApplyBatch(fixture.Source, 1, 1, [Fact("new")]), CancellationToken.None).ConfigureAwait(false);
            return snapshot.Count;
        }, CancellationToken.None);
        Task<long>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            second = fixture.Service.RunAsync((snapshot, _) => { secondEntered = true; return Task.FromResult(snapshot.Count); }, CancellationToken.None);
            Assert.IsFalse(secondEntered);
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual(1L, await first.ConfigureAwait(false)); Assert.IsNotNull(second); Assert.AreEqual(2L, await second.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task EarlyCancellationAndConsumerFailurePreserveSourceFactsAndAllowCleanExit()
    {
        await using var fixture = new Fixture(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.RunAsync((_, _) => Task.FromResult(1), canceled.Token)).ConfigureAwait(false);
        Assert.IsFalse(Directory.Exists(fixture.CapturePath));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.RunAsync<int>((_, _) => Task.FromException<int>(new InvalidOperationException("synthetic consumer failure")), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, Directory.GetFiles(fixture.CapturePath, "*.sqlite").Length); Assert.IsNull(fixture.Service.Failure);
        Assert.AreEqual(1L, await fixture.Service.RunAsync((snapshot, _) => Task.FromResult(snapshot.Count), CancellationToken.None).ConfigureAwait(false));
        await fixture.Service.DisposeAsync().ConfigureAwait(false); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        Assert.AreEqual(1, await fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ReadPage().Assets.Count, CancellationToken.None).ConfigureAwait(false));
    }

    private static DiscoveredDriveAsset Fact(string file) => new(file, 1, "Photo.jpg", "image/jpeg", 40, 30, 100, null, null, 0);
    private sealed class Space : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "mpa-catalog-preparation-" + Guid.NewGuid().ToString("N"));
        public string CapturePath => Path.Combine(_path, "catalog-preparations");
        public ProcessorStateDatabase Database { get; }
        public ProcessorStateCoordinator State { get; }
        public CatalogPreparationService Service { get; }
        public StorageBudgetManager Budget { get; } = new(new Space());
        public Guid Source { get; } = Guid.NewGuid();
        public Fixture()
        {
            Database = new(_path); State = new(Database); var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account", "Synthetic", null);
            new LibrarySourceRepository(Database).Add(Source, "account", 1, "photos", "Photos", true, 1); new DiscoveredCatalogRepository(Database).ApplyBatch(Source, 1, 1, [Fact("original")]);
            Service = new(State, _path, Database.InstallationId, Budget);
        }
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync().ConfigureAwait(false); await State.DisposeAsync().ConfigureAwait(false); Database.Dispose(); Directory.Delete(_path, recursive: true); }
    }
}
