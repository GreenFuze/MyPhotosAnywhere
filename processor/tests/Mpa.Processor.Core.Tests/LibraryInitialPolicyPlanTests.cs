using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void InitialPolicyPlanFixesBothIntentIdentitiesBeforeSigningAndSurvivesRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); LibraryInitialPolicyPlan first; PublisherSigningRegistration registration;
        using (var fixture = new Fixture(root.Path))
        {
            registration = fixture.PrepareInitialPolicies(); first = fixture.Store.PrepareInitialPolicyPlan(registration);
            Assert.AreEqual(first, fixture.Store.PrepareInitialPolicyPlan(registration)); Assert.AreNotEqual(Guid.Empty, first.PublisherIntentId); Assert.AreNotEqual(first.PublisherIntentId, first.SourceIntentId);
            Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
            Assert.AreEqual(first, fixture.Store.AssertInitialPolicyIntentCurrent(first.PublisherIntentId, "library-publisher-authorization", registration));
            Assert.AreEqual(first, fixture.Store.AssertInitialPolicyIntentCurrent(first.SourceIntentId, "library-source-configuration", registration));
            Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.AssertInitialPolicyIntentCurrent(Guid.NewGuid(), "library-publisher-authorization", registration));
        }
        using var database = new ProcessorStateDatabase(root.Path); var authority = new LibraryAuthorityRepository(database, new(new Space()));
        Assert.AreEqual(first, authority.InitialPolicyPlan()); Assert.AreEqual(first, authority.PrepareInitialPolicyPlan(registration));
    }

    [TestMethod]
    public void ChangedSourcesCannotReplaceAReviewedInitialPlanOrCreateNewIntents()
    {
        using var fixture = new Fixture(); var registration = fixture.PrepareInitialPolicies(); var first = fixture.Store.PrepareInitialPolicyPlan(registration);
        fixture.DisablePublicationSource();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.PrepareInitialPolicyPlan(registration));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertInitialPolicyIntentCurrent(first.PublisherIntentId, "library-publisher-authorization", registration));
        Assert.AreEqual(first, fixture.Store.InitialPolicyPlan()); Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
    }

    [TestMethod]
    public void MissingPlanWithRetainedSigningWorkCannotManufactureFreshSetupIdentities()
    {
        using var fixture = new Fixture(); var registration = fixture.PrepareInitialPolicies(); var first = fixture.Store.PrepareInitialPolicyPlan(registration);
        var body = InitialPolicyBody(fixture.Policy(1));
        var retained = new ProtocolSigningJournal(fixture.Database, fixture.Budget).Reserve(first.PublisherIntentId, fixture.ActorId, 1, "library-publisher-authorization", _ => body);
        fixture.Sql("DELETE FROM processor_meta WHERE key IN('initial_library_policies_v1','initial_library_policies_v1:sha256')");
        Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.PrepareInitialPolicyPlan(registration));
        Assert.IsNull(fixture.Store.InitialPolicyPlan()); Assert.IsNotNull(new ProtocolSigningJournal(fixture.Database, fixture.Budget).Read(retained.Id));
    }

    [TestMethod]
    public void SupersedingRevocationRetiresBothOldSetupSigningPaths()
    {
        using var fixture = new Fixture(); var registration = fixture.PrepareInitialPolicies(); var first = fixture.Store.PrepareInitialPolicyPlan(registration);
        var journal = new ProtocolSigningJournal(fixture.Database, fixture.Budget); var body = InitialPolicyBody(fixture.Policy(1));
        var intent = journal.Reserve(first.PublisherIntentId, fixture.ActorId, 1, "library-publisher-authorization", _ => body);
        intent = journal.Seal(intent.Id, intent.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(intent.Body.Span)); fixture.Store.SelectPublisherPolicy(intent.Envelope!.Value.Span);
        Assert.AreEqual(first, fixture.Store.AssertInitialPolicyIntentCurrent(first.PublisherIntentId, "library-publisher-authorization", registration));
        var revocationBody = InitialPolicyBody(fixture.Policy(2, revoked: true));
        var revoked = journal.Reserve(Guid.NewGuid(), fixture.ActorId, 1, "library-publisher-authorization", _ => revocationBody);
        revoked = journal.Seal(revoked.Id, revoked.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(revoked.Body.Span)); fixture.Store.SelectPublisherPolicy(revoked.Envelope!.Value.Span);
        Assert.AreEqual(first, fixture.Store.PrepareInitialPolicyPlan(registration)); // Historical inspection cannot grant signing rights.
        Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.AssertInitialPolicyIntentCurrent(first.PublisherIntentId, "library-publisher-authorization", registration));
        Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.AssertInitialPolicyIntentCurrent(first.SourceIntentId, "library-source-configuration", registration));
        Assert.IsNull(fixture.Store.CurrentPublisherPolicy()!.Publisher); Assert.AreEqual(2L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
    }

    [TestMethod]
    public void SupersedingSourcePolicyRetiresSetupEvenWhenItsHumanSourcePayloadIsUnchanged()
    {
        using var fixture = new Fixture(); var registration = fixture.PrepareInitialPolicies(); var first = fixture.Store.PrepareInitialPolicyPlan(registration);
        var journal = new ProtocolSigningJournal(fixture.Database, fixture.Budget);
        var originalBody = InitialPolicyBody(fixture.SourcePolicy(1, 2, sourceRevision: 1));
        var original = journal.Reserve(first.SourceIntentId, fixture.ActorId, 1, "library-source-configuration", _ => originalBody);
        original = journal.Seal(original.Id, original.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(original.Body.Span)); fixture.Store.SelectSourceConfiguration(original.Envelope!.Value.Span);
        Assert.AreEqual(first, fixture.Store.AssertInitialPolicyIntentCurrent(first.PublisherIntentId, "library-publisher-authorization", registration));
        var newerBody = InitialPolicyBody(fixture.SourcePolicy(2, 2, sourceRevision: 1));
        var newer = journal.Reserve(Guid.NewGuid(), fixture.ActorId, 1, "library-source-configuration", _ => newerBody);
        newer = journal.Seal(newer.Id, newer.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(newer.Body.Span)); fixture.Store.SelectSourceConfiguration(newer.Envelope!.Value.Span);
        Assert.AreEqual(first.ConfigurationRevision, fixture.Store.AssertBootstrapSourcesCurrent().ConfigurationRevision);
        Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.AssertInitialPolicyIntentCurrent(first.PublisherIntentId, "library-publisher-authorization", registration));
        Assert.ThrowsExactly<LibraryInitialPolicyConflictException>(() => fixture.Store.AssertInitialPolicyIntentCurrent(first.SourceIntentId, "library-source-configuration", registration));
    }

    [TestMethod]
    public void InitialPlanWritesAreAtomicUnderLowStorageOrFailedDigestCheckpoint()
    {
        using var fixture = new Fixture(); var registration = fixture.PrepareInitialPolicies(); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Store.PrepareInitialPolicyPlan(registration)); Assert.IsNull(fixture.Store.InitialPolicyPlan());
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_initial_plan BEFORE INSERT ON processor_meta WHEN NEW.key='initial_library_policies_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Store.PrepareInitialPolicyPlan(registration)); Assert.IsNull(fixture.Store.InitialPolicyPlan());
        fixture.Sql("DROP TRIGGER fail_initial_plan"); Assert.IsNotNull(fixture.Store.PrepareInitialPolicyPlan(registration)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow("initial_library_policies_v1")]
    [DataRow("initial_library_policies_v1:sha256")]
    public void CorruptInitialPlanCannotBeReplacedWithNewIds(string key)
    {
        using var fixture = new Fixture(); var registration = fixture.PrepareInitialPolicies(); fixture.Store.PrepareInitialPolicyPlan(registration);
        fixture.Sql("UPDATE processor_meta SET value='corrupt' WHERE key='" + key + "'");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.InitialPolicyPlan());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.PrepareInitialPolicyPlan(registration));
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
    }

    private static byte[] InitialPolicyBody(byte[] envelope)
    { using var document = JsonDocument.Parse(envelope); return Encoding.UTF8.GetBytes(document.RootElement.GetProperty("document").GetRawText()); }
    private sealed partial class Fixture
    {
        public PublisherSigningRegistration PrepareInitialPolicies()
        {
            new LibrarySourceRepository(Database).Add(SourceIdentity, "account-A", 1, "photo-folder", "Photos", true, 1);
            var key = Publisher.ExportSubjectPublicKeyInfo(); var registration = new PublisherSigningRegistration(_publisherId, 1, Convert.ToBase64String(key), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(key)));
            Store.Bootstrap(ActorId, 1, Workspace, 1, Controller.ExportSubjectPublicKeyInfo(), registration, new(2, false)); return registration;
        }
    }
}
