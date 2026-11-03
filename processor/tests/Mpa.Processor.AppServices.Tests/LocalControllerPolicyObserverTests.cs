using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class JournaledProtocolSignerTests
{
    private static readonly string[] ObservedPublisherCapabilities = [CatalogPublisherAuthorization.Capability];
    [TestMethod]
    public async Task RealNativeAndJournaledControllerPoliciesSelectMonotonicallyIncludingRevocation()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var observer = PolicyObserver(fixture); var sources = await fixture.Signer.SignInitialSourceConfigurationAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        var sourceObservation = await observer.ObserveAsync(PolicyRead(sources), CancellationToken.None).ConfigureAwait(false); Assert.AreEqual(1L, sourceObservation.SelectedSequence);
        var publisher = await fixture.Signer.SignControllerAsync(Guid.NewGuid(), "library-publisher-authorization", (identity, sequence) => ObservedPublisherBody(identity, sequence, false), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, (await observer.ObserveAsync(PolicyRead(publisher), CancellationToken.None).ConfigureAwait(false)).SelectedSequence);
        var revoked = await fixture.Signer.SignControllerAsync(Guid.NewGuid(), "library-publisher-authorization", (identity, sequence) => ObservedPublisherBody(identity, sequence, true), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3L, (await observer.ObserveAsync(PolicyRead(revoked), CancellationToken.None).ConfigureAwait(false)).SelectedSequence);
        var replay = await observer.ObserveAsync(PolicyRead(publisher), CancellationToken.None).ConfigureAwait(false); Assert.AreEqual(2L, replay.ObservedSequence); Assert.AreEqual(3L, replay.SelectedSequence);
        var current = await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).CurrentPublisherPolicy(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(current); Assert.IsNull(current.Publisher); Assert.AreEqual(revoked.EnvelopeSha256, current.EnvelopeSha256);
    }

    [TestMethod]
    public async Task ValidCloudPolicyAheadOfLocalHistoryCannotAdoptCounterOrSelectAuthority()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var body = ObservedPublisherBody(fixture.Keys.PublicIdentity, 1, false);
        var envelope = new SignedProtocolDocuments(ObservedPublisherCapabilities).Encode(body, fixture.Keys.SignControllerDocument(body));
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(() => PolicyObserver(fixture)
            .ObserveAsync(new("account", "unretained-policy", "metadata", 1, envelope), CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).CurrentPublisherPolicy(), CancellationToken.None).ConfigureAwait(false));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Database.StateDirectory, "processor.db"), Pooling = false }.ToString());
        connection.Open(); using var counter = connection.CreateCommand(); counter.CommandText = "SELECT sequence FROM signing_actor WHERE actor_id=$actor";
        counter.Parameters.AddWithValue("$actor", fixture.Keys.PublicIdentity.ControllerId.ToString("D")); Assert.AreEqual(0L, counter.ExecuteScalar());
    }

    [TestMethod]
    public async Task ValidPolicyWithOnlyAnUnsealedLocalIntentRequiresRecovery()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); var identity = fixture.Keys.PublicIdentity;
        var intent = await fixture.State.ExecuteAsync(database => new ProtocolSigningJournal(database, fixture.Budget).Reserve(Guid.NewGuid(), identity.ControllerId, identity.ControllerEpoch,
            "library-publisher-authorization", sequence => ObservedPublisherBody(identity, sequence, false)), CancellationToken.None).ConfigureAwait(false);
        var envelope = new SignedProtocolDocuments(ObservedPublisherCapabilities).Encode(intent.Body.Span, fixture.Keys.SignControllerDocument(intent.Body.Span));
        await Assert.ThrowsExactlyAsync<SigningHistoryRecoveryRequiredException>(() => PolicyObserver(fixture).ObserveAsync(new("account", "unsealed-policy", "metadata", 1, envelope), CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await fixture.ReadAsync(intent.Id).ConfigureAwait(false))!.Envelope);
        Assert.IsNull(await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).CurrentPublisherPolicy(), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PolicyWriteFailureAndRetiredAccessCannotTurnAnObservationIntoAcceptedAuthority()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var intent = await fixture.Signer.SignInitialSourceConfigurationAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false); var observer = PolicyObserver(fixture);
        fixture.Sql("CREATE TRIGGER fail_observed_policy BEFORE INSERT ON processor_meta WHEN NEW.key='controller_source_document_v1:1' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => observer.ObserveAsync(PolicyRead(intent), CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).CurrentSourceConfiguration(), CancellationToken.None).ConfigureAwait(false));
        fixture.Sql("DROP TRIGGER fail_observed_policy");
        await fixture.State.ExecuteAsync(database => { new DriveAccountRepository(database).Disconnect("account", 1); return true; }, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(() => observer.ObserveAsync(PolicyRead(intent), CancellationToken.None)).ConfigureAwait(false);
        var revision = await fixture.State.ExecuteAsync(database =>
        { var accounts = new DriveAccountRepository(database); var pending = accounts.Begin(Guid.NewGuid(), "account"); return accounts.Promote(pending.Id, "account", "Again", null).Revision; }, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(() => observer.ObserveAsync(PolicyRead(intent), CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1L, (await PolicyObserver(fixture, revision).ObserveAsync(PolicyRead(intent), CancellationToken.None).ConfigureAwait(false)).SelectedSequence);
    }

    private static LocalControllerPolicyObserver PolicyObserver(Fixture fixture, long revision = 1) => new(fixture.State, fixture.Budget, fixture.Keys, new(fixture.Library, "account", "metadata", revision, null));
    private static DriveWorkspaceObject PolicyRead(ProtocolSigningIntent intent) => new("account", "policy-" + intent.Id.ToString("N"), "metadata", 1, intent.Envelope!.Value);
    private static byte[] ObservedPublisherBody(LibrarySigningPublicIdentity identity, long sequence, bool revoked) => new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "mpa", major = 1, minor = 0, libraryId = identity.LibraryId.ToString("D"), actorId = identity.ControllerId.ToString("D"), epoch = identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
        sequence = sequence.ToString(CultureInfo.InvariantCulture), kind = "library-publisher-authorization", requiredCapabilities = ObservedPublisherCapabilities,
        payload = new { workspace = new { accountId = "account", folderId = "metadata" }, publisher = revoked ? null : new { actorId = identity.PublisherId.ToString("D"), epoch = identity.PublisherEpoch.ToString(CultureInfo.InvariantCulture), publicKeySpki = identity.PublisherPublicKeySpki, keyId = identity.PublisherKeyId } }
    }));
}
