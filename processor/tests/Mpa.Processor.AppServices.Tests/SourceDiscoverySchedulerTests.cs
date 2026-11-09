using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task MetadataSchedulerWorksWithoutABrowserAndPausedReconciliationDoesNotRestartCompletedWork()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false); var reads = 0;
        fixture.Readers.SourceRead = (_, _, _, _) => { Interlocked.Increment(ref reads); return Task.FromResult(new DriveSourcePage([Photo("healthy")], [], null, false)); };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service);
        await using var scheduler = new SourceDiscoveryScheduler(fixture.State, runner, pollInterval: TimeSpan.FromMinutes(1));
        await UntilAsync(async () => (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count == 1).ConfigureAwait(false);
        await scheduler.PauseAsync().ConfigureAwait(false); Assert.AreEqual(ProcessorWorkState.Paused, scheduler.State);
        scheduler.NotifySourcesChanged(); await scheduler.PauseAsync().ConfigureAwait(false);
        Assert.AreEqual(1, reads); Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        await scheduler.ResumeAsync().ConfigureAwait(false);
        await scheduler.PauseAsync().ConfigureAwait(false); Assert.AreEqual(1, reads); Assert.IsNull(scheduler.Failure);
    }

    [TestMethod]
    public async Task SchedulerPauseAcknowledgesOnlyAfterTheActualCanceledReadAndLeaseHaveJoined()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Readers.SourceRead = async (_, _, _, token) =>
        {
            entered.TrySetResult(); using var registration = token.Register(() => canceled.TrySetResult());
            await release.Task.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return new([Photo("healthy")], [], null, false);
        };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var request = await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await using var scheduler = new SourceDiscoveryScheduler(fixture.State, runner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); var pausing = scheduler.PauseAsync();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); Assert.IsFalse(pausing.IsCompleted); Assert.AreEqual(ProcessorWorkState.Pausing, scheduler.State);
            Assert.AreEqual(1, (await SummaryAsync(fixture, request.JobId).ConfigureAwait(false)).Running);
        }
        finally { release.TrySetResult(); }
        await pausing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Paused, scheduler.State); Assert.AreEqual(0, (await SummaryAsync(fixture, request.JobId).ConfigureAwait(false)).Running);
        Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        await scheduler.ResumeAsync().ConfigureAwait(false);
        await UntilAsync(async () => (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count == 1).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SchedulerExitWaitsForTheActualNetworkOwnerAndRetainsRetryableWork()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Readers.SourceRead = async (_, _, _, token) =>
        {
            entered.TrySetResult(); using var registration = token.Register(() => canceled.TrySetResult());
            await release.Task.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return new([], [], null, false);
        };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var request = await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await using var scheduler = new SourceDiscoveryScheduler(fixture.State, runner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); var exit = scheduler.DisposeAsync().AsTask();
        try { await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); Assert.IsFalse(exit.IsCompleted); }
        finally { release.TrySetResult(); }
        await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); Assert.AreEqual(ProcessorWorkState.Stopped, scheduler.State);
        var summary = await SummaryAsync(fixture, request.JobId).ConfigureAwait(false); Assert.AreEqual(0, summary.Running); Assert.AreEqual(1, summary.Queued); Assert.AreEqual(0, summary.Failed);
    }

    [TestMethod]
    public async Task SchedulerInternalFailureIsVisibleAndRemainsAnUnsuccessfulExit()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = (_, _, _, _) => Task.FromResult(new DriveSourcePage([Photo("same"), Photo("same")], [], null, false));
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service);
        var scheduler = new SourceDiscoveryScheduler(fixture.State, runner);
        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.IsNotNull(scheduler.Failure); Assert.AreEqual(ProcessorWorkState.Faulted, scheduler.State);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scheduler.PauseAsync()).ConfigureAwait(false);
        }
        finally { await Assert.ThrowsExactlyAsync<ArgumentException>(() => scheduler.DisposeAsync().AsTask()).ConfigureAwait(false); }
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false))
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("The owned source scheduler did not reach its expected durable state.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
