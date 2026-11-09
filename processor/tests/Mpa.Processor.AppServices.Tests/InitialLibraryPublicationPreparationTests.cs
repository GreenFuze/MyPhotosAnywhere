using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class JournaledProtocolSignerTests
{
    [TestMethod]
    public async Task InitialOutboxPreparationIsConcurrentIdempotentAndNeverAllocatesOrUploadsDriveObjects()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var preparation = new InitialLibraryPublicationPreparation(fixture.State, fixture.Keys, fixture.Budget);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => preparation.PrepareAsync(CancellationToken.None))).ConfigureAwait(false); var first = results[0];
        Assert.IsTrue(results.All(result => result.Plan == first.Plan)); Assert.IsTrue(results.All(result => result.Publisher.Id == first.Publisher.Id && result.Sources.Id == first.Sources.Id));
        Assert.AreEqual("publisher-authorization", first.Publisher.Role); Assert.AreEqual("source-configuration", first.Sources.Role);
        Assert.AreEqual(first.Plan.PublisherEnvelopeSha256, first.Publisher.Sha256); Assert.AreEqual(first.Plan.SourceEnvelopeSha256, first.Sources.Sha256);
        Assert.IsNull(first.Publisher.FileId); Assert.IsNull(first.Sources.FileId); Assert.IsNull(first.Publisher.VerifiedVersion); Assert.IsNull(first.Sources.VerifiedVersion);
        Assert.AreEqual((2L, 2L), InitialPreparationCounts(fixture));
        var publisher = await fixture.ReadAsync(first.Plan.PublisherIntentId).ConfigureAwait(false); Assert.IsNotNull(publisher);
        CollectionAssert.AreEqual(publisher.Envelope!.Value.ToArray(), first.Publisher.Payload.ToArray());
    }

    [TestMethod]
    public async Task InitialOutboxRecoveryAfterLostReplyKeepsIdsAndRejectsRenewedAccessWithoutProof()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        var preparation = new InitialLibraryPublicationPreparation(fixture.State, fixture.Keys, fixture.Budget); var first = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        var recovered = await new InitialLibraryPublicationPreparation(fixture.State, fixture.Keys, fixture.Budget).PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Plan, recovered.Plan); CollectionAssert.AreEqual(first.Sources.Payload.ToArray(), recovered.Sources.Payload.ToArray());
        await fixture.State.ExecuteAsync(database =>
        {
            var accounts = new DriveAccountRepository(database); accounts.Disconnect("account", 1); var pending = accounts.Begin(Guid.NewGuid(), "account");
            return accounts.Promote(pending.Id, "account", "Reconnected", null);
        }, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(first.Plan, await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).InitialPublicationPlan(), CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual((2L, 2L), InitialPreparationCounts(fixture));
    }

    [TestMethod]
    public async Task InitialOutboxPartialFailureResumesOnlyTheMissingFixedObject()
    {
        await using var fixture = await Fixture.CreateAsync().ConfigureAwait(false); await fixture.BootstrapAsync(new(1, false)).ConfigureAwait(false);
        fixture.Sql("CREATE TRIGGER fail_source_object BEFORE INSERT ON workspace_publication WHEN NEW.role='source-configuration' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        var preparation = new InitialLibraryPublicationPreparation(fixture.State, fixture.Keys, fixture.Budget);
        await Assert.ThrowsExactlyAsync<SqliteException>(() => preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var plan = await fixture.State.ExecuteAsync(database => new LibraryAuthorityRepository(database).InitialPublicationPlan(), CancellationToken.None).ConfigureAwait(false); Assert.IsNotNull(plan);
        Assert.AreEqual((2L, 1L), InitialPreparationCounts(fixture));
        fixture.Sql("DROP TRIGGER fail_source_object"); var recovered = await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(plan, recovered.Plan); Assert.AreEqual(plan.PublisherObjectId, recovered.Publisher.Id); Assert.AreEqual(plan.SourceObjectId, recovered.Sources.Id);
        Assert.AreEqual((2L, 2L), InitialPreparationCounts(fixture));
    }

    private static (long Signing, long Outbox) InitialPreparationCounts(Fixture fixture)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.Database.StateDirectory, "processor.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT (SELECT count(*) FROM signing_intent),(SELECT count(*) FROM workspace_publication)";
        using var reader = command.ExecuteReader(); Assert.IsTrue(reader.Read()); return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
