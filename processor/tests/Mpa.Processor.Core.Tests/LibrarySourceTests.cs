using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class LibrarySourceTests
{
    [TestMethod]
    public void SourceAndPublicationSnapshotCommitTogetherAndSurviveRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); var sourceId = Guid.NewGuid();
        using (var database = new ProcessorStateDatabase(root.Path))
        {
            Connect(database, "account-A");
            var sources = new LibrarySourceRepository(database);
            var added = sources.Add(sourceId, "account-A", 1, "folder-A", "Camera", true, 1);
            Assert.AreEqual(2L, added.Revision); Assert.AreEqual(sourceId, added.Sources.Single().Id);
            Assert.AreEqual(2L, sources.PendingPublications().Single().Revision);
        }
        using var reopened = new ProcessorStateDatabase(root.Path);
        var durable = new LibrarySourceRepository(reopened); var snapshot = durable.Snapshot();
        Assert.AreEqual(2L, snapshot.Revision); Assert.AreEqual(sourceId, snapshot.Sources.Single().Id);
        StringAssert.Contains(durable.PendingPublications().Single().CanonicalSnapshot, sourceId.ToString("D"));
        Assert.IsTrue(snapshot.Sources.Single().AccountConnected);
    }

    [TestMethod]
    public void StaleConcurrentClientCannotReplacePolicyOrAcknowledgeAnotherPublication()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        Connect(database, "account-A"); var sources = new LibrarySourceRepository(database); var sourceId = Guid.NewGuid();
        var first = sources.Add(sourceId, "account-A", 1, "folder-A", "Photos", true, 1);
        var receipt = sources.PendingPublications().Single();
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => sources.Update(sourceId, false, false, 1, 1));
        Assert.AreEqual(first.Revision, sources.Snapshot().Revision);
        Assert.AreEqual(first.Sources.Single(), sources.Snapshot().Sources.Single());
        Assert.IsTrue(sources.Snapshot().Sources.Single().Enabled);
        Assert.ThrowsExactly<InvalidOperationException>(() => sources.ConfirmPublished(receipt.Revision, receipt.CanonicalSnapshot + " "));
        Assert.AreEqual(1, sources.PendingPublications().Count);
        sources.ConfirmPublished(receipt.Revision, receipt.CanonicalSnapshot);
        Assert.AreEqual(0, sources.PendingPublications().Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => sources.ConfirmPublished(receipt.Revision, receipt.CanonicalSnapshot));
    }

    [TestMethod]
    public void DisabledSourceKeepsItsIdentityAndDoesNotRetargetCapturedJobs()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        Connect(database, "account-A"); var sources = new LibrarySourceRepository(database); var sourceId = Guid.NewGuid();
        sources.Add(sourceId, "account-A", 1, "folder-A", "Photos", true, 1);
        var jobs = new DurableJobRepository(database); var jobId = Guid.NewGuid();
        jobs.Create(jobId, "source-scan", "{\"source\":\"captured-folder-A\"}"u8);
        jobs.AddItem(jobId, new(Guid.NewGuid(), new string('a', 64), [new("metadata", new string('b', 64), [])]));
        var disabled = sources.Update(sourceId, false, false, 1, 2);
        Assert.IsFalse(disabled.Sources.Single().Enabled); Assert.AreEqual(sourceId, disabled.Sources.Single().Id);
        Assert.AreEqual("folder-A", disabled.Sources.Single().FolderId);
        Assert.AreEqual("{\"source\":\"captured-folder-A\"}", jobs.ClaimNext()!.CapturedContext);
        var enabled = sources.Update(sourceId, true, true, 2, 3);
        Assert.AreEqual(sourceId, enabled.Sources.Single().Id); Assert.AreEqual(3L, enabled.Sources.Single().Revision);
        Assert.AreEqual(3, sources.PendingPublications().Count);
    }

    [TestMethod]
    public void AccountChangeDuringFolderVerificationCannotCreateAnUnverifiedBinding()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        var account = Connect(database, "account-A");
        new DriveAccountRepository(database).Disconnect(account.PermissionId, account.Revision);
        var sources = new LibrarySourceRepository(database);
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => sources.Add(Guid.NewGuid(), "account-A", account.Revision, "folder-A", "Photos", true, 1));
        Assert.AreEqual(1L, sources.Snapshot().Revision); Assert.AreEqual(0, sources.Snapshot().Sources.Count); Assert.AreEqual(0, sources.PendingPublications().Count);
    }

    [TestMethod]
    public void DuplicateNamesAndDifferentAccountsDoNotMergeSourceIdentities()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        Connect(database, "account-A"); Connect(database, "account-B"); var sources = new LibrarySourceRepository(database);
        sources.Add(Guid.NewGuid(), "account-A", 1, "folder-A", "Camera", true, 1);
        sources.Add(Guid.NewGuid(), "account-B", 1, "folder-A", "Camera", true, 2);
        var snapshot = sources.Add(Guid.NewGuid(), "account-A", 1, "folder-B", "Camera", false, 3);
        Assert.AreEqual(3, snapshot.Sources.Count); Assert.AreEqual(3, snapshot.Sources.Select(source => source.Id).Distinct().Count());
        Assert.ThrowsExactly<ExistingLibrarySourceException>(() => sources.Add(Guid.NewGuid(), "account-A", 1, "folder-A", "Renamed", true, 4));
        Assert.AreEqual(4L, sources.Snapshot().Revision); Assert.AreEqual(3, sources.PendingPublications().Count);
    }

    [TestMethod]
    public void DisconnectedAccountPreservesSourceMetadataAndReconnectKeepsTheSameBinding()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        var original = Connect(database, "account-A"); var sources = new LibrarySourceRepository(database); var id = Guid.NewGuid();
        sources.Add(id, "account-A", 1, "folder-A", "Camera", true, 1);
        var accounts = new DriveAccountRepository(database); accounts.Disconnect("account-A", 1);
        Assert.IsFalse(sources.Snapshot().Sources.Single().AccountConnected); Assert.IsTrue(sources.Snapshot().Sources.Single().Enabled);
        var pending = accounts.Begin(Guid.NewGuid(), "account-A"); accounts.Promote(pending.Id, "account-A", "Updated account", null);
        var current = sources.Snapshot().Sources.Single();
        Assert.IsTrue(current.AccountConnected); Assert.AreEqual(id, current.Id); Assert.AreEqual(original.PermissionId, current.AccountId);
    }

    [TestMethod]
    public void SchemaTwoUpgradePreservesExistingAccountsLibraryIdentityAndSavedJobs()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); Guid installation; Guid library; var job = Guid.NewGuid();
        using (var database = new ProcessorStateDatabase(root.Path))
        {
            installation = database.InstallationId; library = new ProcessorLibraryRepository(database).GetOrCreateIdentity();
            Connect(database, "account-A"); var jobs = new DurableJobRepository(database);
            jobs.Create(job, "source-scan", "{\"destination\":\"captured\"}"u8);
            jobs.AddItem(job, new(Guid.NewGuid(), new string('a', 64), [new("metadata", new string('b', 64), [])]));
        }
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root.Path, "processor.db"), Pooling = false }.ToString()))
        { old.Open(); using var command = old.CreateCommand(); command.CommandText = "DROP TABLE catalog_generation_publication; DROP TABLE workspace_inventory_checkpoint; DROP TABLE workspace_document_observation; DROP INDEX workspace_publication_generation; DROP TABLE catalog_generation_capture; DROP TABLE signing_intent; DROP TABLE signing_actor; DROP TABLE workspace_publication; DROP TABLE current_source_scan; ALTER TABLE job_item DROP COLUMN captured_context; DROP TABLE source_asset; DROP TABLE discovered_asset; DROP TABLE discovery_revision; DROP TABLE configuration_outbox; DROP TABLE source_binding; DROP TABLE library_configuration; PRAGMA user_version=2;"; command.ExecuteNonQuery(); }
        using var upgraded = new ProcessorStateDatabase(root.Path);
        Assert.AreEqual(installation, upgraded.InstallationId); Assert.AreEqual(library, new ProcessorLibraryRepository(upgraded).GetOrCreateIdentity());
        Assert.AreEqual("account-A", new DriveAccountRepository(upgraded).Accounts().Single().PermissionId);
        Assert.AreEqual(1L, new LibrarySourceRepository(upgraded).Snapshot().Revision);
        Assert.AreEqual("{\"destination\":\"captured\"}", new DurableJobRepository(upgraded).ClaimNext()!.CapturedContext);
    }
    private static DriveAccountRecord Connect(ProcessorStateDatabase database, string accountId)
    {
        var repository = new DriveAccountRepository(database); var pending = repository.Begin(Guid.NewGuid());
        return repository.Promote(pending.Id, accountId, "Synthetic account", null);
    }
}
