using System.Security.Cryptography;
using Mpa.Processor.Credentials;
using Mpa.Processor.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Lan.Tests;

[TestClass]
public sealed class LanIdentityTests
{
    [TestMethod]
    public async Task SeparateTlsAndDocumentKeysSurviveRestartWithoutRekeying()
    {
        await using var vault = new Vault();
        var library = Guid.NewGuid(); var processor = Guid.NewGuid();
        string tlsPin; string signingPin; DocumentSignature signature;
        await using (var owner = new LanIdentityOwner(vault, library, processor))
        {
            await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(owner.Identity.TlsKeyId, owner.Identity.SigningKeyId);
            Assert.IsTrue(owner.Identity.ServerCertificate.HasPrivateKey);
            tlsPin = owner.Identity.TlsKeyId; signingPin = owner.Identity.SigningKeyId;
            signature = owner.Identity.SignDocument("{\"synthetic\":true}"u8);
            Assert.ThrowsExactly<InvalidOperationException>(() => owner.InitializeAsync(CancellationToken.None));
        }
        await using var reopened = new LanIdentityOwner(vault, library, processor);
        await reopened.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(tlsPin, reopened.Identity.TlsKeyId);
        Assert.AreEqual(signingPin, reopened.Identity.SigningKeyId);
        Assert.AreEqual(1, vault.Writes);
        using var verifier = new ProtocolDocumentVerifier(reopened.Identity.ExportSigningPublicKey());
        Assert.IsTrue(verifier.Verify("{\"synthetic\":true}"u8, signature));
    }

    [TestMethod]
    public async Task WrongLibraryAndCorruptRecordsFailWithoutReplacingProtectedIdentity()
    {
        await using var vault = new Vault();
        var library = Guid.NewGuid(); var processor = Guid.NewGuid();
        await using (var initial = new LanIdentityOwner(vault, library, processor)) await initial.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var originalHash = Convert.ToHexString(SHA256.HashData(vault.Bytes!));
        await using (var wrong = new LanIdentityOwner(vault, Guid.NewGuid(), processor))
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrong.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreSame(error, wrong.Failure);
        }
        Assert.AreEqual(originalHash, Convert.ToHexString(SHA256.HashData(vault.Bytes!)));
        vault.Bytes = vault.Bytes![..^1];
        var corruptHash = Convert.ToHexString(SHA256.HashData(vault.Bytes));
        await using (var broken = new LanIdentityOwner(vault, library, processor))
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => broken.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(corruptHash, Convert.ToHexString(SHA256.HashData(vault.Bytes)));
        Assert.AreEqual(1, vault.Writes);
    }

    [TestMethod]
    public async Task CertificateRenewalKeepsBothEstablishedPins()
    {
        await using var vault = new Vault();
        var library = Guid.NewGuid(); var processor = Guid.NewGuid();
        var clock = new Clock(DateTimeOffset.UtcNow);
        string tls; string signing;
        await using (var initial = new LanIdentityOwner(vault, library, processor, clock))
        {
            await initial.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            tls = initial.Identity.TlsKeyId; signing = initial.Identity.SigningKeyId;
        }
        clock.Now = clock.Now.AddYears(2).AddDays(-10);
        await using var renewed = new LanIdentityOwner(vault, library, processor, clock);
        await renewed.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(tls, renewed.Identity.TlsKeyId);
        Assert.AreEqual(signing, renewed.Identity.SigningKeyId);
        Assert.AreEqual(2, vault.Writes);
        Assert.IsTrue(new DateTimeOffset(renewed.Identity.ServerCertificate.NotAfter.ToUniversalTime()) > clock.Now.AddYears(1));
    }

    [TestMethod]
    public async Task ShutdownJoinsAnUnresponsiveVaultReadBeforeReleasingItsOwner()
    {
        await using var vault = new Vault { Hold = true };
        var owner = new LanIdentityOwner(vault, Guid.NewGuid(), Guid.NewGuid());
        var start = owner.InitializeAsync(CancellationToken.None);
        await vault.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stop = owner.DisposeAsync().AsTask();
        try { Assert.IsFalse(stop.IsCompleted); }
        finally { vault.Release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => start).ConfigureAwait(false);
        await stop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsNull(owner.Failure);
        Assert.AreEqual(0, vault.Writes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Identity);
        await owner.DisposeAsync().ConfigureAwait(false);
    }

    internal sealed class Vault : ICredentialVault
    {
        public byte[]? Bytes { get; set; }
        public int Writes { get; private set; }
        public bool Hold { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Hold) { Started.TrySetResult(); await Release.Task.ConfigureAwait(false); }
            return Bytes is null ? null : SecretLease.CopyFrom(Bytes);
        }
        public Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Writes++;
            if (Bytes is not null) CryptographicOperations.ZeroMemory(Bytes);
            Bytes = bytes.ToArray(); return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("Identity tests must never clear keys to recover.");
        public ValueTask DisposeAsync() { if (Bytes is not null) CryptographicOperations.ZeroMemory(Bytes); return ValueTask.CompletedTask; }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
