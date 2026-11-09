using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class LibraryAuthorityTests
{
    [TestMethod]
    public void SignedSourceSelectionSurvivesRestartWithoutChangingLocalSourceBindings()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); AcceptedSourceConfiguration selected;
        using (var fixture = new Fixture(root.Path))
        {
            fixture.Bootstrap(); Assert.IsNull(fixture.Store.CurrentSourceConfiguration());
            selected = fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(2));
            Assert.AreEqual(2L, selected.Sequence); Assert.AreEqual(1L, selected.Configuration.Revision);
            Assert.AreEqual("photo-folder", selected.Configuration.Sources.Single().FolderId);
            Assert.AreEqual(0, new LibrarySourceRepository(fixture.Database).Snapshot().Sources.Count);
        }
        using var database = new ProcessorStateDatabase(root.Path); var reader = new LibraryAuthorityRepository(database);
        Assert.AreEqual(selected.EnvelopeSha256, reader.CurrentSourceConfiguration()!.EnvelopeSha256);
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.SelectSourceConfiguration([]));
    }

    [TestMethod]
    public void DelayedSourcePoliciesNeverRewindAndExactReplayWorksUnderLowStorage()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var first = fixture.SourcePolicy(2); var later = fixture.SourcePolicy(5, 3, enabled: false);
        fixture.Store.SelectSourceConfiguration(first); fixture.Store.SelectSourceConfiguration(later);
        Assert.AreEqual(5L, fixture.Store.SelectSourceConfiguration(first).Sequence);
        Assert.AreEqual(5L, fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(3, 2)).Sequence);
        fixture.Space.Available = 1; var replay = fixture.Store.SelectSourceConfiguration(later);
        Assert.IsFalse(replay.Configuration.Sources.Single().Enabled); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void SourceRevisionAndControllerSequenceCannotDisagreeOrEquivocate()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(5, 3));
        foreach (var candidate in new[] { fixture.SourcePolicy(5, 3, enabled: false), fixture.SourcePolicy(6, 2),
            fixture.SourcePolicy(4, 4), fixture.SourcePolicy(6, 3, enabled: false) })
            Assert.ThrowsExactly<ControllerAuthorityConflictException>(() => fixture.Store.SelectSourceConfiguration(candidate));
        Assert.AreEqual(6L, fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(6, 3)).Sequence);
        Assert.AreEqual(3L, fixture.Store.CurrentSourceConfiguration()!.Configuration.Revision);
    }

    [TestMethod]
    public void PublisherAndSourceDocumentsCannotReuseTheControllerSequence()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); fixture.Store.SelectPublisherPolicy(fixture.Policy(1));
        Assert.ThrowsExactly<ControllerAuthorityConflictException>(() => fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(1)));
        fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(2));
        Assert.ThrowsExactly<ControllerAuthorityConflictException>(() => fixture.Store.SelectPublisherPolicy(fixture.Policy(2, revoked: true)));
        Assert.IsNotNull(fixture.Store.CurrentPublisherPolicy()!.Publisher);
        Assert.AreEqual(2L, fixture.Store.CurrentSourceConfiguration()!.Sequence);
    }

    [TestMethod]
    public void UnenrolledForeignAndWrongRoleSourcePoliciesCannotCreateAcceptedState()
    {
        using var fixture = new Fixture(); var valid = fixture.SourcePolicy(1);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.SelectSourceConfiguration(valid)); fixture.Bootstrap();
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(1, signer: fixture.Publisher)));
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(1, workspace: "another-workspace")));
        Assert.ThrowsExactly<ProtocolCompatibilityException>(() => fixture.Store.SelectSourceConfiguration(fixture.Policy(1)));
        Assert.IsNull(fixture.Store.CurrentSourceConfiguration());
    }

    [TestMethod]
    public void DamagedSourceSelectionNeverGetsRebuiltFromIncomingOrLocalSettings()
    {
        foreach (var sql in new[] { "DELETE FROM processor_meta WHERE key='current_source_configuration_v1'",
            "UPDATE processor_meta SET value='2' WHERE key='current_source_configuration_v1'",
            "DELETE FROM processor_meta WHERE key='controller_source_document_v1:5'",
            "UPDATE processor_meta SET value='corrupt' WHERE key='controller_source_document_v1:5:sha256'" })
        {
            using var fixture = new Fixture(); fixture.Bootstrap(); fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(2));
            fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(5, 2)); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.CurrentSourceConfiguration());
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(6, 3)));
        }
    }

    [TestMethod]
    public void SourceHistoryCannotBootstrapAReplacementControllerAfterRootLoss()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(2));
        fixture.Sql("DELETE FROM processor_meta WHERE key IN ('library_controller_v1','library_controller_v1:sha256')");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.Controller());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.CurrentSourceConfiguration());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Bootstrap());
    }

    [TestMethod]
    public void LowStorageAndCheckpointFailureKeepSourceSelectionAtomic()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); fixture.Store.SelectSourceConfiguration(fixture.SourcePolicy(2));
        var later = fixture.SourcePolicy(5, 2); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Store.SelectSourceConfiguration(later));
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_source_policy BEFORE INSERT ON processor_meta WHEN NEW.key='controller_source_document_v1:5:sha256' BEGIN SELECT RAISE(ABORT,'synthetic failed source policy checkpoint'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Store.SelectSourceConfiguration(later));
        Assert.AreEqual(2L, fixture.Store.CurrentSourceConfiguration()!.Sequence);
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='controller_source_document_v1:5'"));
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_source_policy");
        fixture.Sql("CREATE TRIGGER fail_source_pointer BEFORE UPDATE ON processor_meta WHEN NEW.key='current_source_configuration_v1' BEGIN SELECT RAISE(ABORT,'synthetic failed source selection'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Store.SelectSourceConfiguration(later));
        Assert.AreEqual(2L, fixture.Store.CurrentSourceConfiguration()!.Sequence);
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='controller_source_document_v1:5'"));
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_source_pointer"); Assert.AreEqual(5L, fixture.Store.SelectSourceConfiguration(later).Sequence);
    }

    private sealed partial class Fixture
    {
        private static readonly Guid SourceIdentity = Guid.Parse("00000000-0000-4000-8000-000000000001");
        public byte[] SourcePolicy(long sequence, long revision = 1, bool enabled = true, ECDsa? signer = null, string? workspace = null, long? sourceRevision = null)
        {
            var body = new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0,
                libraryId = _library.ToString("D"), actorId = ActorId.ToString("D"), epoch = "1", sequence = sequence.ToString(CultureInfo.InvariantCulture),
                kind = "library-source-configuration", requiredCapabilities = new[] { CatalogSourceConfiguration.Capability },
                payload = new { configurationRevision = revision.ToString(CultureInfo.InvariantCulture),
                    workspace = new { accountId = Workspace.AccountId, folderId = workspace ?? Workspace.FolderId }, accounts = new[] { Workspace.AccountId },
                    sources = new[] { new { id = SourceIdentity.ToString("D"), accountId = Workspace.AccountId, folderId = "photo-folder",
                        revision = (sourceRevision ?? revision).ToString(CultureInfo.InvariantCulture), includeDescendants = true, enabled } } } }));
            return new SignedProtocolDocuments([CatalogSourceConfiguration.Capability]).Encode(body, new ProtocolDocumentSigner(signer ?? Controller).Sign(body));
        }
    }
}
