using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class JournaledProtocolSignerTests
{
    [TestMethod]
    public async Task ConcurrentRetriesRetainOneCounterAndExactlyTheFirstSealedEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.SignAsync(id))).ConfigureAwait(false);
        Assert.IsTrue(results.All(result => result.Sequence == 1));
        foreach (var result in results) CollectionAssert.AreEqual(results[0].Envelope!.Value.ToArray(), result.Envelope!.Value.ToArray());
        Assert.AreEqual(2L, (await fixture.SignAsync(Guid.NewGuid()).ConfigureAwait(false)).Sequence);
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task RestartReusesVaultIdentityAndCapturedSignatureInsteadOfResigning()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var id = Guid.NewGuid();
        var first = await fixture.SignAsync(id).ConfigureAwait(false);
        await fixture.Keys.DisposeAsync().ConfigureAwait(false);
        var pin = await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, fixture.Budget).Controller(), CancellationToken.None).ConfigureAwait(false);
        await using var recovered = new LibrarySigningIdentityOwner(fixture.Vault, fixture.Library, fixture.Database.InstallationId, pin);
        await recovered.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var replay = await new JournaledProtocolSigner(fixture.State, recovered, fixture.Budget)
            .SignControllerAsync(id, Fixture.Kind, Fixture.Body, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.BodySha256, replay.BodySha256); Assert.AreEqual(first.EnvelopeSha256, replay.EnvelopeSha256);
        CollectionAssert.AreEqual(first.Envelope!.Value.ToArray(), replay.Envelope!.Value.ToArray()); Assert.AreEqual(1, fixture.Vault.Writes);
    }

    [TestMethod]
    public async Task CancellationAfterReservationLeavesAnUnsealedIntentForTheSameIdOnly()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var id = Guid.NewGuid(); using var caller = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Signer.SignControllerAsync(id, Fixture.Kind, (identity, sequence) =>
        { caller.Cancel(); return Fixture.Body(identity, sequence); }, caller.Token)).ConfigureAwait(false);
        var pending = await fixture.ReadAsync(id).ConfigureAwait(false); Assert.IsNotNull(pending); Assert.IsNull(pending.Envelope); Assert.AreEqual(1L, pending.Sequence);
        var resumed = await fixture.SignAsync(id).ConfigureAwait(false); Assert.AreEqual(1L, resumed.Sequence); Assert.IsNotNull(resumed.Envelope);
        Assert.AreEqual(pending.BodySha256, resumed.BodySha256); Assert.AreEqual(2L, (await fixture.SignAsync(Guid.NewGuid()).ConfigureAwait(false)).Sequence);
    }

    [TestMethod]
    public async Task FailedSealKeepsReservedBytesAndCounterThenRetriesWithoutDuplicatingWork()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var id = Guid.NewGuid();
        fixture.Sql("CREATE TRIGGER fail_seal BEFORE UPDATE ON signing_intent BEGIN SELECT RAISE(ABORT,'synthetic interrupted seal'); END");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.SignAsync(id)).ConfigureAwait(false);
        var pending = await fixture.ReadAsync(id).ConfigureAwait(false); Assert.IsNotNull(pending); Assert.IsNull(pending.Envelope);
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes); fixture.Sql("DROP TRIGGER fail_seal");
        var resumed = await fixture.SignAsync(id).ConfigureAwait(false); Assert.AreEqual(1L, resumed.Sequence); Assert.AreEqual(pending.BodySha256, resumed.BodySha256);
    }

    [TestMethod]
    public async Task NoPinOrDifferentNativeOwnerCannotBootstrapOrSignImplicitly()
    {
        await using var fixture = await Fixture.CreateAsync(bootstrap: false).ConfigureAwait(false); var id = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.SignAsync(id)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(id).ConfigureAwait(false)); await fixture.BootstrapAsync().ConfigureAwait(false);
        await using var otherVault = new Vault(); await using var other = new LibrarySigningIdentityOwner(otherVault, fixture.Library, fixture.Database.InstallationId, null);
        await other.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var wrong = new JournaledProtocolSigner(fixture.State, other, fixture.Budget);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrong.SignControllerAsync(id, Fixture.Kind, Fixture.Body, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.ReadAsync(id).ConfigureAwait(false)); Assert.AreEqual(1L, (await fixture.SignAsync(id).ConfigureAwait(false)).Sequence);
    }

    [TestMethod]
    public async Task PublisherRoleIsSeparateAndCannotSignControllerOrHumanOperations()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false);
        var published = await fixture.Signer.SignCatalogAsync(Guid.NewGuid(), (identity, sequence) => Fixture.Body(identity, sequence, false), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, published.Sequence); Assert.AreEqual(fixture.Keys.PublicIdentity.PublisherId, published.ActorId);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => fixture.Signer.SignCatalogAsync(Guid.NewGuid(), Fixture.Body, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => fixture.Signer.SignControllerAsync(Guid.NewGuid(), "user-operation",
            (identity, sequence) => Fixture.Body(identity, sequence, true, "user-operation"), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1L, (await fixture.SignAsync(Guid.NewGuid()).ConfigureAwait(false)).Sequence);
    }

    private sealed class Vault : ICredentialVault
    {
        private byte[]? _bytes;
        public int Writes { get; private set; }
        public Task<SecretLease?> ReadAsync(string key, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(_bytes is null ? null : SecretLease.CopyFrom(_bytes)); }
        public Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (_bytes is not null) CryptographicOperations.ZeroMemory(_bytes); _bytes = bytes.ToArray(); Writes++; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken token) => throw new InvalidOperationException("Signing must not delete native keys.");
        public ValueTask DisposeAsync() { if (_bytes is not null) CryptographicOperations.ZeroMemory(_bytes); _bytes = null; return ValueTask.CompletedTask; }
    }
    private sealed class Space : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
    private sealed class Fixture : IAsyncDisposable
    {
        public const string Kind = "library-source-configuration";
        private readonly string _path = Path.Combine(Path.GetTempPath(), "mpa-journaled-signing-" + Guid.NewGuid().ToString("N"));
        public ProcessorStateDatabase Database { get; }
        public ProcessorStateCoordinator State { get; }
        public Guid Library { get; }
        public Vault Vault { get; } = new();
        public StorageBudgetManager Budget { get; } = new(new Space());
        public LibrarySigningIdentityOwner Keys { get; }
        public JournaledProtocolSigner Signer { get; }
        private Fixture()
        {
            Database = new(_path); State = new(Database); Library = new ProcessorLibraryRepository(Database).GetOrCreateIdentity();
            var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account", "Synthetic", null);
            Keys = new(Vault, Library, Database.InstallationId, null); Signer = new(State, Keys, Budget);
        }
        public static async Task<Fixture> CreateAsync(bool bootstrap = true)
        {
            var fixture = new Fixture();
            try { await fixture.Keys.InitializeAsync(CancellationToken.None).ConfigureAwait(false); if (bootstrap) await fixture.BootstrapAsync().ConfigureAwait(false); return fixture; }
            catch { await fixture.DisposeAsync().ConfigureAwait(false); throw; }
        }
        public Task<LibraryControllerPin> BootstrapAsync() => State.ExecuteAsync(database =>
        {
            var identity = Keys.PublicIdentity;
            return new LibraryAuthorityRepository(database, Budget).Bootstrap(identity.ControllerId, identity.ControllerEpoch, new("account", "metadata"), 1,
                Convert.FromBase64String(identity.ControllerPublicKeySpki), new(identity.PublisherId, identity.PublisherEpoch, identity.PublisherPublicKeySpki, identity.PublisherKeyId));
        }, CancellationToken.None);
        public Task<ProtocolSigningIntent> SignAsync(Guid id) => Signer.SignControllerAsync(id, Kind, Body, CancellationToken.None);
        public Task<ProtocolSigningIntent?> ReadAsync(Guid id) => State.ExecuteAsync(database => new ProtocolSigningJournal(database, Budget).Read(id), CancellationToken.None);
        public static byte[] Body(LibrarySigningPublicIdentity identity, long sequence) => Body(identity, sequence, true);
        public static byte[] Body(LibrarySigningPublicIdentity identity, long sequence, bool controller, string? kind = null) =>
            new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0, libraryId = identity.LibraryId.ToString("D"),
                actorId = (controller ? identity.ControllerId : identity.PublisherId).ToString("D"), epoch = "1", sequence = sequence.ToString(CultureInfo.InvariantCulture),
                kind = kind ?? (controller ? Kind : "catalog-commit"), requiredCapabilities = Array.Empty<string>(), payload = new { description = "Captured synthetic intent" } }));
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_path, "processor.db"), Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync()
        { await State.DisposeAsync().ConfigureAwait(false); await Keys.DisposeAsync().ConfigureAwait(false); await Vault.DisposeAsync().ConfigureAwait(false); Database.Dispose(); Directory.Delete(_path, true); }
    }
}
