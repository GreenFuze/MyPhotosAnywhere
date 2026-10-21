using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class ProcessorStateStorageTests
{
    [TestMethod]
    public void ActualStateVolumeAndKnownFilesAreMeasuredWithoutIncludingOtherFilesOrChangingBytes()
    {
        using var fixture = new Fixture();
        var reader = new ProcessorStateStorageReader(fixture.Root);
        var before = reader.Read();
        Assert.IsGreaterThan(0L, before.CapacityBytes);
        Assert.IsTrue(before.AvailableBytes >= 0 && before.AvailableBytes <= before.CapacityBytes);
        Assert.IsGreaterThan(0L, before.OwnedCacheBytes);
        var original = Path.Combine(fixture.Root, "never-owned-original.jpg");
        File.WriteAllBytes(original, new byte[1024 * 1024]);
        var after = reader.Read();
        Assert.AreEqual(before.OwnedCacheBytes, after.OwnedCacheBytes);
        Assert.AreEqual(1024 * 1024, new FileInfo(original).Length);
        Assert.AreEqual(before.CapacityBytes, after.CapacityBytes);
    }

    [TestMethod]
    public void MissingStateOrAnUnexpectedSidecarDirectoryCannotBecomeZeroUsage()
    {
        using var fixture = new Fixture();
        var reader = new ProcessorStateStorageReader(fixture.Root);
        fixture.Database.Dispose();
        var sidecar = Path.Combine(fixture.Root, "processor.db-wal");
        Directory.CreateDirectory(sidecar);
        Assert.ThrowsExactly<IOException>(() => reader.Read());
        Directory.Delete(sidecar);
        File.Move(Path.Combine(fixture.Root, "processor.db"), Path.Combine(fixture.Root, "retained.db"));
        Assert.ThrowsExactly<FileNotFoundException>(() => reader.Read());
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => new ProcessorStateStorageReader(Path.Combine(fixture.Root, "absent")).Read());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mpa-state-budget-" + Guid.NewGuid().ToString("N"));
        public ProcessorStateDatabase Database { get; }
        public Fixture() => Database = new(Root);
        public void Dispose() { Database.Dispose(); Directory.Delete(Root, true); }
    }
}
