using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void ExplicitInitialSourceAssociationSurvivesRestartWithoutIncludingConnectionStateOrDisplayNames()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); LibraryBootstrapSources expected;
        using (var fixture = new Fixture(root.Path))
        {
            var sources = new LibrarySourceRepository(fixture.Database); var captured = sources.Add(Guid.NewGuid(), "account-A", 1, "photo-folder", "Private display name", true, 1);
            fixture.Bootstrap(new(captured.Revision, true)); expected = fixture.Store.AssertBootstrapSourcesCurrent();
            using var json = JsonDocument.Parse(expected.CanonicalSnapshot); Assert.IsTrue(expected.AllowSharedMetadata);
            Assert.AreEqual("photo-folder", json.RootElement.GetProperty("sources")[0].GetProperty("folderId").GetString());
            Assert.IsFalse(expected.CanonicalSnapshot.Contains("Private display name", StringComparison.Ordinal));
            new DriveAccountRepository(fixture.Database).Disconnect("account-A", 1);
            Assert.AreEqual(expected, fixture.Store.AssertBootstrapSourcesCurrent());
        }
        using var database = new ProcessorStateDatabase(root.Path); var store = new LibraryAuthorityRepository(database, new(new Space()));
        Assert.AreEqual(expected, store.BootstrapSources()); Assert.AreEqual(expected, store.AssertBootstrapSourcesCurrent());
    }

    [TestMethod]
    public void PinAndConnectedAccountsCannotManufactureMissingSourceApproval()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); Assert.IsNull(fixture.Store.BootstrapSources());
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.AssertBootstrapSourcesCurrent());
        fixture.Bootstrap(new(1, false)); Assert.IsNotNull(fixture.Store.AssertBootstrapSourcesCurrent());
    }

    [TestMethod]
    public void LaterSourceOrPrivacyChangesNeverReplaceTheInitialReviewedScope()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(new(1, false)); var first = fixture.Store.BootstrapSources();
        Assert.ThrowsExactly<LibraryBootstrapApprovalConflictException>(() => fixture.Bootstrap(new(1, true))); Assert.AreEqual(first, fixture.Store.BootstrapSources());
        var sources = new LibrarySourceRepository(fixture.Database); var changed = sources.Add(Guid.NewGuid(), "account-A", 1, "another-folder", "Other photos", true, 1);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.AssertBootstrapSourcesCurrent());
        Assert.ThrowsExactly<LibraryBootstrapApprovalConflictException>(() => fixture.Bootstrap(new(changed.Revision, false)));
        Assert.AreEqual(first, fixture.Store.BootstrapSources()); Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='library_bootstrap_sources_v1'"));
    }

    [TestMethod]
    public void SourceApprovalFailureRollsBackRootActorsAndSnapshotTogether()
    {
        using var fixture = new Fixture();
        fixture.Sql("CREATE TRIGGER fail_source_approval BEFORE INSERT ON processor_meta WHEN NEW.key='library_bootstrap_sources_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic failed source approval checkpoint'); END");
        var publisher = fixture.Publisher.ExportSubjectPublicKeyInfo(); var registration = new PublisherSigningRegistration(Guid.NewGuid(), 1, Convert.ToBase64String(publisher), Convert.ToHexStringLower(SHA256.HashData(publisher)));
        Assert.ThrowsExactly<SqliteException>(() => fixture.Store.Bootstrap(fixture.ActorId, 1, Fixture.Workspace, 1, fixture.Controller.ExportSubjectPublicKeyInfo(), registration, new(1, false)));
        Assert.IsNull(fixture.Store.Controller()); Assert.IsNull(fixture.Store.BootstrapSources());
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM signing_actor")); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_source_approval"); fixture.Store.Bootstrap(fixture.ActorId, 1, Fixture.Workspace, 1, fixture.Controller.ExportSubjectPublicKeyInfo(), registration, new(1, false));
        Assert.AreEqual(2L, fixture.Scalar("SELECT count(*) FROM signing_actor")); Assert.IsNotNull(fixture.Store.AssertBootstrapSourcesCurrent());
    }

    [TestMethod]
    public void SourceApprovalCorruptionOrMissingRootCannotBeReplacedByAReplayedSetup()
    {
        foreach (var damage in new[] { "DELETE FROM processor_meta WHERE key='library_bootstrap_sources_v1:sha256'", "UPDATE processor_meta SET value='corrupt' WHERE key='library_bootstrap_sources_v1'",
            "DELETE FROM processor_meta WHERE key GLOB 'library_controller_v1*'" })
        {
            using var fixture = new Fixture(); fixture.Bootstrap(new(1, false)); fixture.Sql(damage);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.BootstrapSources());
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Bootstrap(new(1, false)));
        }
    }

    [TestMethod]
    public void StaleInitialApprovalNeverPinsRootAndExactReplayRequiresNoAdditionalStorage()
    {
        using var fixture = new Fixture(); Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Bootstrap(new(2, false)));
        Assert.IsNull(fixture.Store.Controller()); fixture.Bootstrap(new(1, false)); var captured = fixture.Store.BootstrapSources();
        fixture.Space.Available = 1; fixture.Bootstrap(new(1, false)); Assert.AreEqual(captured, fixture.Store.BootstrapSources()); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }
}
