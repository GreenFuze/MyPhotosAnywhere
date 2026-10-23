using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;
using Mpa.Processor.TestSupport;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class WorkerProcessTests
{
    [TestMethod]
    public async Task ResourceStopKeepsItsPrimaryFailureAndRetainsTheInterruptedFrameDiagnosticAfterJoining()
    {
        using var root = new WorkerTestDirectory(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var spec = await SpecAsync(root.Path, "partial-frame").ConfigureAwait(false);
        var worker = await FramedWorkerProcess.StartAsync(spec with { ResourcePolicy = new(1024L * 1024 * 1024, TimeSpan.FromSeconds(5)) }, deadline.Token).ConfigureAwait(false);
        try
        {
            var marker = Path.Combine(root.Path, "worker-partial-frame.marker");
            while (!File.Exists(marker))
            {
                if (worker.Completion.IsCompleted) await worker.Completion.ConfigureAwait(false);
                await Task.Delay(20, deadline.Token).ConfigureAwait(false);
            }
            var error = await Assert.ThrowsExactlyAsync<WorkerResourceLimitException>(() => worker.Completion.WaitAsync(deadline.Token)).ConfigureAwait(false);
            Assert.AreEqual("lifetime-milliseconds", error.Resource); Assert.IsTrue(worker.ExitConfirmed);
            var failure = worker.Failure; Assert.IsNotNull(failure); Assert.AreSame(error, failure.Primary);
            Assert.IsTrue(failure.Secondary.Any(secondary => secondary is ProtocolFormatException { InnerException: EndOfStreamException }));
            await using var frames = worker.ReadFramesAsync(deadline.Token).GetAsyncEnumerator();
            Assert.IsTrue(await frames.MoveNextAsync().ConfigureAwait(false)); // The complete hello preceding the partial frame remains readable.
            var channelFailure = await Assert.ThrowsExactlyAsync<WorkerResourceLimitException>(() => frames.MoveNextAsync().AsTask()).ConfigureAwait(false);
            Assert.AreSame(error, channelFailure);
            await Assert.ThrowsExactlyAsync<WorkerResourceLimitException>(() => worker.DisposeAsync().AsTask()).ConfigureAwait(false);
            Assert.AreSame(error, worker.Failure!.Primary);
            using var released = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.AreEqual(42, released.ReadByte());
        }
        finally
        {
            try { await worker.DisposeAsync().ConfigureAwait(false); }
            catch (WorkerResourceLimitException) { /* Expected failed worker; all owned tasks/process were still joined. */ }
        }
    }

    [TestMethod]
    public async Task ActualChildExchangesFramesAndConcurrentDisposalJoinsItAndReleasesTheReadLease()
    {
        using var root = new WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await SpecAsync(root.Path, "echo").ConfigureAwait(false);
        var worker = await FramedWorkerProcess.StartAsync(spec, deadline.Token).ConfigureAwait(false);
        await using var frames = worker.ReadFramesAsync(deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await frames.MoveNextAsync().ConfigureAwait(false));
        Assert.AreEqual("{\"type\":\"hello\"}", Encoding.UTF8.GetString(frames.Current));
        using (var observed = Process.GetProcessById(worker.ProcessId))
        {
            observed.Refresh();
            Assert.IsTrue(observed.WorkingSet64 > 0, "A running worker needs a real resident-memory measurement, not a zero placeholder.");
            Console.WriteLine("Actual managed worker resident bytes: " + observed.WorkingSet64);
        }
        Assert.ThrowsExactly<IOException>(() => { using var blocked = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None); });
        await worker.SendAsync("{\"photo\":\"synthetic\"}"u8.ToArray(), deadline.Token).ConfigureAwait(false);
        Assert.IsTrue(await frames.MoveNextAsync().ConfigureAwait(false));
        Assert.AreEqual("{\"photo\":\"synthetic\"}", Encoding.UTF8.GetString(frames.Current));
        await Task.WhenAll(worker.DisposeAsync().AsTask(), worker.DisposeAsync().AsTask()).ConfigureAwait(false);
        await worker.Completion.ConfigureAwait(false);
        using var released = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.AreEqual(42, released.ReadByte());
        Assert.ThrowsExactly<ObjectDisposedException>(() => worker.SendAsync("{}"u8.ToArray(), CancellationToken.None));
        Assert.AreEqual(0L, new FileInfo(spec.NewDiagnosticPath).Length);
    }

    [TestMethod]
    public async Task StubbornActualChildIsTerminatedBeforeDisposalReturns()
    {
        using var root = new WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var worker = await FramedWorkerProcess.StartAsync(await SpecAsync(root.Path, "ignore-eof").ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
        await using var frames = worker.ReadFramesAsync(deadline.Token).GetAsyncEnumerator();
        Assert.IsTrue(await frames.MoveNextAsync().ConfigureAwait(false));
        using var process = Process.GetProcessById(worker.ProcessId);
        await worker.DisposeAsync().ConfigureAwait(false);
        Assert.IsTrue(process.HasExited);
        using var released = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.AreEqual(42, released.ReadByte());
    }

    [TestMethod]
    public async Task UnexpectedExitAndMalformedFramesRemainFailuresAfterActualChildCleanup()
    {
        foreach (var scenario in new[] { "exit-error", "malformed" })
        {
            using var root = new WorkerTestDirectory();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var worker = await FramedWorkerProcess.StartAsync(await SpecAsync(root.Path, scenario).ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
            if (scenario == "exit-error")
            {
                await Assert.ThrowsExactlyAsync<WorkerProcessFailedException>(() => worker.Completion).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<WorkerProcessFailedException>(() => worker.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => worker.Completion).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => worker.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            using var released = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.AreEqual(42, released.ReadByte());
        }
    }

    [TestMethod]
    public async Task WrongExecutableHashNeverStartsTheFixture()
    {
        using var root = new WorkerTestDirectory();
        var spec = await SpecAsync(root.Path, "echo").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => FramedWorkerProcess.StartAsync(spec with { ExecutableSha256 = new string('0', 64) }, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "worker-started.marker")));
        CollectionAssert.AreEqual(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(root.Path, "worker-read-lease.bin")));
    }

    [TestMethod]
    public async Task ActualWorkerResourceStopsJoinTheProcessAndKeepItsInputIntact()
    {
        foreach (var resource in new[] { "resident-memory", "lifetime-milliseconds" })
        {
            using var root = new WorkerTestDirectory();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var policy = resource == "resident-memory"
                ? new WorkerResourcePolicy(16 * 1024 * 1024, TimeSpan.FromSeconds(10))
                : new WorkerResourcePolicy(1024L * 1024 * 1024, TimeSpan.FromMilliseconds(700));
            var spec = await SpecAsync(root.Path, "ignore-eof").ConfigureAwait(false);
            var worker = await FramedWorkerProcess.StartAsync(spec with { ResourcePolicy = policy }, deadline.Token).ConfigureAwait(false);
            using var child = Process.GetProcessById(worker.ProcessId);
            var error = await Assert.ThrowsExactlyAsync<WorkerResourceLimitException>(() => worker.Completion.WaitAsync(deadline.Token)).ConfigureAwait(false);
            Assert.AreEqual(resource, error.Resource);
            Assert.IsTrue(error.Observed >= error.Allowed);
            Assert.IsTrue(worker.ObservedPeakResidentBytes > 0);
            await Assert.ThrowsExactlyAsync<WorkerResourceLimitException>(() => worker.DisposeAsync().AsTask()).ConfigureAwait(false);
            Assert.IsTrue(child.HasExited);
            using var released = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.AreEqual(42, released.ReadByte());
        }
    }

    [TestMethod]
    public async Task InvalidResourcePolicyFailsBeforeLaunchingAnyChild()
    {
        using var root = new WorkerTestDirectory();
        var spec = await SpecAsync(root.Path, "echo").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => FramedWorkerProcess.StartAsync(
            spec with { ResourcePolicy = new(1, TimeSpan.FromSeconds(1)) }, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "worker-started.marker")));
        CollectionAssert.AreEqual(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(root.Path, "worker-read-lease.bin")));
    }

    [TestMethod]
    public async Task RepeatedRapidCleanExitsJoinWithoutMisclassifyingAddressSpaceTeardown()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        for (var run = 0; run < 20; run++)
        {
            using var root = new WorkerTestDirectory();
            var worker = await FramedWorkerProcess.StartAsync(await SpecAsync(root.Path, "exit-clean").ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
            await worker.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            await worker.DisposeAsync().ConfigureAwait(false);
            Assert.IsTrue(worker.ExitConfirmed);
            using var input = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.AreEqual(42, input.ReadByte());
        }
    }

    [TestMethod]
    public async Task ActualControllerCrashReleasesItsOtherwiseStubbornWorkerAndInputLease()
    {
        using var root = new WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spec = await SpecAsync(root.Path, "parent-launcher").ConfigureAwait(false);
        var start = new ProcessStartInfo(spec.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);
        using var parent = Process.Start(start) ?? throw new InvalidOperationException("Synthetic controller did not start.");
        var output = parent.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = parent.StandardError.ReadToEndAsync(deadline.Token);
        Process? child = null;
        var ownedChild = false;
        try
        {
            var markerPath = Path.Combine(root.Path, "parent-ready.json");
            while (!File.Exists(markerPath))
            {
                if (parent.HasExited) throw new InvalidOperationException("Synthetic controller exited before readiness: " + await errors.ConfigureAwait(false));
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(markerPath, deadline.Token).ConfigureAwait(false));
            child = Process.GetProcessById(marker.RootElement.GetProperty("childPid").GetInt32());
            Assert.AreEqual(marker.RootElement.GetProperty("instance").GetString(), TestProcessIdentity.Read(child));
            ownedChild = true;
            Assert.IsFalse(child.HasExited);
            parent.Kill(entireProcessTree: false); // Deliberate controller loss, not test cleanup of its child.
            await parent.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsTrue(child.HasExited);
            using var released = File.Open(Path.Combine(root.Path, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.AreEqual(42, released.ReadByte());
            Assert.AreEqual("", await output.ConfigureAwait(false));
            Assert.AreEqual("", await errors.ConfigureAwait(false));
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            if (ownedChild && child is not null && !child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            child?.Dispose();
        }
    }

    internal static async Task<WorkerLaunchSpec> SpecAsync(string root, string scenario)
    {
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        using var source = File.OpenRead(host);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(source).ConfigureAwait(false));
        var fixture = Path.Combine(AppContext.BaseDirectory, "WorkerFixture", "Mpa.Processor.TestWorkerHost.dll");
        if (!File.Exists(fixture)) throw new InvalidOperationException("Synthetic worker fixture was not built/copied.");
        // Originals exist before work starts. The synthetic child may hold/read this byte,
        // but must never create/write it or rely on surviving long enough to flush it.
        using (var input = new FileStream(Path.Combine(root, "worker-read-lease.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { input.WriteByte(42); input.Flush(flushToDisk: true); }
        return new(host, digest, [fixture, scenario, root], Path.GetDirectoryName(fixture)!, Path.Combine(root, "diagnostics-" + Guid.NewGuid().ToString("N") + ".log"));
    }

    internal sealed class WorkerTestDirectory : IDisposable
    {
        private readonly string _temporaryRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        public string Path { get; }
        public WorkerTestDirectory()
        {
            Path = System.IO.Path.Combine(_temporaryRoot, "MPA-Worker-Test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Path);
            if (System.IO.Path.GetDirectoryName(full) != _temporaryRoot || !System.IO.Path.GetFileName(full).StartsWith("MPA-Worker-Test-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(System.IO.Path.GetFileName(full)[16..], "N", out _)) throw new InvalidOperationException("Worker test cleanup escaped its owned directory.");
            Directory.Delete(full, recursive: true);
        }
    }
}
