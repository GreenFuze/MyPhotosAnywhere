using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Credentials.Tests;

[TestClass]
public sealed class CredentialOwnershipTests
{
    [TestMethod]
    public async Task ExitJoinsTheActualNativeOwnerAndQueuedCallsCannotRunAfterStop()
    {
        using var backend = new HeldBackend();
        var vault = new NativeCredentialVault(Guid.NewGuid(), backend);
        var read = vault.ReadAsync("held", CancellationToken.None);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var queued = vault.ReadAsync("queued", CancellationToken.None);
        var stopped = vault.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(stopped.IsCompleted);
            Assert.IsFalse(backend.Disposed);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => vault.ReadAsync("late", CancellationToken.None)).ConfigureAwait(false);
        }
        finally { backend.Release.Set(); }
        using var result = await read.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => queued).ConfigureAwait(false);
        Assert.IsTrue(backend.Disposed);
        Assert.AreEqual(1, backend.Calls);
        await vault.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CancelDoesNotAbandonNativeCallAndUnreturnedSecretIsCleared()
    {
        using var backend = new HeldBackend();
        await using var vault = new NativeCredentialVault(Guid.NewGuid(), backend);
        using var cancellation = new CancellationTokenSource();
        var read = vault.ReadAsync("held", cancellation.Token);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cancellation.Cancel();
        try { Assert.IsFalse(read.IsCompleted); Assert.IsFalse(backend.Disposed); }
        finally { backend.Release.Set(); }
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => read).ConfigureAwait(false);
        Assert.IsTrue(backend.Secret.All(value => value == 0));
    }

    [TestMethod]
    public async Task WritesOwnAndClearTheirCopyAndSecretDiagnosticSerializationIsEmpty()
    {
        using var backend = new HeldBackend();
        await using var vault = new NativeCredentialVault(Guid.NewGuid(), backend);
        var caller = new byte[] { 1, 2, 3 };
        await vault.WriteAsync("write", caller, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(backend.Written);
        Assert.IsTrue(backend.Written.All(value => value == 0));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, caller);
        using var lease = new SecretLease(new byte[] { 55, 56 });
        Assert.AreEqual("{}", JsonSerializer.Serialize(lease));
    }

    private sealed class HeldBackend : ISecretBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public byte[] Secret { get; } = [19, 23];
        public byte[]? Written { get; private set; }
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public byte[]? Read(string address, CancellationToken cancellationToken)
        {
            Calls++; Started.SetResult();
            // Deliberately emulate a native call that cannot immediately honor cancellation.
            if (!Release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None)) throw new TimeoutException("Native ownership test did not release its fixture.");
            return Secret;
        }
        public void Write(string address, byte[] bytes, CancellationToken cancellationToken) => Written = bytes;
        public void Delete(string address, CancellationToken cancellationToken) => throw new InvalidOperationException("Not used by this fixture.");
        public void Dispose() { Disposed = true; Release.Dispose(); }
    }
}
