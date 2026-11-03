using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LowSpaceCannotClaimOrFailAnExistingItemAndRestoredSpaceResumesItsCapturedWork()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var space = new MutableStorage(); var budget = new StorageBudgetManager(space);
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service, budget);
        var request = await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        space.Available = 0;
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(() => runner.RunNextAsync(CancellationToken.None)).ConfigureAwait(false);
        var waiting = await SummaryAsync(fixture, request.JobId).ConfigureAwait(false);
        Assert.AreEqual(1, waiting.Queued); Assert.AreEqual(0, waiting.Running); Assert.AreEqual(0, waiting.Failed);
        Assert.IsNull(runner.Failure); Assert.AreEqual(0L, budget.Status().ReservedBytes);
        space.Available = 20 * StorageBudgetManager.Gibibyte;
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        Assert.AreEqual(0L, budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task ConcurrentDiskUseAfterNetworkReadReturnsItsLeaseWithoutPublishingOrFailingHealthyMedia()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var space = new MutableStorage(); var budget = new StorageBudgetManager(space); var reads = 0;
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service, budget);
        var request = await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.SourceRead = (_, _, _, _) =>
        {
            reads++; space.Available = 0;
            return Task.FromResult(new DriveSourcePage([Photo("healthy")], [], null, false));
        };
        await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(() => runner.RunNextAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, reads); Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        var waiting = await SummaryAsync(fixture, request.JobId).ConfigureAwait(false);
        Assert.AreEqual(1, waiting.Queued); Assert.AreEqual(0, waiting.Running); Assert.AreEqual(0, waiting.Failed);
        Assert.IsNull(runner.Failure); Assert.AreEqual(0L, budget.Status().ReservedBytes);
        space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Readers.SourceRead = (_, _, _, _) => Task.FromResult(new DriveSourcePage([Photo("healthy")], [], null, false));
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
    }

    [TestMethod]
    public async Task BackgroundLowStorageIsVisibleAndPauseOrExitStillJoinsWithoutLosingWork()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false);
        var space = new MutableStorage { Available = 0 }; var budget = new StorageBudgetManager(space); var reads = 0;
        fixture.Readers.SourceRead = (_, _, _, _) => { reads++; return Task.FromResult(new DriveSourcePage([Photo("healthy")], [], null, false)); };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service, budget);
        await using var scheduler = new SourceDiscoveryScheduler(fixture.State, runner, pollInterval: TimeSpan.FromMinutes(1));
        await UntilAsync(() => Task.FromResult(scheduler.StorageAttention is not null)).ConfigureAwait(false);
        Assert.AreEqual(0, reads); Assert.IsNull(scheduler.Failure);
        await scheduler.PauseAsync().ConfigureAwait(false); Assert.AreEqual(ProcessorWorkState.Paused, scheduler.State);
        space.Available = 20 * StorageBudgetManager.Gibibyte;
        await scheduler.ResumeAsync().ConfigureAwait(false); scheduler.NotifySourcesChanged();
        await UntilAsync(async () => (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count == 1).ConfigureAwait(false);
        await scheduler.PauseAsync().ConfigureAwait(false);
        Assert.AreEqual(1, reads); Assert.IsNull(scheduler.StorageAttention); Assert.IsNull(scheduler.Failure);
        await scheduler.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(scheduler.ExitConfirmed);
        Assert.AreEqual(0L, budget.Status().ReservedBytes);
    }

    private sealed class MutableStorage : IStorageSpaceReader
    {
        private long _available = 20 * StorageBudgetManager.Gibibyte;
        public long Available { get => Interlocked.Read(ref _available); set => Interlocked.Exchange(ref _available, value); }
        public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, Available, 0);
    }
}
