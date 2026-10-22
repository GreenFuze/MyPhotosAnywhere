using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class FrozenCatalogSnapshotTests
{
    [TestMethod]
    public void FrozenFactsAndCoverageSurviveSourceChangesAndReleaseTheLiveDatabase()
    {
        using var fixture = new Fixture(); fixture.Add([Fact("old", -1), Fact("missing-date", null)]);
        var live = CatalogFactsSnapshot.Open(fixture.Database); var revision = live.ConfigurationRevision;
        fixture.Add([Fact("new", 300), Fact("old", 500) with { DriveVersion = 2 }]);
        fixture.Sources.Update(fixture.Source, false, true, 1, fixture.Sources.Snapshot().Revision);
        using var frozen = FrozenCatalogSnapshot.CaptureAndRelease(live, fixture.Directory, fixture.Budget);
        Assert.ThrowsExactly<ObjectDisposedException>(() => live.ReadPage("")); fixture.Database.Dispose();
        Assert.AreEqual(2L, frozen.Count); Assert.AreEqual(revision, frozen.ConfigurationRevision);
        Assert.IsTrue(frozen.Coverage.Single().Enabled); Assert.AreEqual("pending", frozen.Coverage.Single().State);
        var old = frozen.ReadPage("").Assets.Single(asset => asset.FileId == "old");
        Assert.AreEqual(1L, old.DriveVersion); Assert.AreEqual(-1L, old.EffectiveUtc);
        Assert.IsTrue(frozen.ReadPage("").Assets.All(asset => asset.FileId != "new"));
    }

    [TestMethod]
    public void BoundedKeysetAndPartitionReadsRetainEveryCapturedIdentity()
    {
        using var fixture = new Fixture();
        for (var offset = 0; offset < 640; offset += 250)
            fixture.Add(Enumerable.Range(offset, Math.Min(250, 640 - offset)).Select(index => Fact("file-" + index.ToString(CultureInfo.InvariantCulture), index)).ToArray());
        using var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), fixture.Directory, fixture.Budget);
        var all = new List<Guid>(); Guid? after = null;
        while (true)
        {
            var page = frozen.ReadPage("", after, 17); all.AddRange(page.Assets.Select(asset => asset.Id));
            if (!page.HasMore) break; Assert.AreEqual(17, page.Assets.Count); after = page.Assets[^1].Id;
        }
        Assert.AreEqual(640, all.Count); Assert.AreEqual(640, all.Distinct().Count());
        var partitions = Enumerable.Range(0, 256).SelectMany(part => frozen.ReadPage(part.ToString("x2", CultureInfo.InvariantCulture)).Assets).ToArray();
        Assert.AreEqual(640, partitions.Length); CollectionAssert.AreEquivalent(all, partitions.Select(asset => asset.Id).ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => frozen.ReadPage("abc"));
        Assert.ThrowsExactly<ArgumentException>(() => frozen.ReadPage("ff", Guid.Parse("11111111-1111-4111-8111-111111111111")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frozen.ReadPage("", limit: 251));
    }

    [TestMethod]
    public void LowStorageAndCancellationReleaseTheSnapshotAndGeneratedReservation()
    {
        using var fixture = new Fixture(); fixture.Add([Fact("retained", 1)]); fixture.Space.Available = 1;
        var live = CatalogFactsSnapshot.Open(fixture.Database);
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => FrozenCatalogSnapshot.CaptureAndRelease(live, fixture.Directory, fixture.Budget));
        Assert.ThrowsExactly<ObjectDisposedException>(() => live.ReadPage("")); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Space.Available = 20 * StorageBudgetManager.Gibibyte; using var caller = new CancellationTokenSource(); var reads = 0;
        fixture.Space.OnRead = () => { if (++reads == 3) caller.Cancel(); };
        live = CatalogFactsSnapshot.Open(fixture.Database);
        Assert.ThrowsExactly<OperationCanceledException>(() => FrozenCatalogSnapshot.CaptureAndRelease(live, fixture.Directory, fixture.Budget, caller.Token));
        fixture.Space.OnRead = null; Assert.ThrowsExactly<ObjectDisposedException>(() => live.ReadPage(""));
        Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes); Assert.AreEqual(0, Directory.GetFiles(fixture.CapturePath, "*.sqlite").Length);
        using var current = CatalogFactsSnapshot.Open(fixture.Database); Assert.AreEqual(1, current.ReadPage("").Assets.Count);
    }

    [TestMethod]
    [DataRow("UPDATE fact SET payload=X'7b7d'")]
    [DataRow("UPDATE fact SET id='00000000-0000-4000-8000-000000000001'")]
    public void CorruptOrSubstitutedGeneratedFactsNeverReplaceTheCapturedPhoto(string corruption)
    {
        using var fixture = new Fixture(); fixture.Add([Fact("retained", 1)]);
        using var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), fixture.Directory, fixture.Budget);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Directory.GetFiles(fixture.CapturePath, "*.sqlite").Single(), Pooling = false }.ToString()))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = corruption; command.ExecuteNonQuery(); }
        Assert.ThrowsExactly<InvalidDataException>(() => frozen.ReadPage(""));
    }

    [TestMethod]
    public void CapacityFailureReleasesOnlyTheNewSnapshotAndDirectoryOutlivesExistingCaptures()
    {
        using var fixture = new Fixture(); fixture.Add([Fact("retained", 1)]);
        using var first = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), fixture.Directory, fixture.Budget);
        using var second = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), fixture.Directory, fixture.Budget);
        var live = CatalogFactsSnapshot.Open(fixture.Database);
        Assert.ThrowsExactly<CatalogPreparationCapacityException>(() => FrozenCatalogSnapshot.CaptureAndRelease(live, fixture.Directory, fixture.Budget));
        Assert.ThrowsExactly<ObjectDisposedException>(() => live.ReadPage("")); Assert.ThrowsExactly<InvalidOperationException>(fixture.Directory.Dispose);
        Assert.AreEqual(1, first.ReadPage("").Assets.Count); Assert.AreEqual(1, second.ReadPage("").Assets.Count);
    }

    [TestMethod]
    public void EmptyAndUnicodeCapturesUseMetadataOnlyAndCleanupIsIdempotent()
    {
        using var fixture = new Fixture();
        using (var empty = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), fixture.Directory, fixture.Budget))
        { Assert.AreEqual(0L, empty.Count); Assert.AreEqual(0, empty.ReadPage("").Assets.Count); Assert.IsFalse(empty.ReadPage("").HasMore); }
        fixture.Add([Fact("unicode", 1) with { Name = new string('ש', 4000) }]);
        var frozen = FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(fixture.Database), fixture.Directory, fixture.Budget);
        Assert.AreEqual(new string('ש', 4000), frozen.ReadPage("").Assets.Single().Name); frozen.Dispose(); frozen.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => frozen.ReadPage("")); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(10000)]
    [DataRow(100000)]
    public void LargeCapturesMeasureStorageAndMemoryWhileAnImportWritesBesideTheirLiveObservation(int count)
    {
        using var fixture = new Fixture();
        for (var offset = 0; offset < count; offset += 250)
            fixture.Add(Enumerable.Range(offset, Math.Min(250, count - offset)).Select(index => Fact("scale-" + index.ToString(CultureInfo.InvariantCulture), index)).ToArray());
        var live = CatalogFactsSnapshot.Open(fixture.Database); var reads = 0; var importMs = -1.0;
        fixture.Space.OnRead = () =>
        {
            if (++reads != 3) return;
            var write = System.Diagnostics.Stopwatch.StartNew(); fixture.Add([Fact("inserted-during-capture", -1)]); importMs = write.Elapsed.TotalMilliseconds;
        };
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true); var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var frozen = FrozenCatalogSnapshot.CaptureAndRelease(live, fixture.Directory, fixture.Budget); var captureMs = clock.Elapsed.TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore; var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
        fixture.Space.OnRead = null; Assert.AreEqual((long)count, frozen.Count); Assert.IsTrue(importMs >= 0);
        clock.Restart(); var first = frozen.ReadPage(""); var firstPageMs = clock.Elapsed.TotalMilliseconds;
        Assert.AreEqual(250, first.Assets.Count); Assert.IsTrue(first.HasMore);
        var inserted = new Mpa.Processor.Protocol.DriveAssetIdentity(frozen.LibraryId).Derive("account", "inserted-during-capture");
        Assert.IsNotNull(new DiscoveredCatalogRepository(fixture.Database).ResolveVisible(inserted));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { frozenCatalogFixture = count, captureMs, firstPageMs, importDuringCaptureMs = importMs,
            cumulativeManagedAllocationBytes = allocated, managedBeforeBytes = managedBefore, managedAfterBytes = managedAfter,
            generatedMetadataBytes = new FileInfo(Directory.GetFiles(fixture.CapturePath, "*.sqlite").Single()).Length,
            reservedBytes = fixture.Budget.Status().ReservedBytes, retainedReadPageBound = 250,
            scope = "private metadata staging and interleaved import write; shared-test-process memory; native UI, compression and Drive publication remain separate" }));
    }

    private static DiscoveredDriveAsset Fact(string file, long? capture) => new(file, 1, "Photo.jpg", "image/jpeg", 40, 30, capture, null, null, 0);
    private sealed class Space : IStorageSpaceReader
    {
        public long Available { get; set; } = 20 * StorageBudgetManager.Gibibyte;
        public Action? OnRead { get; set; }
        public StorageSpaceSnapshot Read() { OnRead?.Invoke(); return new(100 * StorageBudgetManager.Gibibyte, Available, 0); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly WorkerProcessTests.WorkerTestDirectory _root = new();
        public ProcessorStateDatabase Database { get; }
        public CatalogPreparationDirectory Directory { get; }
        public string CapturePath => Path.Combine(_root.Path, "catalog-preparations");
        public LibrarySourceRepository Sources { get; }
        public Guid Source { get; } = Guid.NewGuid();
        public Space Space { get; } = new();
        public StorageBudgetManager Budget { get; }
        public Fixture()
        {
            Database = new(_root.Path); var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account", "Synthetic", null);
            _ = new ProcessorLibraryRepository(Database).GetOrCreateIdentity(); Sources = new(Database); Sources.Add(Source, "account", 1, "photos", "Photos", true, 1);
            Directory = new(_root.Path, Database.InstallationId); Budget = new(Space);
        }
        public void Add(DiscoveredDriveAsset[] facts) => new DiscoveredCatalogRepository(Database).ApplyBatch(Source, 1, 1, facts);
        public void Dispose() { Directory.Dispose(); Database.Dispose(); _root.Dispose(); }
    }
}
