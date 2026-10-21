using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.WorkerRuntime;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class ParentLifetimeTests
{
    [TestMethod]
    public async Task ParentCloseIsObservedAndNormalGuardDisposalJoinsTheMonitor()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var parent = new ParentLifetimeListener();
        var accepting = parent.AcceptAsync(deadline.Token);
        await using var child = await ParentLifetimeGuard.ConnectAsync(parent.Configuration, deadline.Token).ConfigureAwait(false);
        await accepting.ConfigureAwait(false);
        Assert.IsFalse(child.Completion.IsCompleted);
        parent.Dispose();
        var end = await child.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
        Assert.IsTrue(end.ParentLost);
        Assert.AreEqual("parent-closed", end.Reason);

        using var normal = new ParentLifetimeListener();
        accepting = normal.AcceptAsync(deadline.Token);
        var orderly = await ParentLifetimeGuard.ConnectAsync(normal.Configuration, deadline.Token).ConfigureAwait(false);
        await accepting.ConfigureAwait(false);
        await Task.WhenAll(orderly.DisposeAsync().AsTask(), orderly.DisposeAsync().AsTask()).ConfigureAwait(false);
        Assert.IsFalse((await orderly.Completion.ConfigureAwait(false)).ParentLost);
    }

    [TestMethod]
    public async Task WrongLocalKeyDoesNotAcquireTheSessionAndListenerRemainsUsable()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var parent = new ParentLifetimeListener();
        var accepting = parent.AcceptAsync(deadline.Token);
        var wrong = new ParentLifetimeConfiguration(parent.Configuration.Port, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<IOException>(() => ParentLifetimeGuard.ConnectAsync(wrong, deadline.Token)).ConfigureAwait(false);
        await using var child = await ParentLifetimeGuard.ConnectAsync(parent.Configuration, deadline.Token).ConfigureAwait(false);
        await accepting.ConfigureAwait(false);
        Assert.IsFalse(child.Completion.IsCompleted);
        Assert.IsFalse(parent.Configuration.ToString().Contains(parent.Configuration.SharedKey, StringComparison.Ordinal));
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(parent.Configuration).Contains(parent.Configuration.SharedKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancellationDisposesUnacceptedListenerAndHandshakeIsBounded()
    {
        using var parent = new ParentLifetimeListener();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => parent.AcceptAsync(canceled.Token)).ConfigureAwait(false);
        using var socket = new TcpClient(AddressFamily.InterNetwork);
        await Assert.ThrowsAsync<SocketException>(async () => await socket.ConnectAsync(IPAddress.Loopback, parent.Configuration.Port).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DisposalDuringPartialHandshakeClosesPendingSocketWithoutWaitingForStartupDeadline()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var parent = new ParentLifetimeListener();
        var accepting = parent.AcceptAsync(deadline.Token);
        using var partial = new TcpClient(AddressFamily.InterNetwork);
        await partial.ConnectAsync(IPAddress.Loopback, parent.Configuration.Port, deadline.Token).ConfigureAwait(false);
        await partial.GetStream().WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
        parent.Dispose();
        var stopped = await Task.WhenAny(accepting, Task.Delay(TimeSpan.FromSeconds(2), deadline.Token)).ConfigureAwait(false);
        Assert.AreSame(accepting, stopped, "Disposal left an accepted handshake socket blocked.");
        await Assert.ThrowsAsync<Exception>(() => accepting).ConfigureAwait(false);
        try
        {
            var read = await partial.GetStream().ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(0, read);
        }
        catch (IOException) { /* OS reset and orderly EOF both prove the peer was closed. */ }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(31)]
    public async Task RepeatedShutdownAtAcceptAndPartialReadAlwaysClosesThePeer(int sentBytes)
    {
        for (var iteration = 0; iteration < 24; iteration++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var parent = new ParentLifetimeListener(); var accepting = parent.AcceptAsync(deadline.Token);
            using var peer = new TcpClient(AddressFamily.InterNetwork);
            await peer.ConnectAsync(IPAddress.Loopback, parent.Configuration.Port, deadline.Token).ConfigureAwait(false);
            if (sentBytes != 0) await peer.GetStream().WriteAsync(new byte[sentBytes], deadline.Token).ConfigureAwait(false);
            parent.Dispose();
            try { await accepting.WaitAsync(deadline.Token).ConfigureAwait(false); Assert.Fail("A partial handshake acquired a worker session."); }
            catch (ObjectDisposedException) { /* Parent closure before/after accept adoption. */ }
            catch (SocketException) { /* Closed acceptor/connection. */ }
            catch (IOException) { /* Closing an admitted partial stream aborts its read. */ }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { /* Closed asynchronous accept. */ }
            try
            {
                Assert.AreEqual(0, await peer.GetStream().ReadAsync(new byte[1], deadline.Token).ConfigureAwait(false), "Parent closure retained its peer.");
            }
            catch (IOException) { /* Reset is also an actual peer closure. */ }
        }
    }
}
