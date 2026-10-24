using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void InitialPublicationFixesItsOriginalScopeAndBothOutboxIdentitiesAcrossRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); LibraryInitialPublicationPlan plan;
        using (var fixture = new Fixture(root.Path))
        {
            var sealedPolicies = fixture.SealInitialPolicies(); plan = fixture.Store.PrepareInitialPublication();
            Assert.AreEqual(plan, fixture.Store.PrepareInitialPublication()); Assert.AreEqual(sealedPolicies.Publisher.EnvelopeSha256, plan.PublisherEnvelopeSha256);
            Assert.AreEqual(sealedPolicies.Sources.EnvelopeSha256, plan.SourceEnvelopeSha256); Assert.AreEqual(sealedPolicies.Signing.PublisherId, plan.Scope.ActorId);
            Assert.AreNotEqual(plan.PublisherObjectId, plan.SourceObjectId); Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM workspace_publication"));
        }
        using var database = new ProcessorStateDatabase(root.Path); var store = new LibraryAuthorityRepository(database, new(new Space()));
        Assert.AreEqual(plan, store.InitialPublicationPlan()); Assert.AreEqual(plan, store.PrepareInitialPublication());
    }

    [TestMethod]
    public void ReconnectRetainsOldUploadScopeAndNeverGrantsItNewCredentialAccess()
    {
        using var fixture = new Fixture(); fixture.SealInitialPolicies(); var before = fixture.Store.PrepareInitialPublication();
        var accounts = new DriveAccountRepository(fixture.Database); accounts.Disconnect("account-A", 1);
        Assert.AreEqual(before, fixture.Store.InitialPublicationPlan());
        var pending = accounts.Begin(Guid.NewGuid(), "account-A"); var account = accounts.Promote(pending.Id, "account-A", "Again", null);
        Assert.AreEqual(before, fixture.Store.PrepareInitialPublication()); Assert.AreNotEqual(account.Revision, before.Scope.AccountRevision);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertLocalPublicationScope(before.Scope));
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM workspace_publication"));
    }

    [TestMethod]
    public void MissingPublicationPlanWithExistingOutboxCannotMintReplacementContext()
    {
        using var fixture = new Fixture(); var sealedPolicies = fixture.SealInitialPolicies(); var plan = fixture.Store.PrepareInitialPublication();
        var record = new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Prepare(plan.SourceObjectId, plan.Scope,
            "source-configuration", "application/json", sealedPolicies.Sources.Envelope!.Value.Span);
        fixture.Sql("DELETE FROM processor_meta WHERE key IN('initial_library_publication_v1','initial_library_publication_v1:sha256')");
        Assert.ThrowsExactly<LibraryInitialPublicationConflictException>(() => fixture.Store.PrepareInitialPublication());
        var retained = new WorkspacePublicationRepository(fixture.Database, fixture.Budget).Read(record.Id); Assert.IsNotNull(retained);
        Assert.AreEqual(record.Id, retained.Id); Assert.AreEqual(record.Scope, retained.Scope); Assert.AreEqual(record.Role, retained.Role); Assert.AreEqual(record.Sha256, retained.Sha256);
        CollectionAssert.AreEqual(record.Payload.ToArray(), retained.Payload.ToArray()); Assert.IsNull(fixture.Store.InitialPublicationPlan());
    }

    [TestMethod]
    public void InitialPublicationRequiresActuallySealedAndSelectedOwnPolicyHistory()
    {
        using var fixture = new Fixture(); var registration = fixture.PrepareInitialPolicies(); fixture.Store.PrepareInitialPolicyPlan(registration);
        Assert.ThrowsExactly<CatalogPublicationNotAuthorizedException>(() => fixture.Store.PrepareInitialPublication());
        fixture.Store.SelectPublisherPolicy(fixture.Policy(1)); fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(2, 2, sourceRevision: 1));
        Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.PrepareInitialPublication());
        Assert.IsNull(fixture.Store.InitialPublicationPlan()); Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM workspace_publication"));
    }

    [TestMethod]
    public void InitialPublicationCheckpointIsAtomicAndCorruptContextCannotBeReplaced()
    {
        using var fixture = new Fixture(); fixture.SealInitialPolicies(); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Store.PrepareInitialPublication()); Assert.IsNull(fixture.Store.InitialPublicationPlan());
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_initial_publication BEFORE INSERT ON processor_meta WHEN NEW.key='initial_library_publication_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Store.PrepareInitialPublication()); Assert.IsNull(fixture.Store.InitialPublicationPlan());
        fixture.Sql("DROP TRIGGER fail_initial_publication"); fixture.Store.PrepareInitialPublication();
        fixture.Sql("UPDATE processor_meta SET value='corrupt' WHERE key='initial_library_publication_v1:sha256'");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.InitialPublicationPlan()); Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.PrepareInitialPublication());
        Assert.AreEqual(2L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
    }

    private sealed partial class Fixture
    {
        public (LibraryInitialPolicyPlan Signing, ProtocolSigningIntent Publisher, ProtocolSigningIntent Sources) SealInitialPolicies()
        {
            var registration = PrepareInitialPolicies(); var plan = Store.PrepareInitialPolicyPlan(registration); var journal = new ProtocolSigningJournal(Database, Budget);
            var publisherBody = InitialPolicyBody(Policy(1)); var sourceBody = InitialPolicyBody(SourcePolicy(2, 2, sourceRevision: 1));
            var publisher = journal.Reserve(plan.PublisherIntentId, ActorId, 1, "library-publisher-authorization", _ => publisherBody);
            publisher = journal.Seal(publisher.Id, publisher.BodySha256, new ProtocolDocumentSigner(Controller).Sign(publisher.Body.Span)); Store.SelectPublisherPolicy(publisher.Envelope!.Value.Span);
            var sources = journal.Reserve(plan.SourceIntentId, ActorId, 1, "library-source-configuration", _ => sourceBody);
            sources = journal.Seal(sources.Id, sources.BodySha256, new ProtocolDocumentSigner(Controller).Sign(sources.Body.Span)); Store.SelectSourceConfiguration(sources.Envelope!.Value.Span);
            return (plan, publisher, sources);
        }
    }
}
