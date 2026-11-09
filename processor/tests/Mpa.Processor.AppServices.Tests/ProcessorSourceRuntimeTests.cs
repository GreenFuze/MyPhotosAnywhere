using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task ComposedSourcesStayPausedUntilHostActivationAndShareNativePauseResume()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false); var reads = 0;
        fixture.Readers.SourceRead = (_, _, _, _) => { reads++; return Task.FromResult(new DriveSourcePage([Photo("healthy")], [], null, false)); };
        await using var native = new ProcessorWorkSupervisor();
        await using var runtime = await ProcessorSourceRuntime.CreatePausedAsync(fixture.State, fixture.Service, native,
            new(new MutableStorage()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, reads); Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        await runtime.ActivateAsync(CancellationToken.None).ConfigureAwait(false);
        await UntilAsync(async () => (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count == 1).ConfigureAwait(false);
        await runtime.Work.PauseAsync().ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Paused, native.State); Assert.AreEqual(ProcessorWorkState.Paused, runtime.Work.State);
        await runtime.Work.ResumeAsync().ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Ready, native.State); Assert.IsNull(runtime.Failure);
        await runtime.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(runtime.ExitConfirmed);
        Assert.AreEqual(ProcessorWorkState.Ready, native.State); // Borrowed owner remains the composition root's responsibility.
    }

    [TestMethod]
    public async Task ComposedExitJoinsActualReadBeforeAccountsMayBeReleased()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Readers.SourceRead = async (_, _, _, token) =>
        {
            entered.TrySetResult(); using var registration = token.Register(() => canceled.TrySetResult());
            await release.Task.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return new([], [], null, false);
        };
        await using var native = new ProcessorWorkSupervisor();
        await using var runtime = await ProcessorSourceRuntime.CreatePausedAsync(fixture.State, fixture.Service, native,
            new(new MutableStorage()), CancellationToken.None).ConfigureAwait(false);
        await runtime.ActivateAsync(CancellationToken.None).ConfigureAwait(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var exit = runtime.DisposeAsync().AsTask();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(exit.IsCompleted); Assert.IsFalse(runtime.ExitConfirmed); Assert.AreEqual(0, fixture.Readers.DisposalAttempts);
        }
        finally { release.TrySetResult(); }
        await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(runtime.ExitConfirmed); Assert.IsNull(runtime.Failure);
        Assert.AreEqual(1, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Accounts.Count);
    }

    [TestMethod]
    public async Task ComposedInvariantFailureStopsOtherWorkAndRetainsAnUnsuccessfulExitWithoutLeakingOwners()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = (_, _, _, _) => Task.FromResult(new DriveSourcePage([Photo("same"), Photo("same")], [], null, false));
        await using var native = new ProcessorWorkSupervisor();
        await using var runtime = await ProcessorSourceRuntime.CreatePausedAsync(fixture.State, fixture.Service, native,
            new(new MutableStorage()), CancellationToken.None).ConfigureAwait(false);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Failed += error => failure.TrySetResult(error);
        await runtime.ActivateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsInstanceOfType<ArgumentException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        await runtime.DisposeAsync().ConfigureAwait(false);
        Assert.IsNotNull(runtime.Failure); Assert.IsTrue(runtime.ExitConfirmed); Assert.AreEqual(ProcessorWorkState.Stopped, native.State);
        Assert.AreEqual(1, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Accounts.Count);
    }

    [TestMethod]
    public async Task LowStorageDoesNotPreventOpeningTheHostAndDoesNotAdmitNewMetadata()
    {
        await using var fixture = new Fixture(); await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = (_, _, _, _) => throw new InvalidOperationException("No network read is authorized under this exhausted budget.");
        await using var native = new ProcessorWorkSupervisor();
        await using var runtime = await ProcessorSourceRuntime.CreatePausedAsync(fixture.State, fixture.Service, native,
            new(new MutableStorage { Available = 0 }), CancellationToken.None).ConfigureAwait(false);
        await runtime.ActivateAsync(CancellationToken.None).ConfigureAwait(false);
        await UntilAsync(() => Task.FromResult(runtime.StorageAttention is not null)).ConfigureAwait(false);
        Assert.IsNull(runtime.Failure); Assert.AreEqual(1, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Accounts.Count);
        await runtime.Work.PauseAsync().ConfigureAwait(false); Assert.AreEqual(ProcessorWorkState.Paused, runtime.Work.State);
        await runtime.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(runtime.ExitConfirmed);
    }

    [TestMethod]
    public async Task ExitJoinsAnAdmittedScanCommandEvenWhileItWaitsForTheStateOwner()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        await using var native = new ProcessorWorkSupervisor();
        await using var runtime = await ProcessorSourceRuntime.CreatePausedAsync(fixture.State, fixture.Service, native,
            new(new MutableStorage()), CancellationToken.None).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = fixture.State.ExecuteAsync(_ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic state owner was not released.");
            return true;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var request = new SourceScanRequest(Guid.NewGuid(), source, 1, 1);
        var command = runtime.RequestScanAsync(request, CancellationToken.None); var exit = runtime.DisposeAsync().AsTask();
        try { Assert.IsFalse(command.IsCompleted); Assert.IsFalse(exit.IsCompleted); Assert.IsFalse(runtime.ExitConfirmed); }
        finally { release.Set(); }
        await held.ConfigureAwait(false); Assert.IsTrue((await command.ConfigureAwait(false)).Created);
        await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsTrue(runtime.ExitConfirmed); Assert.AreEqual(1, (await SummaryAsync(fixture, request.RequestId).ConfigureAwait(false)).Queued);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => runtime.RequestScanAsync(request, CancellationToken.None)).ConfigureAwait(false);
    }
}
