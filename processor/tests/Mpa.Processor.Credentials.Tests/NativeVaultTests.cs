using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Credentials.Tests;

[TestClass]
public sealed class NativeVaultTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [TestMethod]
    public async Task RealOsVaultRoundTripOverwriteRestartAndTargetedRemoval()
    {
        var directory = FixtureDirectory();
        var installation = Guid.NewGuid();
        var first = RandomNumberGenerator.GetBytes(4193);
        var second = RandomNumberGenerator.GetBytes(973);
        await using (var vault = new NativeCredentialVault(installation, directory))
        {
            Assert.IsNull(await vault.ReadAsync("synthetic-one", None).ConfigureAwait(false));
            await vault.WriteAsync("synthetic-one", first, None).ConfigureAwait(false);
            await vault.WriteAsync("synthetic-two", second, None).ConfigureAwait(false);
            using var read = await vault.ReadAsync("synthetic-one", None).ConfigureAwait(false);
            Assert.IsNotNull(read);
            CollectionAssert.AreEqual(first, read.Bytes.ToArray());
            Assert.AreEqual("[protected credential]", read.ToString());
            await vault.WriteAsync("synthetic-one", second, None).ConfigureAwait(false);
        }
        await using (var reopened = new NativeCredentialVault(installation, directory))
        {
            using var read = await reopened.ReadAsync("synthetic-one", None).ConfigureAwait(false);
            Assert.IsNotNull(read);
            var memory = read.Bytes;
            CollectionAssert.AreEqual(second, memory.ToArray());
            read.Dispose(); read.Dispose();
            Assert.IsTrue(memory.Span.IndexOfAnyExcept((byte)0) < 0);
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = read.Bytes);
            await reopened.DeleteAsync("synthetic-one", None).ConfigureAwait(false);
            await reopened.DeleteAsync("synthetic-one", None).ConfigureAwait(false);
            Assert.IsNull(await reopened.ReadAsync("synthetic-one", None).ConfigureAwait(false));
            using var unrelated = await reopened.ReadAsync("synthetic-two", None).ConfigureAwait(false);
            Assert.IsNotNull(unrelated);
            CollectionAssert.AreEqual(second, unrelated.Bytes.ToArray());
            await reopened.DeleteAsync("synthetic-two", None).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.EnumerateFiles(directory, "*.pending").Any());
    }

    [TestMethod]
    public async Task ConcurrentCallsAreJoinedAndInstallationsRemainSeparate()
    {
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var directory = FixtureDirectory();
        await using var first = new NativeCredentialVault(firstId, directory);
        await using var second = new NativeCredentialVault(secondId, directory);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => first.WriteAsync("key-" + index,
            new byte[] { 0, (byte)index, 255 }, None))).ConfigureAwait(false);
        Assert.IsNull(await second.ReadAsync("key-0", None).ConfigureAwait(false));
        for (var index = 0; index < 12; index++)
        {
            using var read = await first.ReadAsync("key-" + index, None).ConfigureAwait(false);
            Assert.IsNotNull(read);
            CollectionAssert.AreEqual(new byte[] { 0, (byte)index, 255 }, read.Bytes.ToArray());
            await first.DeleteAsync("key-" + index, None).ConfigureAwait(false);
        }
        await first.DisposeAsync().ConfigureAwait(false);
        await first.DisposeAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => first.ReadAsync("key-0", None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task BoundsAndCancellationFailBeforeStoreMutation()
    {
        await using var vault = new NativeCredentialVault(Guid.NewGuid(), FixtureDirectory());
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => vault.WriteAsync("key", ReadOnlyMemory<byte>.Empty, None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => vault.WriteAsync("key", new byte[NativeCredentialVault.MaximumSecretBytes + 1], None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => vault.ReadAsync("bad\nkey", None)).ConfigureAwait(false);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => vault.WriteAsync("key", new byte[] { 3 }, canceled.Token)).ConfigureAwait(false);
        Assert.IsNull(await vault.ReadAsync("key", None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LargestBinaryRecordIncludingNullBytesSurvives()
    {
        await using var vault = new NativeCredentialVault(Guid.NewGuid(), FixtureDirectory());
        var bytes = RandomNumberGenerator.GetBytes(NativeCredentialVault.MaximumSecretBytes);
        bytes[0] = 0; bytes[^1] = 0;
        await vault.WriteAsync("largest", bytes, None).ConfigureAwait(false);
        using var read = await vault.ReadAsync("largest", None).ConfigureAwait(false);
        Assert.IsNotNull(read);
        CollectionAssert.AreEqual(bytes, read.Bytes.ToArray());
        await vault.DeleteAsync("largest", None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WindowsCiphertextCorruptionIsAnErrorAndOriginalRecordIsPreserved()
    {
        if (!OperatingSystem.IsWindows()) return; // A platform-specific assertion, not a skipped native vault suite.
        var directory = FixtureDirectory();
        await using var vault = new NativeCredentialVault(Guid.NewGuid(), directory);
        var bytes = RandomNumberGenerator.GetBytes(700);
        await vault.WriteAsync("corruption", bytes, None).ConfigureAwait(false);
        var path = Directory.GetFiles(directory, "*.dpapi").Single();
        var encrypted = await File.ReadAllBytesAsync(path, None).ConfigureAwait(false);
        Assert.IsTrue(encrypted.AsSpan().IndexOf(bytes) < 0);
        encrypted[^1] ^= 1;
        await File.WriteAllBytesAsync(path, encrypted, None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CredentialVaultException>(() => vault.ReadAsync("corruption", None)).ConfigureAwait(false);
        CollectionAssert.AreEqual(encrypted, await File.ReadAllBytesAsync(path, None).ConfigureAwait(false));
        await vault.DeleteAsync("corruption", None).ConfigureAwait(false);
    }

    private static string FixtureDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpa-vault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
