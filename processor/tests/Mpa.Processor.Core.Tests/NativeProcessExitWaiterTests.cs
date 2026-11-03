using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class NativeProcessExitWaiterTests
{
    [TestMethod]
    public async Task ActualNativeWaitAndThreadJoinDoNotDependOnHeldEventDispatch()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await WorkerProcessTests.SpecAsync(root.Path, "native-exit-gate").ConfigureAwait(false);
        using var process = Launch(spec);
        var errors = process.StandardError.ReadToEndAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler blocked = (_, _) => { entered.TrySetResult(); release.Wait(); };
        Task? original = null; NativeProcessExitWaiter? native = null; Process? nativeProcess = null;
        try
        {
            Assert.AreEqual("native-exit-ready", await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false));
            process.Exited += blocked;
            original = process.WaitForExitAsync(); // This TEST owns and later joins the original waiter.
            nativeProcess = Process.GetProcessById(process.Id);
            native = new(nativeProcess); native.Start();
            await process.StandardInput.WriteLineAsync("exit").ConfigureAwait(false);
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await native.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsFalse(native.RetirementConfirmed, "A native signal alone cannot confirm actual thread retirement.");
            Assert.IsTrue(native.TryJoin(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(native.ThreadJoined); Assert.IsTrue(native.NativeSignalTicks > 0);
            Assert.IsTrue(nativeProcess.HasExited);
            Assert.IsFalse(original.IsCompleted, "The deliberate event barrier must still own the original async wait.");
            // Reproduce the old observable failure class through its unchanged ten-
            // second join budget, without canceling or abandoning that original task.
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => original.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token)).ConfigureAwait(false);
            Assert.IsFalse(original.IsCompleted);
            Assert.IsTrue(native.RetirementConfirmed);
        }
        finally
        {
            release.Set();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (original is not null) await original.ConfigureAwait(false);
            else process.WaitForExit();
            if (native?.Started == true) { try { await native.Completion.ConfigureAwait(false); } finally { native.Join(); } }
            process.Exited -= blocked; nativeProcess?.Dispose();
            _ = await errors.ConfigureAwait(false);
        }
        Assert.AreEqual("", await errors.ConfigureAwait(false));
        using var input = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.AreEqual(42, input.ReadByte());
    }

    [TestMethod]
    public async Task LiveNativeWaitCannotBeRetiredUntilTheActualChildAndWaitThreadExit()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await WorkerProcessTests.SpecAsync(root.Path, "native-exit-gate").ConfigureAwait(false);
        using var process = Launch(spec); var errors = process.StandardError.ReadToEndAsync();
        var native = new NativeProcessExitWaiter(process);
        try
        {
            native.Start();
            Assert.AreEqual("native-exit-ready", await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false));
            Assert.IsFalse(native.Completion.IsCompleted); Assert.IsFalse(native.TryJoin(TimeSpan.Zero)); Assert.IsFalse(native.ThreadJoined);
            Assert.ThrowsExactly<InvalidOperationException>(native.Start);
            process.Kill(entireProcessTree: true);
            await native.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(native.TryJoin(TimeSpan.FromSeconds(5))); Assert.IsTrue(native.ThreadJoined);
            Assert.IsTrue(native.TryJoin(TimeSpan.Zero)); Assert.IsFalse(process.EnableRaisingEvents);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (native.Started) { try { await native.Completion.ConfigureAwait(false); } finally { native.Join(); } }
            else process.WaitForExit();
            _ = await errors.ConfigureAwait(false);
        }
        Assert.AreEqual("", await errors.ConfigureAwait(false));
        using var input = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.AreEqual(42, input.ReadByte());
    }

    [TestMethod]
    public async Task AlreadyExitedNativeChildStillRequiresStartingAndJoiningItsOwnWaiter()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await WorkerProcessTests.SpecAsync(root.Path, "native-exit-gate").ConfigureAwait(false);
        using var process = Launch(spec); var errors = process.StandardError.ReadToEndAsync();
        NativeProcessExitWaiter? native = null;
        try
        {
            Assert.AreEqual("native-exit-ready", await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false));
            await process.StandardInput.WriteLineAsync("exit").ConfigureAwait(false);
            Assert.IsTrue(process.WaitForExit(5000));
            native = new NativeProcessExitWaiter(process);
            Assert.ThrowsExactly<InvalidOperationException>(() => native.TryJoin(TimeSpan.Zero));
            native.Start(); await native.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(native.TryJoin(TimeSpan.FromSeconds(5))); Assert.IsTrue(native.ThreadJoined);
            Assert.ThrowsExactly<InvalidOperationException>(native.Start);
            Assert.IsFalse(process.EnableRaisingEvents);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            if (native?.Started == true) { try { await native.Completion.ConfigureAwait(false); } finally { native.Join(); } }
            _ = await errors.ConfigureAwait(false);
        }
        Assert.AreEqual("", await errors.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task StartupCleanupWithoutAThreadRequiresARealNativeExitAndFencesLaterStart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await WorkerProcessTests.SpecAsync(root.Path, "native-exit-gate").ConfigureAwait(false);
        using var process = Launch(spec); var errors = process.StandardError.ReadToEndAsync();
        var native = new NativeProcessExitWaiter(process);
        try
        {
            Assert.AreEqual("native-exit-ready", await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false));
            Assert.IsFalse(native.CompleteUnstartedCleanup(TimeSpan.Zero));
            Assert.IsFalse(native.Completion.IsCompleted); Assert.IsFalse(native.RetirementConfirmed);
            Assert.IsFalse(native.ThreadJoined); Assert.IsFalse(native.Started);
            Assert.ThrowsExactly<InvalidOperationException>(native.Start);
            process.Kill(entireProcessTree: true);
            Assert.IsTrue(native.CompleteUnstartedCleanup(TimeSpan.FromSeconds(5)));
            await native.Completion.ConfigureAwait(false); native.Join();
            Assert.IsTrue(native.RetirementConfirmed); Assert.IsFalse(native.ThreadJoined);
            Assert.IsTrue(native.TryJoin(TimeSpan.Zero));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = native.CompleteUnstartedCleanup(Timeout.InfiniteTimeSpan);
            native.Join(); _ = await errors.ConfigureAwait(false);
        }
        Assert.AreEqual("", await errors.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NoThreadRecoveryReturnsAnOwnedPendingTaskAndJoinsAfterTheRealExit()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await WorkerProcessTests.SpecAsync(root.Path, "native-exit-gate").ConfigureAwait(false);
        using var process = Launch(spec); var errors = process.StandardError.ReadToEndAsync();
        var native = new NativeProcessExitWaiter(process);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? invocation = null; Task? recovery = null;
        try
        {
            Assert.AreEqual("native-exit-ready", await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false));
            // The isolated caller is owned even if a regression blocks it synchronously.
            invocation = Task.Run(() => returned.SetResult(native.WaitForRetirementAsync(Timeout.InfiniteTimeSpan)));
            recovery = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false);
            await invocation.ConfigureAwait(false);
            Assert.IsFalse(recovery.IsCompleted); Assert.IsFalse(native.Completion.IsCompleted);
            Assert.IsFalse(native.RetirementConfirmed); Assert.IsFalse(native.Started);
            Assert.ThrowsExactly<InvalidOperationException>(native.Start);
            await process.StandardInput.WriteLineAsync("exit").ConfigureAwait(false);
            await recovery.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(native.RetirementConfirmed); Assert.IsFalse(native.ThreadJoined);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (invocation is not null)
            {
                await invocation.ConfigureAwait(false);
                recovery ??= await returned.Task.ConfigureAwait(false);
                await recovery.ConfigureAwait(false);
            }
            else _ = native.CompleteUnstartedCleanup(Timeout.InfiniteTimeSpan);
            native.Join(); _ = await errors.ConfigureAwait(false);
        }
        Assert.AreEqual("", await errors.ConfigureAwait(false));
    }

    [TestMethod]
    public async Task NoThreadRetirementDeadlinePreservesItsLiveChildAndOriginalRecoveryIdentity()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await WorkerProcessTests.SpecAsync(root.Path, "native-exit-gate").ConfigureAwait(false);
        using var process = Launch(spec); var errors = process.StandardError.ReadToEndAsync();
        var native = new NativeProcessExitWaiter(process); Task? recovery = null;
        try
        {
            Assert.AreEqual("native-exit-ready", await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => native.WaitForRetirementAsync(TimeSpan.FromMilliseconds(200))).ConfigureAwait(false);
            Assert.IsFalse(process.HasExited); Assert.IsFalse(native.Completion.IsCompleted);
            Assert.IsFalse(native.RetirementConfirmed); Assert.IsFalse(native.Started);
            Assert.ThrowsExactly<IOException>(() => { using var probe = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None); });
            recovery = native.WaitForRetirementAsync(Timeout.InfiniteTimeSpan);
            Assert.IsFalse(recovery.IsCompleted);
            process.Kill(entireProcessTree: true);
            await recovery.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(native.RetirementConfirmed); Assert.IsFalse(native.ThreadJoined);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            recovery ??= native.WaitForRetirementAsync(Timeout.InfiniteTimeSpan);
            await recovery.ConfigureAwait(false); native.Join(); _ = await errors.ConfigureAwait(false);
        }
        Assert.AreEqual("", await errors.ConfigureAwait(false));
    }

    private static Process Launch(WorkerLaunchSpec spec)
    {
        var start = new ProcessStartInfo(spec.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = spec.InstalledWorkingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
        foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("The synthetic direct child did not start.");
    }
}
