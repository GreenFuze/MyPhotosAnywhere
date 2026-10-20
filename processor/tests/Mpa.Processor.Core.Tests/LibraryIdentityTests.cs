using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class LibraryIdentityTests
{
    [TestMethod]
    public void LibraryIdentitySurvivesRestartAndDoesNotReplaceInstallationOrAccountBindings()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); Guid library; Guid installation;
        using (var database = new ProcessorStateDatabase(root.Path))
        {
            installation = database.InstallationId;
            library = new ProcessorLibraryRepository(database).GetOrCreateIdentity();
            Assert.AreEqual(library, new ProcessorLibraryRepository(database).GetOrCreateIdentity());
            var accounts = new DriveAccountRepository(database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "synthetic-account", "Synthetic", null);
        }
        using var reopened = new ProcessorStateDatabase(root.Path);
        Assert.AreEqual(installation, reopened.InstallationId); Assert.AreEqual(library, new ProcessorLibraryRepository(reopened).GetOrCreateIdentity());
        Assert.AreEqual("synthetic-account", new DriveAccountRepository(reopened).Accounts().Single().PermissionId);
    }
    [TestMethod]
    public void CorruptLibraryIdentityIsPreservedAndNeverCreatesReplacementKeys()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using (var database = new ProcessorStateDatabase(root.Path)) new ProcessorLibraryRepository(database).GetOrCreateIdentity();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root.Path, "processor.db"), Pooling = false }.ToString()))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE processor_meta SET value='invalid' WHERE key='library_id'"; Assert.AreEqual(1, command.ExecuteNonQuery()); }
        using var preserved = new ProcessorStateDatabase(root.Path);
        Assert.ThrowsExactly<InvalidDataException>(() => new ProcessorLibraryRepository(preserved).GetOrCreateIdentity());
        Assert.ThrowsExactly<InvalidDataException>(() => new ProcessorLibraryRepository(preserved).GetOrCreateIdentity());
    }
}
