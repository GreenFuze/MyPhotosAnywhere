using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class LibraryAuthorityTests
{
    [TestMethod]
    public void IndependentlyBootstrappedPublicRootAndRevocationSurviveRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); LibraryControllerPin pin; Guid installation;
        using (var fixture = new Fixture(root.Path))
        {
            installation = fixture.Database.InstallationId; pin = fixture.Bootstrap(); fixture.Store.SelectPublisherPolicy(fixture.Policy(9));
            fixture.Store.SelectPublisherPolicy(fixture.Policy(10, revoked: true));
            new DurableJobRepository(fixture.Database).Create(Guid.NewGuid(), "takeout-import", "{\"destination\":\"captured\",\"caption\":\"Human caption\"}"u8);
        }
        using var database = new ProcessorStateDatabase(root.Path); var store = new LibraryAuthorityRepository(database, new(new Space()));
        Assert.AreEqual(pin, store.Controller()); Assert.AreEqual(installation, database.InstallationId);
        Assert.AreEqual(10L, store.CurrentPublisherPolicy()!.Sequence); Assert.IsNull(store.CurrentPublisherPolicy()!.Publisher);
        using var connection = Open(root.Path); using var command = connection.CreateCommand(); command.CommandText = "SELECT captured_context FROM job";
        StringAssert.Contains((string)command.ExecuteScalar()!, "Human caption");
    }

    [TestMethod]
    public void DelayedAndRepeatedAcceptedPoliciesNeverReactivateARevokedPublisher()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var active = fixture.Policy(9); var revoked = fixture.Policy(10, revoked: true);
        fixture.Store.SelectPublisherPolicy(active); fixture.Store.SelectPublisherPolicy(revoked);
        Assert.IsNull(fixture.Store.SelectPublisherPolicy(active).Publisher); Assert.IsNull(fixture.Store.SelectPublisherPolicy(fixture.Policy(8)).Publisher);
        Assert.AreEqual(10L, fixture.Store.CurrentPublisherPolicy()!.Sequence);
        fixture.Space.Available = 1; Assert.IsNull(fixture.Store.SelectPublisherPolicy(revoked).Publisher);
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void SameSequenceDifferentBytesAndRootReplacementAreExplicitConflicts()
    {
        using var fixture = new Fixture(); var pin = fixture.Bootstrap(); var active = fixture.Policy(9); fixture.Store.SelectPublisherPolicy(active);
        Assert.ThrowsExactly<ControllerAuthorityConflictException>(() => fixture.Store.SelectPublisherPolicy(fixture.Policy(9, revoked: true)));
        Assert.ThrowsExactly<ControllerAuthorityConflictException>(() => fixture.Store.Bootstrap(Guid.NewGuid(), 1, Fixture.Workspace, 1, fixture.Controller.ExportSubjectPublicKeyInfo()));
        Assert.ThrowsExactly<ControllerAuthorityConflictException>(() => fixture.Store.Bootstrap(fixture.ActorId, 2, Fixture.Workspace, 1, fixture.Controller.ExportSubjectPublicKeyInfo()));
        Assert.ThrowsExactly<ControllerAuthorityConflictException>(() => fixture.Store.Bootstrap(fixture.ActorId, 1, Fixture.Workspace, 1, fixture.Publisher.ExportSubjectPublicKeyInfo()));
        Assert.AreEqual(pin, fixture.Store.Controller()); Assert.AreEqual(9L, fixture.Store.CurrentPublisherPolicy()!.Sequence);
        Assert.AreEqual(pin, fixture.Bootstrap());
    }

    [TestMethod]
    public void MissingOrCorruptRootIsNeverSilentlyRecreatedOverAcceptedHistory()
    {
        foreach (var sql in new[] { "DELETE FROM processor_meta WHERE key='library_controller_v1'", "DELETE FROM processor_meta WHERE key='library_controller_v1:sha256'",
            "DELETE FROM processor_meta WHERE key IN('library_controller_v1','library_controller_v1:sha256')", "UPDATE processor_meta SET value='{}' WHERE key='library_controller_v1'" })
        {
            using var fixture = new Fixture(); fixture.Bootstrap(); fixture.Store.SelectPublisherPolicy(fixture.Policy(9)); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.Controller());
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Bootstrap());
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.CurrentPublisherPolicy());
        }
    }

    [TestMethod]
    public void DamagedOrRewoundSelectionCannotBePresentedAsAnOlderActivePolicy()
    {
        foreach (var sql in new[] { "DELETE FROM processor_meta WHERE key='current_publisher_policy_v1'", "UPDATE processor_meta SET value='9' WHERE key='current_publisher_policy_v1'",
            "UPDATE processor_meta SET value='11' WHERE key='current_publisher_policy_v1'", "DELETE FROM processor_meta WHERE key='controller_document_v1:10'",
            "UPDATE processor_meta SET value='corrupt' WHERE key='controller_document_v1:10:sha256'" })
        {
            using var fixture = new Fixture(); fixture.Bootstrap(); var old = fixture.Policy(9); fixture.Store.SelectPublisherPolicy(old);
            fixture.Store.SelectPublisherPolicy(fixture.Policy(10, revoked: true)); fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.CurrentPublisherPolicy());
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Store.SelectPublisherPolicy(old));
        }
    }

    [TestMethod]
    public void UnenrolledUntrustedAndForeignWorkspaceDocumentsNeverCreatePolicyState()
    {
        using var fixture = new Fixture(); var policy = fixture.Policy(9);
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Store.SelectPublisherPolicy(policy)); fixture.Bootstrap();
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Store.SelectPublisherPolicy(fixture.Policy(9, signer: fixture.Publisher)));
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Store.SelectPublisherPolicy(fixture.Policy(9, folder: "foreign-folder")));
        Assert.IsNull(fixture.Store.CurrentPublisherPolicy()); Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key GLOB 'controller_document_v1:*'"));
    }

    [TestMethod]
    public void LowStorageAndStaleBootstrapKeepRootAndPolicyWritesAtomic()
    {
        using var fixture = new Fixture();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Store.Bootstrap(fixture.ActorId, 1, Fixture.Workspace, 2, fixture.Controller.ExportSubjectPublicKeyInfo()));
        fixture.Space.Available = 1; Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Bootstrap()); Assert.IsNull(fixture.Store.Controller());
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte; fixture.Bootstrap(); fixture.Store.SelectPublisherPolicy(fixture.Policy(9));
        var revoked = fixture.Policy(10, revoked: true); fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Store.SelectPublisherPolicy(revoked));
        Assert.AreEqual(9L, fixture.Store.CurrentPublisherPolicy()!.Sequence); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        Assert.IsNull(fixture.Store.SelectPublisherPolicy(revoked).Publisher);
    }

    [TestMethod]
    public void FailedHashOrPointerWritesCannotLeavePartialAcceptedAuthority()
    {
        using var fixture = new Fixture();
        fixture.Sql("CREATE TRIGGER fail_root BEFORE INSERT ON processor_meta WHEN NEW.key='library_controller_v1:sha256' BEGIN SELECT RAISE(ABORT,'synthetic full write'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Bootstrap()); Assert.IsNull(fixture.Store.Controller()); fixture.Sql("DROP TRIGGER fail_root");
        fixture.Bootstrap(); var active = fixture.Policy(9); fixture.Store.SelectPublisherPolicy(active);
        fixture.Sql("CREATE TRIGGER fail_policy BEFORE INSERT ON processor_meta WHEN NEW.key='controller_document_v1:10:sha256' BEGIN SELECT RAISE(ABORT,'synthetic failed checkpoint'); END");
        var revoked = fixture.Policy(10, revoked: true);
        Assert.ThrowsExactly<SqliteException>(() => fixture.Store.SelectPublisherPolicy(revoked)); Assert.AreEqual(9L, fixture.Store.CurrentPublisherPolicy()!.Sequence);
        Assert.AreEqual(0L, fixture.Scalar("SELECT count(*) FROM processor_meta WHERE key='controller_document_v1:10'"));
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes); fixture.Sql("DROP TRIGGER fail_policy");
        Assert.IsNull(fixture.Store.SelectPublisherPolicy(revoked).Publisher);
    }

    private static SqliteConnection Open(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "processor.db"), Pooling = false }.ToString()); connection.Open(); return connection; }
    private sealed class Space : IStorageSpaceReader
    {
        public long Available { get; set; } = 20 * StorageBudgetManager.Gibibyte;
        public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, Available, 0);
    }
    private sealed class Fixture : IDisposable
    {
        public static readonly CatalogWorkspace Workspace = new("account-A", "metadata-workspace");
        private readonly WorkerProcessTests.WorkerTestDirectory? _root;
        private readonly string _path;
        private readonly Guid _library;
        private readonly Guid _publisherId = Guid.NewGuid();
        public Guid ActorId { get; } = Guid.NewGuid();
        public ECDsa Controller { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Publisher { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ProcessorStateDatabase Database { get; }
        public LibraryAuthorityRepository Store { get; }
        public Space Space { get; } = new();
        public StorageBudgetManager Budget { get; }
        public Fixture(string? path = null)
        {
            if (path is null) { _root = new(); path = _root.Path; } _path = path;
            Database = new(path); _library = new ProcessorLibraryRepository(Database).GetOrCreateIdentity();
            var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, Workspace.AccountId, "Synthetic", null);
            Budget = new(Space); Store = new(Database, Budget);
        }
        public LibraryControllerPin Bootstrap() => Store.Bootstrap(ActorId, 1, Workspace, 1, Controller.ExportSubjectPublicKeyInfo());
        public byte[] Policy(long sequence, bool revoked = false, ECDsa? signer = null, string? folder = null)
        {
            var publicKey = Publisher.ExportSubjectPublicKeyInfo();
            var body = new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0, libraryId = _library.ToString("D"),
                actorId = ActorId.ToString("D"), epoch = "1", sequence = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), kind = "library-publisher-authorization",
                requiredCapabilities = new[] { CatalogPublisherAuthorization.Capability }, payload = new { workspace = new { accountId = Workspace.AccountId, folderId = folder ?? Workspace.FolderId },
                    publisher = revoked ? null : new { actorId = _publisherId.ToString("D"), epoch = "1", publicKeySpki = Convert.ToBase64String(publicKey), keyId = Convert.ToHexStringLower(SHA256.HashData(publicKey)) } } }));
            return new SignedProtocolDocuments([CatalogPublisherAuthorization.Capability]).Encode(body, new ProtocolDocumentSigner(signer ?? Controller).Sign(body));
        }
        public void Sql(string sql) { using var connection = Open(_path); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public object Scalar(string sql) { using var connection = Open(_path); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()!; }
        public void Dispose() { Database.Dispose(); Controller.Dispose(); Publisher.Dispose(); _root?.Dispose(); }
    }
}
