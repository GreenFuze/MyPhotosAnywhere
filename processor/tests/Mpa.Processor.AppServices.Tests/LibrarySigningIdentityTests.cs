using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class LibrarySigningIdentityTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly Guid Installation = Guid.Parse("01f7d002-76e9-4bcf-a6c9-148240ee1834");
    private static LibrarySigningIdentityOwner Owner(Vault vault, LibraryControllerPin? pin = null) => new(vault, Library, Installation, pin);
    private static LibraryControllerPin Pin(LibrarySigningPublicIdentity identity) => new(identity.LibraryId, identity.ControllerId, identity.ControllerEpoch,
        "workspace-account", "workspace-folder", identity.ControllerPublicKeySpki, identity.ControllerKeyId);
    private static byte[] Body(LibrarySigningPublicIdentity identity, bool controller, string? kind = null, Guid? library = null) =>
        new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0, libraryId = (library ?? identity.LibraryId).ToString("D"),
            actorId = (controller ? identity.ControllerId : identity.PublisherId).ToString("D"), epoch = (controller ? identity.ControllerEpoch : identity.PublisherEpoch).ToString(System.Globalization.CultureInfo.InvariantCulture),
            sequence = "1", kind = kind ?? (controller ? "library-source-configuration" : "catalog-commit"), requiredCapabilities = Array.Empty<string>(), payload = new { accepted = true } }));

    [TestMethod]
    public async Task PersistedDistinctKeysAndActorsSurviveRestartWithoutAnotherWrite()
    {
        await using var vault = new Vault(); LibrarySigningPublicIdentity identity;
        await using (var owner = Owner(vault))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => owner.PublicIdentity);
            await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false); identity = owner.PublicIdentity;
            Assert.AreNotEqual(identity.ControllerId, identity.PublisherId); Assert.AreNotEqual(identity.ControllerKeyId, identity.PublisherKeyId);
            Assert.AreEqual(1, vault.Writes); Assert.IsTrue(vault.Submitted.Span.ToArray().All(b => b == 0));
            Assert.ThrowsExactly<InvalidOperationException>(() => owner.InitializeAsync(CancellationToken.None));
        }
        await using var restarted = Owner(vault, Pin(identity)); await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(identity, restarted.PublicIdentity); Assert.AreEqual(1, vault.Writes);
    }

    [TestMethod]
    public async Task MissingPinnedKeyNeverGeneratesOrSavesAReplacementRoot()
    {
        await using var vault = new Vault(); LibraryControllerPin pin;
        await using (var first = Owner(vault)) { await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false); pin = Pin(first.PublicIdentity); }
        vault.Erase(); var writes = vault.Writes; await using var owner = Owner(vault, pin);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => owner.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(error, owner.Failure); Assert.AreEqual(writes, vault.Writes);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.PublicIdentity);
    }

    [TestMethod]
    public async Task WrongPinAndMalformedPrivateRecordsFailWithoutMutatingSavedKeys()
    {
        await using var vault = new Vault(); LibrarySigningPublicIdentity identity;
        await using (var first = Owner(vault)) { await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false); identity = first.PublicIdentity; }
        var original = vault.Stored!.ToArray(); var digest = SHA256.HashData(original);
        await using (var wrong = Owner(vault, Pin(identity) with { ActorId = Guid.NewGuid() }))
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrong.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
        CollectionAssert.AreEqual(digest, SHA256.HashData(vault.Stored!)); Assert.AreEqual(1, vault.Writes);
        foreach (var damaged in new[] { new byte[] { 1 }, original[..^1], original.Concat(new byte[] { 0 }).ToArray(), new byte[1121] })
        {
            vault.Replace(damaged); await using var owner = Owner(vault, Pin(identity));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => owner.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1, vault.Writes); Assert.ThrowsExactly<InvalidOperationException>(() => owner.PublicIdentity);
        }
        CryptographicOperations.ZeroMemory(original);
    }

    [TestMethod]
    public async Task APrivateRecordFromAnotherInstallationOrLibraryIsNeverReused()
    {
        await using var vault = new Vault(); await using (var first = Owner(vault)) await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var context in new[] { (Guid.NewGuid(), Installation), (Library, Guid.NewGuid()) })
        {
            await using var owner = new LibrarySigningIdentityOwner(vault, context.Item1, context.Item2, null);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => owner.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1, vault.Writes);
        }
    }

    [TestMethod]
    public async Task EachNativeKeySignsOnlyItsOwnLibraryActorEpochAndRole()
    {
        await using var vault = new Vault(); await using var owner = Owner(vault); await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var identity = owner.PublicIdentity; var controller = Body(identity, true); var catalog = Body(identity, false);
        var controllerSignature = owner.SignControllerDocument(controller); var publisherSignature = owner.SignCatalogDocument(catalog);
        using var controllerVerifier = new ProtocolDocumentVerifier(Convert.FromBase64String(identity.ControllerPublicKeySpki));
        using var publisherVerifier = new ProtocolDocumentVerifier(Convert.FromBase64String(identity.PublisherPublicKeySpki));
        Assert.IsTrue(controllerVerifier.Verify(controller, controllerSignature)); Assert.IsTrue(publisherVerifier.Verify(catalog, publisherSignature));
        Assert.IsFalse(controllerVerifier.Verify(catalog, publisherSignature));
        Assert.ThrowsExactly<ProtocolFormatException>(() => owner.SignCatalogDocument(controller));
        Assert.ThrowsExactly<ProtocolFormatException>(() => owner.SignControllerDocument(catalog));
        Assert.ThrowsExactly<ProtocolFormatException>(() => owner.SignControllerDocument(Body(identity, true, "user-operation")));
        Assert.ThrowsExactly<ProtocolFormatException>(() => owner.SignCatalogDocument(Body(identity, false, library: Guid.NewGuid())));
        await owner.DisposeAsync().ConfigureAwait(false); Assert.ThrowsExactly<ObjectDisposedException>(() => owner.SignCatalogDocument(catalog));
    }

    [TestMethod]
    public async Task ExitAndCallerCancellationJoinAnUnresponsiveVaultWriteAndZeroItsBorrowedRecord()
    {
        foreach (var exit in new[] { false, true })
        {
            await using var vault = new Vault { HoldWrite = true }; await using var owner = Owner(vault); using var caller = new CancellationTokenSource();
            var initializing = owner.InitializeAsync(caller.Token); await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Task disposal = Task.CompletedTask;
            if (exit) disposal = owner.DisposeAsync().AsTask(); else await caller.CancelAsync().ConfigureAwait(false);
            await vault.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try { Assert.IsFalse(initializing.IsCompleted); if (exit) Assert.IsFalse(disposal.IsCompleted); Assert.IsTrue(vault.Submitted.Span.ContainsAnyExcept((byte)0)); }
            finally { vault.Release.TrySetResult(); }
            await Assert.ThrowsAsync<OperationCanceledException>(() => initializing).ConfigureAwait(false); await disposal.ConfigureAwait(false);
            Assert.IsTrue(vault.Submitted.Span.ToArray().All(b => b == 0));
            if (!exit) Assert.ThrowsExactly<InvalidOperationException>(() => owner.PublicIdentity);
            await using var recovered = Owner(vault); await recovered.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, vault.Writes); Assert.IsNotNull(recovered.PublicIdentity);
        }
    }

    [TestMethod]
    public async Task AReportedSuccessfulWriteWithoutExactReadbackCannotActivateControllerKeys()
    {
        await using var vault = new Vault { HideReadback = true }; await using var owner = Owner(vault);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => owner.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.ThrowsExactly<InvalidOperationException>(() => owner.PublicIdentity); Assert.IsNotNull(vault.Stored);
        Assert.IsTrue(vault.Submitted.Span.ToArray().All(b => b == 0)); vault.HideReadback = false;
        await using var recovered = Owner(vault); await recovered.InitializeAsync(CancellationToken.None).ConfigureAwait(false); Assert.AreEqual(1, vault.Writes);
    }

    [TestMethod]
    public async Task FailedVaultWriteNeverMakesKeysReadyOrClaimsRollback()
    {
        await using var vault = new Vault { FailAfterSaving = true }; await using var owner = Owner(vault);
        var error = await Assert.ThrowsExactlyAsync<CredentialVaultException>(() => owner.InitializeAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(error, owner.Failure); Assert.ThrowsExactly<InvalidOperationException>(() => owner.PublicIdentity);
        Assert.IsTrue(vault.Submitted.Span.ToArray().All(b => b == 0)); vault.FailAfterSaving = false;
        await using var recovered = Owner(vault); await recovered.InitializeAsync(CancellationToken.None).ConfigureAwait(false); Assert.AreEqual(1, vault.Writes);
    }

    [TestMethod]
    public async Task CallerCancellationIsObservedEvenBeforeItsLinkedCallbackCanRun()
    {
        await using var vault = new Vault { HoldWrite = true }; await using var owner = Owner(vault); using var caller = new CancellationTokenSource();
        var initializing = owner.InitializeAsync(caller.Token); await vault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var registration = caller.Token.Register(() => { entered.Set(); release.Wait(); });
        var canceling = Task.Run(() => caller.Cancel());
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5))); vault.Release.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(() => initializing.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            Assert.ThrowsExactly<InvalidOperationException>(() => owner.PublicIdentity);
        }
        finally { release.Set(); vault.Release.TrySetResult(); await canceling.ConfigureAwait(false); }
    }

    private sealed class Vault : ICredentialVault
    {
        public byte[]? Stored { get; private set; }
        public ReadOnlyMemory<byte> Submitted { get; private set; }
        public int Writes { get; private set; }
        public bool HoldWrite { get; init; }
        public bool FailAfterSaving { get; set; }
        public bool HideReadback { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Stored is null || HideReadback && Writes > 0 ? null : SecretLease.CopyFrom(Stored)); }
        public async Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Writes++; Submitted = bytes; using var registration = cancellationToken.Register(() => Canceled.TrySetResult()); Entered.TrySetResult();
            if (HoldWrite) await Release.Task.ConfigureAwait(false); // Deliberately ignores cancellation until native completion.
            Replace(bytes.Span); if (FailAfterSaving) throw new CredentialVaultException("Synthetic uncertain vault write");
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("Signing startup must never delete saved keys.");
        public void Replace(ReadOnlySpan<byte> value) { Erase(); Stored = value.ToArray(); }
        public void Erase() { if (Stored is not null) CryptographicOperations.ZeroMemory(Stored); Stored = null; }
        public ValueTask DisposeAsync() { Release.TrySetResult(); Erase(); return ValueTask.CompletedTask; }
    }
}
