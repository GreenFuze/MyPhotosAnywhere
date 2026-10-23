using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class DriveAccountTests
{
    [TestMethod]
    public void SetupPromotionAndReconnectionKeepStableIdentityAndDurableCleanupAcrossRestart()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        Guid installation; Guid old; Guid replacement;
        using (var state = new ProcessorStateDatabase(root.Path))
        {
            installation = state.InstallationId;
            var accounts = new DriveAccountRepository(state);
            old = accounts.Begin(Guid.NewGuid()).Id;
            Assert.AreEqual(0, accounts.Accounts().Count);
            var first = accounts.Promote(old, "permission-A", "First", null);
            Assert.AreEqual(1L, first.Revision);
            Assert.ThrowsExactly<InvalidOperationException>(() => accounts.ConfirmCleanup(old));
            replacement = accounts.Begin(Guid.NewGuid(), "permission-A").Id;
            Assert.AreEqual(old, accounts.Accounts().Single().ConnectionId);
            var changed = accounts.Promote(replacement, "permission-A", "Renamed", "synthetic@example.invalid");
            Assert.AreEqual("permission-A", changed.PermissionId);
            Assert.AreEqual(2L, changed.Revision);
            Assert.AreEqual(0, accounts.PendingConnections().Count);
        }
        using var reopened = new ProcessorStateDatabase(root.Path);
        Assert.AreEqual(installation, reopened.InstallationId);
        var durable = new DriveAccountRepository(reopened);
        Assert.AreEqual(replacement, durable.Accounts().Single().ConnectionId);
        Assert.AreEqual(old, durable.PendingCleanup().Single().ConnectionId);
        durable.ConfirmCleanup(old);
        Assert.AreEqual(0, durable.PendingCleanup().Count);
    }

    [TestMethod]
    public void WrongAccountAndDuplicateNewConnectionCannotReplaceExistingBindings()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var state = new ProcessorStateDatabase(root.Path);
        var accounts = new DriveAccountRepository(state);
        var id = accounts.Begin(Guid.NewGuid()).Id;
        var first = accounts.Promote(id, "permission-A", "A", null);
        var reconnect = accounts.Begin(Guid.NewGuid(), first.PermissionId);
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => accounts.Promote(reconnect.Id, "permission-B", "B", null));
        var duplicate = accounts.Begin(Guid.NewGuid());
        Assert.ThrowsExactly<DriveAccountAlreadyConnectedException>(() => accounts.Promote(duplicate.Id, "permission-A", "A", null));
        Assert.AreEqual(first, accounts.Accounts().Single());
        Assert.AreEqual(2, accounts.PendingConnections().Count);
        Assert.AreEqual(0, accounts.PendingCleanup().Count);
    }

    [TestMethod]
    public void DisconnectFencesAnOlderSetupAndPreservesTheAccountAndUnrelatedJobs()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var state = new ProcessorStateDatabase(root.Path);
        var accounts = new DriveAccountRepository(state);
        var id = accounts.Begin(Guid.NewGuid()).Id;
        var first = accounts.Promote(id, "permission-A", "A", null);
        var reconnect = accounts.Begin(Guid.NewGuid(), first.PermissionId);
        var jobs = new DurableJobRepository(state);
        var job = Guid.NewGuid(); jobs.Create(job, "source-scan", "{\"account\":\"permission-A\"}"u8);
        accounts.Disconnect(first.PermissionId, first.Revision);
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => accounts.Promote(reconnect.Id, first.PermissionId, "A", null));
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => accounts.Disconnect(first.PermissionId, first.Revision));
        Assert.IsFalse(accounts.Accounts().Single().Connected);
        Assert.AreEqual(id, accounts.PendingCleanup().Single().ConnectionId);
        Assert.IsFalse(jobs.Summary(job).IsCanceled);
        accounts.Abandon(reconnect.Id);
        Assert.AreEqual(2, accounts.PendingCleanup().Count);
    }

    [TestMethod]
    public void PendingSetupAndAbandonmentSurviveRestartWithoutAssumingVaultCleanupSucceeded()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        var id = Guid.NewGuid();
        using (var state = new ProcessorStateDatabase(root.Path)) new DriveAccountRepository(state).Begin(id);
        using (var state = new ProcessorStateDatabase(root.Path))
        {
            var accounts = new DriveAccountRepository(state);
            Assert.AreEqual(id, accounts.PendingConnections().Single().Id);
            accounts.Abandon(id);
        }
        using var reopened = new ProcessorStateDatabase(root.Path);
        var durable = new DriveAccountRepository(reopened);
        Assert.AreEqual("abandoned", durable.PendingCleanup().Single().Reason);
        Assert.ThrowsExactly<ArgumentException>(() => durable.Begin(id));
        Assert.ThrowsExactly<StaleDriveConnectionException>(() => durable.Promote(id, "permission-A", "A", null));
        durable.ConfirmCleanup(id);
    }

    [TestMethod]
    public void CandidateSchemaOneMigratesWithoutLosingExistingJobContextsOrInstallationIdentity()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        var job = Guid.NewGuid(); Guid installation;
        using (var state = new ProcessorStateDatabase(root.Path))
        {
            installation = state.InstallationId;
            var jobs = new DurableJobRepository(state);
            jobs.Create(job, "takeout-import", "{\"destination\":\"captured-A\"}"u8);
            jobs.AddItem(job, new(Guid.NewGuid(), new string('a', 64), [new("upload-original", new string('b', 64), [])]));
        }
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root.Path, "processor.db"), Pooling = false }.ToString()))
        {
            old.Open(); using var command = old.CreateCommand();
            command.CommandText = "DROP TABLE workspace_inventory_checkpoint; DROP TABLE workspace_document_observation; DROP INDEX workspace_publication_generation; DROP TABLE catalog_generation_capture; DROP TABLE signing_intent; DROP TABLE signing_actor; DROP TABLE workspace_publication; DROP TABLE current_source_scan; ALTER TABLE job_item DROP COLUMN captured_context; DROP TABLE source_asset; DROP TABLE discovered_asset; DROP TABLE discovery_revision; DROP TABLE configuration_outbox; DROP TABLE source_binding; DROP TABLE library_configuration; DROP TABLE pending_drive_connection; DROP TABLE credential_cleanup; DROP TABLE drive_account; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        using var upgraded = new ProcessorStateDatabase(root.Path);
        Assert.AreEqual(installation, upgraded.InstallationId);
        var migratedJobs = new DurableJobRepository(upgraded);
        Assert.IsFalse(migratedJobs.Summary(job).IsCanceled);
        Assert.AreEqual("{\"destination\":\"captured-A\"}", migratedJobs.ClaimNext()!.CapturedContext);
        var accounts = new DriveAccountRepository(upgraded);
        accounts.Begin(Guid.NewGuid());
        Assert.AreEqual(1, accounts.PendingConnections().Count);
    }

    [TestMethod]
    public async Task SharedStateCoordinatorSerializesDifferentRepositoriesAndJoinsActiveWork()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var database = new ProcessorStateDatabase(root.Path);
        var state = new ProcessorStateCoordinator(database);
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.Select(id => state.ExecuteAsync(db => new DriveAccountRepository(db).Begin(id), CancellationToken.None))).ConfigureAwait(false);
        Assert.AreEqual(12, (await state.ExecuteAsync(db => new DriveAccountRepository(db).PendingConnections(), CancellationToken.None).ConfigureAwait(false)).Count);
        using var release = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = state.ExecuteAsync(_ =>
        {
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None)) throw new TimeoutException("State fixture was not released.");
            return true;
        }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stop = state.DisposeAsync().AsTask();
        try { Assert.IsFalse(stop.IsCompleted); }
        finally { release.Set(); }
        Assert.IsTrue(await active.ConfigureAwait(false));
        await stop.ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => state.ExecuteAsync(_ => true, CancellationToken.None)).ConfigureAwait(false);
        await state.DisposeAsync().ConfigureAwait(false);
    }
}
