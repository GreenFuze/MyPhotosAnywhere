using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class CatalogPreparationDirectoryTests
{
    [TestMethod]
    public void UnmarkedDirectoryIsNeverClaimedOrCleaned()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        var path = Path.Combine(root.Path, "catalog-preparations"); Directory.CreateDirectory(path);
        var original = Path.Combine(path, "original.jpg"); File.WriteAllText(original, "synthetic protected content");
        Assert.ThrowsExactly<InvalidDataException>(() => new CatalogPreparationDirectory(root.Path, database.InstallationId));
        Assert.AreEqual("synthetic protected content", File.ReadAllText(original));
    }

    [TestMethod]
    public void RecoveryValidatesTheWholeInventoryBeforeDeletingAnyStaleCapture()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var database = new ProcessorStateDatabase(root.Path);
        var path = Path.Combine(root.Path, "catalog-preparations"); byte[] marker;
        using (var directory = new CatalogPreparationDirectory(root.Path, database.InstallationId)) marker = File.ReadAllBytes(Path.Combine(path, "owner.json"));
        Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path, "owner.json"), marker);
        var capture = Path.Combine(path, "capture-" + Guid.NewGuid().ToString("N") + ".sqlite"); File.WriteAllText(capture, "synthetic stale generated content");
        var unknown = Path.Combine(path, "unexpected.txt"); File.WriteAllText(unknown, "retain this too");
        Assert.ThrowsExactly<InvalidDataException>(() => new CatalogPreparationDirectory(root.Path, database.InstallationId));
        Assert.AreEqual("synthetic stale generated content", File.ReadAllText(capture)); Assert.AreEqual("retain this too", File.ReadAllText(unknown));
        File.Delete(unknown);
        using var recovered = new CatalogPreparationDirectory(root.Path, database.InstallationId); Assert.IsFalse(File.Exists(capture));
    }
}
