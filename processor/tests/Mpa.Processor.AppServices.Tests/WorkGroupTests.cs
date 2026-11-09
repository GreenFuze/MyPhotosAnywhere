using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class WorkGroupTests
{
    [TestMethod]
    public async Task SharedPauseJoinsBothOwnersAndSerializesAConcurrentResume()
    {
        var native = new ControlledWork(); var metadata = new ControlledWork { HoldPause = true }; await using var group = new ProcessorWorkGroup(native, metadata);
        var paused = group.PauseAsync(); await metadata.PauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var resume = group.ResumeAsync();
        try { Assert.IsFalse(paused.IsCompleted); Assert.IsFalse(resume.IsCompleted); Assert.AreEqual(ProcessorWorkState.Pausing, group.State); }
        finally { metadata.ReleasePause.TrySetResult(); }
        await paused.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); await resume.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Ready, group.State); Assert.AreEqual(1, native.ResumeCalls); Assert.AreEqual(1, metadata.ResumeCalls);
    }

    [TestMethod]
    public async Task SynchronousFailureStillStartsAndJoinsTheOtherOwner()
    {
        var native = new ControlledWork { PauseFailure = new IOException("Synthetic native pause failure") };
        var metadata = new ControlledWork { HoldPause = true }; await using var group = new ProcessorWorkGroup(native, metadata);
        var paused = group.PauseAsync(); await metadata.PauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try { Assert.IsFalse(paused.IsCompleted); }
        finally { metadata.ReleasePause.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<AggregateException>(() => paused).ConfigureAwait(false);
        Assert.AreEqual(ProcessorWorkState.Paused, metadata.State); Assert.AreEqual(ProcessorWorkState.Faulted, group.State); Assert.IsNotNull(group.Failure);
    }

    [TestMethod]
    public async Task FailedResumeQuiescesTheOwnerThatAlreadyResumedAndRetainsTheFailure()
    {
        var native = new ControlledWork(); var metadata = new ControlledWork(); await using var group = new ProcessorWorkGroup(native, metadata);
        await group.PauseAsync().ConfigureAwait(false); metadata.ResumeFailure = new IOException("Synthetic metadata resume failure");
        var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => group.ResumeAsync()).ConfigureAwait(false);
        Assert.IsTrue(error.Flatten().InnerExceptions.Any(error => error is IOException));
        Assert.AreEqual(ProcessorWorkState.Paused, native.State); Assert.AreEqual(ProcessorWorkState.Paused, metadata.State);
        Assert.AreEqual(2, native.PauseCalls); Assert.AreEqual(ProcessorWorkState.Faulted, group.State);
    }

    [TestMethod]
    public async Task RepeatedControlRequestsAreIdempotentAndComponentFaultsStayVisible()
    {
        var native = new ControlledWork(); var metadata = new ControlledWork(); await using var group = new ProcessorWorkGroup(native, metadata);
        await group.ResumeAsync().ConfigureAwait(false); Assert.AreEqual(0, native.ResumeCalls);
        await group.PauseAsync().ConfigureAwait(false); await group.PauseAsync().ConfigureAwait(false); Assert.AreEqual(1, native.PauseCalls);
        await group.ResumeAsync().ConfigureAwait(false); await group.ResumeAsync().ConfigureAwait(false); Assert.AreEqual(1, metadata.ResumeCalls);
        native.Failure = new IOException("Synthetic worker failure"); native.State = ProcessorWorkState.Faulted;
        Assert.AreEqual(ProcessorWorkState.Faulted, group.State); Assert.AreSame(native.Failure, group.Failure);
        await Assert.ThrowsExactlyAsync<ProcessorWorkStateConflictException>(() => group.ResumeAsync()).ConfigureAwait(false);
    }
    [TestMethod]
    public async Task ControlDisposalJoinsActiveTransitionsAndRejectsQueuedTransitionsBeforeReleasingItsGate()
    {
        var native = new ControlledWork(); var metadata = new ControlledWork { HoldPause = true };
        await using var group = new ProcessorWorkGroup(native, metadata);
        var pause = group.PauseAsync(); await metadata.PauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var queuedResume = group.ResumeAsync(); var exit = group.DisposeAsync().AsTask();
        try { Assert.IsFalse(exit.IsCompleted); }
        finally { metadata.ReleasePause.TrySetResult(); }
        await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => queuedResume).ConfigureAwait(false);
        await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(0, native.ResumeCalls); Assert.AreEqual(ProcessorWorkState.Paused, native.State);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => group.PauseAsync()).ConfigureAwait(false);
    }

    private sealed class ControlledWork : IProcessorWorkControl
    {
        public ProcessorWorkState State { get; set; } = ProcessorWorkState.Ready;
        public Exception? Failure { get; set; }
        public Exception? PauseFailure { get; set; }
        public Exception? ResumeFailure { get; set; }
        public bool HoldPause { get; set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public TaskCompletionSource PauseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PauseAsync()
        {
            PauseCalls++; if (PauseFailure is not null) throw PauseFailure;
            State = ProcessorWorkState.Pausing; PauseStarted.TrySetResult(); return JoinPauseAsync();
        }
        private async Task JoinPauseAsync()
        { if (HoldPause) await ReleasePause.Task.ConfigureAwait(false); State = ProcessorWorkState.Paused; }
        public Task ResumeAsync()
        {
            ResumeCalls++; if (ResumeFailure is not null) throw ResumeFailure;
            State = ProcessorWorkState.Ready; return Task.CompletedTask;
        }
    }
}
