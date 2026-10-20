using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class WorkSupervisorTests
{
    [TestMethod]
    public async Task PauseAcknowledgementWaitsForWorkerExitAndAllOperationReadersAndReservations()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var budget = new StorageBudgetManager(new Space());
        await using var supervisor = new ProcessorWorkSupervisor();
        var operation = new Operation(await WorkerProcessTests.SpecAsync(root.Path, "ignore-eof").ConfigureAwait(false), root.Path, budget, holdDisposal: true);
        var work = supervisor.Start(operation, deadline.Token);
        await operation.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Running, supervisor.State);
        var pause = supervisor.PauseAsync();
        await operation.Releasing.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            Assert.IsTrue(operation.ObservedExitBeforeRelease);
            Assert.AreEqual(ProcessorWorkState.Pausing, supervisor.State);
            Assert.IsFalse(pause.IsCompleted);
            Assert.AreEqual(1024L, budget.Status().ReservedBytes);
            Assert.ThrowsExactly<IOException>(() => { using var blocked = File.Open(operation.InputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        }
        finally { operation.AllowRelease.TrySetResult(); }
        await pause.WaitAsync(deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => work).ConfigureAwait(false);
        Assert.AreEqual(0L, budget.Status().ReservedBytes);
        Assert.AreEqual(ProcessorWorkState.Paused, supervisor.State);
        supervisor.Resume();
        Assert.AreEqual(ProcessorWorkState.Ready, supervisor.State);
        await Task.WhenAll(supervisor.StopAsync(), supervisor.StopAsync()).ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Stopped, supervisor.State);
        using var original = File.Open(operation.InputPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.AreEqual(42, original.ReadByte());
    }

    [TestMethod]
    public async Task ExitDuringPauseJoinsTheSameOperationAndNeverStartsAnotherWorker()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var budget = new StorageBudgetManager(new Space());
        await using var supervisor = new ProcessorWorkSupervisor();
        var operation = new Operation(await WorkerProcessTests.SpecAsync(root.Path, "ignore-eof").ConfigureAwait(false), root.Path, budget, holdDisposal: true);
        var work = supervisor.Start(operation, deadline.Token);
        await operation.Started.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var pause = supervisor.PauseAsync();
        await operation.Releasing.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var stop = supervisor.StopAsync();
        try
        {
            Assert.AreEqual(ProcessorWorkState.Stopping, supervisor.State);
            Assert.IsFalse(stop.IsCompleted);
            Assert.ThrowsExactly<InvalidOperationException>(() => supervisor.Resume());
        }
        finally { operation.AllowRelease.TrySetResult(); }
        await Task.WhenAll(pause, stop).WaitAsync(deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => work).ConfigureAwait(false);
        Assert.AreEqual(1, operation.DisposeCount);
        Assert.AreEqual(0L, budget.Status().ReservedBytes);
        Assert.AreEqual(ProcessorWorkState.Stopped, supervisor.State);
    }

    [TestMethod]
    public async Task StartupFailureReleasesOperationButRemainsVisibleAndPreventsAutomaticRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        var budget = new StorageBudgetManager(new Space());
        await using var supervisor = new ProcessorWorkSupervisor();
        var spec = await WorkerProcessTests.SpecAsync(root.Path, "echo").ConfigureAwait(false);
        var operation = new Operation(spec with { ExecutableSha256 = new string('0', 64) }, root.Path, budget);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => supervisor.Start(operation)).ConfigureAwait(false);
        Assert.AreEqual(1, operation.DisposeCount);
        Assert.AreEqual(0L, budget.Status().ReservedBytes);
        Assert.AreEqual(ProcessorWorkState.Faulted, supervisor.State);
        Assert.IsInstanceOfType<InvalidDataException>(supervisor.Failure);
        Assert.ThrowsExactly<InvalidOperationException>(() => supervisor.Start(operation));
        await supervisor.StopAsync().ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Stopped, supervisor.State);
        Assert.IsNotNull(supervisor.Failure);
    }

    private sealed class Space : IStorageSpaceReader
    {
        public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 90 * StorageBudgetManager.Gibibyte, 0);
    }
    private sealed class Operation : OwnedWorkerOperation
    {
        private readonly FileStream _input;
        private readonly StorageReservation _reservation;
        private readonly bool _holdDisposal;
        private FramedWorkerProcess? _worker;
        public override WorkerLaunchSpec Launch { get; }
        public string InputPath { get; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Releasing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedExitBeforeRelease { get; private set; }
        public int DisposeCount { get; private set; }

        public Operation(WorkerLaunchSpec launch, string root, StorageBudgetManager budget, bool holdDisposal = false)
        {
            Launch = launch; _holdDisposal = holdDisposal;
            InputPath = Path.Combine(root, "operation-snapshot.bin");
            File.WriteAllBytes(InputPath, [42]);
            _reservation = budget.Reserve(1024);
            try { _input = File.Open(InputPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch { _reservation.Dispose(); throw; }
        }
        public override async Task ExecuteAsync(FramedWorkerProcess worker, CancellationToken cancellationToken)
        {
            _worker = worker;
            await using var frames = worker.ReadFramesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            if (!await frames.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("Synthetic worker did not start.");
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        public override async ValueTask DisposeAsync()
        {
            if (DisposeCount != 0) return;
            ObservedExitBeforeRelease = _worker is null || _worker.ExitConfirmed;
            if (!ObservedExitBeforeRelease) throw new InvalidOperationException("Operation reader was released before process exit.");
            Releasing.TrySetResult();
            if (_holdDisposal) await AllowRelease.Task.ConfigureAwait(false);
            try { _input.Dispose(); }
            finally { _reservation.Dispose(); }
            DisposeCount++;
        }
    }
}
