using System.IO.Pipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class LocalControlTests
{
    [TestMethod]
    public async Task LocalCommandsAreAcknowledgedAndStopCallbackRunsAfterReply()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<LocalProcessorCommand>();
        await using var server = new LocalProcessorControl(root.Path, (command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(new LocalProcessorReply(true, "ready", null));
        }, () => stopped.TrySetResult());
        foreach (var command in new[] { LocalProcessorCommand.Status, LocalProcessorCommand.Show, LocalProcessorCommand.Pause, LocalProcessorCommand.Resume, LocalProcessorCommand.Stop })
        {
            var reply = await LocalProcessorControl.TrySendAsync(root.Path, command, deadline.Token).ConfigureAwait(false);
            Assert.IsNotNull(reply); Assert.IsTrue(reply.Ok); Assert.AreEqual("ready", reply.State); Assert.IsNull(reply.Error);
        }
        await stopped.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(5, commands.Count);
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task MalformedClientCannotExecuteACommandAndNextClientStillWorks()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var called = 0;
        await using var server = new LocalProcessorControl(root.Path, (_, _) =>
        {
            Interlocked.Increment(ref called);
            return Task.FromResult(new LocalProcessorReply(true, "paused", null));
        });
        using (var malformed = new NamedPipeClientStream(".", LocalProcessorControl.PipeName(root.Path), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await malformed.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await malformed.WriteAsync(new byte[4], deadline.Token).ConfigureAwait(false);
            await malformed.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        var reply = await LocalProcessorControl.TrySendAsync(root.Path, LocalProcessorCommand.Status, deadline.Token).ConfigureAwait(false);
        Assert.IsNotNull(reply); Assert.IsTrue(reply.Ok);
        Assert.AreEqual(1, called);
        Assert.AreEqual(1, server.RejectedConnections);
    }

    [TestMethod]
    public async Task ImmediatelyDisconnectedClientsDoNotStopTheListener()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var calls = 0;
        await using var server = new LocalProcessorControl(root.Path, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new LocalProcessorReply(true, "ready", null));
        });
        for (var index = 0; index < 20; index++)
        {
            using (var client = new NamedPipeClientStream(".", LocalProcessorControl.PipeName(root.Path), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            { await client.ConnectAsync(deadline.Token).ConfigureAwait(false); }
            var reply = await LocalProcessorControl.TrySendAsync(root.Path, LocalProcessorCommand.Status, deadline.Token).ConfigureAwait(false);
            Assert.IsNotNull(reply); Assert.IsTrue(reply.Ok);
        }
        Assert.AreEqual(20, calls);
        Assert.AreEqual(20, server.RejectedConnections);
    }

    [TestMethod]
    public async Task UnavailableProcessorDoesNotLaunchOneOrCreateState()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        Assert.IsNull(await LocalProcessorControl.TrySendAsync(root.Path, LocalProcessorCommand.Status, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(root.Path).Count());
    }

    [TestMethod]
    public async Task UnexpectedHandlerIoFailureIsNotDisguisedAsAnInvalidClient()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new LocalProcessorControl(root.Path, (_, _) => throw new IOException("Synthetic internal storage failure"));
        await Assert.ThrowsAsync<IOException>(() => LocalProcessorControl.TrySendAsync(root.Path, LocalProcessorCommand.Status, deadline.Token)).ConfigureAwait(false);
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => server.Completion.WaitAsync(deadline.Token)).ConfigureAwait(false);
        Assert.AreEqual("Synthetic internal storage failure", failure.Message);
        Assert.AreEqual(0, server.RejectedConnections);
        await Assert.ThrowsExactlyAsync<IOException>(() => server.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ShutdownClosesAndJoinsAPartialLocalFrame()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = new LocalProcessorControl(root.Path, (_, _) => throw new InvalidOperationException("Partial input must not reach its handler."));
        using var client = new NamedPipeClientStream(".", LocalProcessorControl.PipeName(root.Path), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { 0, 0 }, deadline.Token).ConfigureAwait(false);
        await client.FlushAsync(deadline.Token).ConfigureAwait(false);
        await server.DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
        await server.Completion.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ShutdownKeepsTheActualPipeOwnedUntilAnAdmittedHandlerHasJoined()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalProcessorControl(root.Path, async (_, token) =>
        {
            using var registration = token.Register(() => stopping.TrySetResult());
            entered.TrySetResult(); await release.Task.ConfigureAwait(false);
            return new LocalProcessorReply(true, "ready", null);
        });
        using var client = new NamedPipeClientStream(".", LocalProcessorControl.PipeName(root.Path), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
        using var writer = new Mpa.Processor.Protocol.WorkerFrameWriter(client, 8192);
        await writer.WriteAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-control", major = 1,
            requestId = Guid.NewGuid().ToString("D"), command = "status" }), deadline.Token).ConfigureAwait(false);
        await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var disposal = server.DisposeAsync().AsTask();
        try
        {
            await stopping.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsFalse(disposal.IsCompleted); Assert.IsFalse(server.Completion.IsCompleted);
            var peerRead = client.ReadAsync(new byte[1], deadline.Token).AsTask();
            // A premature transport dispose would deliver EOF while the handler still owns it.
            await Assert.ThrowsAsync<TimeoutException>(() => peerRead.WaitAsync(TimeSpan.FromMilliseconds(150), deadline.Token)).ConfigureAwait(false);
            release.TrySetResult(); await disposal.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(0, await peerRead.WaitAsync(deadline.Token).ConfigureAwait(false));
        }
        finally { release.TrySetResult(); await disposal.WaitAsync(deadline.Token).ConfigureAwait(false); }
    }

    [TestMethod]
    public async Task RepeatedIdleAcceptShutdownJoinsBeforeReusingThePipeIdentity()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var iteration = 0; iteration < 40; iteration++)
        {
            await using var server = new LocalProcessorControl(root.Path, (_, _) => throw new InvalidOperationException("No client was admitted."));
            await server.DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            await server.Completion.ConfigureAwait(false);
        }
    }
}
