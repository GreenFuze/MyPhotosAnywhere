using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class WorkspacePublicationTests
{
    private static readonly byte[] Payload = "{\"test\":true}"u8.ToArray();

    [TestMethod]
    public void CompletePublicationInventoryIncludesVerifiedRecordsAndNeverReturnsATruncatedSubset()
    {
        using var fixture = new Fixture(); var verified = Guid.NewGuid(); var pending = Guid.NewGuid();
        var prepared = fixture.Journal.Prepare(verified, fixture.Scope, "publisher-authorization", "application/json", Payload);
        fixture.Journal.AssignFileId(verified, prepared.Sha256, "verified-policy");
        fixture.Journal.ConfirmVerified(verified, fixture.Scope.AccountId, fixture.Scope.FolderId, "verified-policy", prepared.Sha256, Payload.Length, 7);
        fixture.Journal.Prepare(pending, fixture.Scope, "source-configuration", "application/json", Payload);
        CollectionAssert.AreEquivalent(new[] { verified, pending }, fixture.Journal.Identities(2).ToArray());
        CollectionAssert.AreEqual(new[] { pending }, fixture.Journal.Pending().ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.Identities(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Journal.Identities(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Journal.Identities(4097));
        Assert.AreEqual(2, fixture.Journal.Identities(2).Count);
    }

    [TestMethod]
    public void RestartRetainsCapturedBytesScopeAndAllocatedIdUntilExactVerification()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); var id = Guid.NewGuid(); WorkspacePublicationScope scope;
        using (var fixture = new Fixture(root.Path))
        {
            scope = fixture.Scope; var input = Payload.ToArray(); var prepared = fixture.Journal.Prepare(id, scope, "facts-index", "application/json", input);
            input[0] = 0; Assert.IsNull(prepared.FileId); CollectionAssert.AreEqual(Payload, prepared.Payload.ToArray());
        }
        using (var fixture = new Fixture(root.Path))
        {
            var prepared = fixture.Journal.Read(id)!; Assert.AreEqual(scope, prepared.Scope);
            var assigned = fixture.Journal.AssignFileId(id, prepared.Sha256, "preallocated-file");
            Assert.AreEqual("preallocated-file", assigned.FileId); Assert.IsNull(assigned.VerifiedVersion);
            CollectionAssert.AreEqual(new[] { id }, fixture.Journal.Pending().ToArray());
        }
        using (var fixture = new Fixture(root.Path))
        {
            var retry = fixture.Journal.Prepare(id, scope, "facts-index", "application/json", Payload);
            Assert.AreEqual("preallocated-file", retry.FileId); Assert.IsNull(retry.VerifiedVersion);
            fixture.Journal.AssignFileId(id, retry.Sha256, "preallocated-file");
            var receipt = fixture.Journal.ConfirmVerified(id, scope.AccountId, scope.FolderId, retry.FileId!, retry.Sha256, Payload.Length, 7);
            Assert.AreEqual(7L, receipt.VerifiedVersion); Assert.AreEqual(0, fixture.Journal.Pending().Count);
            Assert.AreEqual(receipt.VerifiedVersion, fixture.Journal.ConfirmVerified(id, scope.AccountId, scope.FolderId, retry.FileId!, retry.Sha256, Payload.Length, 7).VerifiedVersion);
        }
        using var final = new Fixture(root.Path); Assert.AreEqual(7L, final.Journal.Read(id)!.VerifiedVersion);
        CollectionAssert.AreEqual(Payload, final.Journal.Read(id)!.Payload.ToArray());
    }

    [TestMethod]
    public void ExistingIdentityCannotBeRetargetedByAnyScopePayloadRoleOrTypeChange()
    {
        using var fixture = new Fixture(); var id = Guid.NewGuid(); var scope = fixture.Scope;
        var record = fixture.Journal.Prepare(id, scope, "facts-index", "application/json", Payload);
        var conflicts = new[] { scope with { AccountId = "account-B" }, scope with { FolderId = "other-workspace" }, scope with { AccountRevision = 2 },
            scope with { ConfigurationRevision = 2 }, scope with { LibraryId = Guid.NewGuid() }, scope with { ActorId = Guid.NewGuid() },
            scope with { GenerationId = Guid.NewGuid() }, scope with { ActorEpoch = 2 }, scope with { AuthoritySha256 = new string('b', 64) } };
        foreach (var changed in conflicts)
            Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.Prepare(id, changed, "facts-index", "application/json", Payload));
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.Prepare(id, scope, "catalog-commit", "application/json", Payload));
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.Prepare(id, scope, "facts-index", "application/octet-stream", Payload));
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.Prepare(id, scope, "facts-index", "application/json", "different"u8));
        fixture.Journal.AssignFileId(id, record.Sha256, "captured-id");
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.AssignFileId(id, record.Sha256, "replacement-id"));
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.AssignFileId(id, new string('0', 64), "captured-id"));
        var other = fixture.Journal.Prepare(Guid.NewGuid(), scope, "facts-index", "application/json", Payload);
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.AssignFileId(other.Id, other.Sha256, "captured-id"));
        Assert.AreEqual("captured-id", fixture.Journal.Read(id)!.FileId); Assert.IsNull(fixture.Journal.Read(other.Id)!.FileId);
    }

    [TestMethod]
    public void ReceiptCannotAcknowledgeAnotherAccountWorkspaceFileDigestLengthOrVersion()
    {
        using var fixture = new Fixture(); var scope = fixture.Scope; var record = fixture.Journal.Prepare(Guid.NewGuid(), scope, "coverage-root", "application/json", Payload);
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.ConfirmVerified(record.Id, scope.AccountId, scope.FolderId, "file", record.Sha256, Payload.Length, 7));
        fixture.Journal.AssignFileId(record.Id, record.Sha256, "file");
        var cases = new[] { ("other-account", scope.FolderId, "file", record.Sha256, (long)Payload.Length), (scope.AccountId, "other-folder", "file", record.Sha256, (long)Payload.Length),
            (scope.AccountId, scope.FolderId, "other-file", record.Sha256, (long)Payload.Length), (scope.AccountId, scope.FolderId, "file", new string('0', 64), (long)Payload.Length),
            (scope.AccountId, scope.FolderId, "file", record.Sha256, Payload.Length + 1L) };
        foreach (var item in cases)
            Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.ConfirmVerified(record.Id, item.Item1, item.Item2, item.Item3, item.Item4, item.Item5, 7));
        Assert.IsNull(fixture.Journal.Read(record.Id)!.VerifiedVersion);
        fixture.Journal.ConfirmVerified(record.Id, scope.AccountId, scope.FolderId, "file", record.Sha256, Payload.Length, 7);
        Assert.ThrowsExactly<WorkspacePublicationConflictException>(() => fixture.Journal.ConfirmVerified(record.Id, scope.AccountId, scope.FolderId, "file", record.Sha256, Payload.Length, 8));
    }

    [TestMethod]
    public void StaleAdmissionDoesNotRetargetOldWorkAfterCredentialOrConfigurationChanges()
    {
        using var fixture = new Fixture(); var old = fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "facts-index", "application/json", Payload);
        fixture.Journal.AssertCurrent(old.Id);
        var accounts = new DriveAccountRepository(fixture.Database); var pending = accounts.Begin(Guid.NewGuid(), fixture.Scope.AccountId);
        accounts.Promote(pending.Id, fixture.Scope.AccountId, "Reconnected", null);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Journal.AssertCurrent(old.Id));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "facts-index", "application/json", Payload));
        fixture.Sql("UPDATE library_configuration SET revision=2");
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope with { AccountRevision = 2 }, "facts-index", "application/json", Payload));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope with { AccountRevision = 2, ConfigurationRevision = 2, LibraryId = Guid.NewGuid() }, "facts-index", "application/json", Payload));
        Assert.AreEqual(old.Scope, fixture.Journal.Read(old.Id)!.Scope);
        Assert.AreEqual(old.Scope, fixture.Journal.Prepare(old.Id, old.Scope, old.Role, old.MimeType, Payload).Scope);
        fixture.Journal.AssignFileId(old.Id, old.Sha256, "captured-file");
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Journal.ConfirmVerified(old.Id, old.Scope.AccountId, old.Scope.FolderId, "captured-file", old.Sha256, Payload.Length, 7));
        Assert.IsNull(fixture.Journal.Read(old.Id)!.VerifiedVersion);
        Assert.AreEqual(1, fixture.Journal.Pending().Count);
    }

    [TestMethod]
    public void DamagedJournalFailsBeforeItCanSupplyRetryBytesOrAcceptAReceipt()
    {
        foreach (var sql in new[] { "UPDATE workspace_publication SET payload=X'00'", "UPDATE workspace_publication SET scope='{}'", "UPDATE workspace_publication SET payload_hash='invalid'",
            "UPDATE workspace_publication SET file_id='root'", "UPDATE workspace_publication SET role='original-upload'",
            "UPDATE workspace_publication SET role='catalog-commit'", "UPDATE workspace_publication SET mime_type='application/gzip'" })
        {
            using var fixture = new Fixture(); var prepared = fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "facts-index", "application/json", Payload);
            fixture.Sql(sql);
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.Read(prepared.Id));
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.AssignFileId(prepared.Id, prepared.Sha256, "new-file"));
            Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.ConfirmVerified(prepared.Id, fixture.Scope.AccountId, fixture.Scope.FolderId, "new-file", prepared.Sha256, Payload.Length, 1));
            Assert.AreEqual(1, fixture.Journal.Pending().Count);
        }
    }

    [TestMethod]
    public void LowStorageAndFailedSqlLeaveNoPartialRecordAndReleaseTheirReservations()
    {
        using var fixture = new Fixture(); var id = Guid.NewGuid();
        fixture.Space.Available = 1;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => fixture.Journal.Prepare(id, fixture.Scope, "facts-index", "application/json", Payload));
        Assert.IsNull(fixture.Journal.Read(id)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte;
        fixture.Sql("CREATE TRIGGER fail_publication BEFORE INSERT ON workspace_publication BEGIN SELECT RAISE(ABORT,'synthetic full write'); END");
        Assert.ThrowsExactly<SqliteException>(() => fixture.Journal.Prepare(id, fixture.Scope, "facts-index", "application/json", Payload));
        Assert.IsNull(fixture.Journal.Read(id)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Sql("DROP TRIGGER fail_publication"); Assert.AreEqual(id, fixture.Journal.Prepare(id, fixture.Scope, "facts-index", "application/json", Payload).Id);
    }

    [TestMethod]
    public void BoundsAndPendingEnumerationDoNotLoadTheWholeOutbox()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 140; i++) fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "facts-index", "application/json", Payload);
        Assert.AreEqual(32, fixture.Journal.Pending().Count); Assert.AreEqual(128, fixture.Journal.Pending(128).Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Journal.Pending(129));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "facts-index", "application/json", []));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "facts-index", "application/json", new byte[WorkspacePublicationRepository.MaximumPayloadBytes + 1]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "original-upload", "image/jpeg", Payload));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope with { FolderId = "root" }, "facts-index", "application/json", Payload));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope with { ActorEpoch = 0 }, "facts-index", "application/json", Payload));
        var maximum = new byte[WorkspacePublicationRepository.MaximumPayloadBytes]; RandomNumberGenerator.Fill(maximum);
        var large = fixture.Journal.Prepare(Guid.NewGuid(), fixture.Scope, "facts-chunk", "application/gzip", maximum);
        CollectionAssert.AreEqual(maximum, fixture.Journal.Read(large.Id)!.Payload.ToArray());
    }

    [TestMethod]
    public void SchemaSixMigrationPreservesExistingRowsJobsOriginalsAndIdentities()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); Guid library; Guid installation; Guid job;
        using (var fixture = new Fixture(root.Path))
        {
            library = fixture.Scope.LibraryId; installation = fixture.Database.InstallationId; job = Guid.NewGuid();
            new DurableJobRepository(fixture.Database).Create(job, "takeout-import", "{\"destination\":\"captured\",\"caption\":\"Human caption\"}"u8);
        }
        var original = Path.Combine(root.Path, "retained-original.bin"); File.WriteAllBytes(original, Payload);
        using (var sql = Open(root.Path))
        { using var command = sql.CreateCommand(); command.CommandText = "DROP TABLE catalog_generation_verification; DROP TABLE catalog_generation_publication; DROP TABLE workspace_inventory_checkpoint; DROP TABLE workspace_document_observation; DROP INDEX workspace_publication_generation; DROP TABLE catalog_generation_capture; DROP TABLE signing_intent; DROP TABLE signing_actor; DROP TABLE workspace_publication; PRAGMA user_version=6"; command.ExecuteNonQuery(); }
        using var upgraded = new Fixture(root.Path);
        Assert.AreEqual(library, upgraded.Scope.LibraryId); Assert.AreEqual(installation, upgraded.Database.InstallationId);
        Assert.AreEqual(0, upgraded.Journal.Pending().Count); Assert.AreEqual(0, new DurableJobRepository(upgraded.Database).Summary(job).Failed);
        CollectionAssert.AreEqual(Payload, File.ReadAllBytes(original));
        using var connection = Open(root.Path); using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.AreEqual(12L, query.ExecuteScalar());
        query.CommandText = "SELECT captured_context FROM job WHERE id=$job"; query.Parameters.AddWithValue("$job", job.ToString("D"));
        StringAssert.Contains((string)query.ExecuteScalar()!, "Human caption");
    }

    private static SqliteConnection Open(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "processor.db"), Pooling = false }.ToString()); connection.Open(); return connection; }
    private sealed class SpaceReader : IStorageSpaceReader
    {
        public long Available { get; set; } = 20 * StorageBudgetManager.Gibibyte;
        public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, Available, 0);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly WorkerProcessTests.WorkerTestDirectory? _root;
        private readonly string _path;
        public ProcessorStateDatabase Database { get; }
        public WorkspacePublicationRepository Journal { get; }
        public WorkspacePublicationScope Scope { get; }
        public SpaceReader Space { get; } = new();
        public StorageBudgetManager Budget { get; }
        public Fixture(string? path = null)
        {
            if (path is null) { _root = new(); path = _root.Path; }
            _path = path;
            Database = new(path); var accounts = new DriveAccountRepository(Database);
            if (accounts.Accounts().Count == 0) { var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account-A", "Synthetic", null); }
            Scope = new(new ProcessorLibraryRepository(Database).GetOrCreateIdentity(), Guid.NewGuid(), 1, Guid.NewGuid(), "account-A", 1, "workspace", 1, new string('a', 64));
            Budget = new(Space); Journal = new(Database, Budget);
        }
        public void Sql(string text) { using var connection = Open(_path); using var command = connection.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
        public void Dispose() { Database.Dispose(); _root?.Dispose(); }
    }
}
