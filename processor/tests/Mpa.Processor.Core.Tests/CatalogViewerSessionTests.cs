using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed partial class CatalogViewerSessionTests
{
    [TestMethod]
    public void CancellationDuringStreamingRollsBackAndReleasesThePrivateIndexWithoutChangingMediaState()
    {
        using var fixture = new Fixture(); fixture.Add(1000); var selected = fixture.Catalog.ReadPage().Assets[30].Id;
        using var canceled = new CancellationTokenSource(); var reads = 0;
        fixture.Space.Observed = () => { if (++reads == 3) canceled.Cancel(); };
        Assert.ThrowsExactly<OperationCanceledException>(() => CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected, null, fixture.Budget, canceled.Token));
        fixture.Space.Observed = null;
        Assert.IsFalse(File.Exists(fixture.IndexPath)); Assert.IsFalse(File.Exists(fixture.IndexPath + "-journal")); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        Assert.AreEqual(selected, fixture.Catalog.ReadPage().Assets[30].Id);
        using var index = CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected, null, fixture.Budget, CancellationToken.None);
        Assert.AreEqual(1000L, index.Count);
    }
    [TestMethod]
    public void SnapshotKeepsItsOpenedOrderDuringNewImportsDateChangesAndSourceRemoval()
    {
        using var fixture = new Fixture(); fixture.Add(137);
        var initial = fixture.Catalog.ReadPage(limit: 90); var selected = initial.Assets[70];
        using var index = CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected.Id, null, fixture.Budget, CancellationToken.None);
        Assert.AreEqual(137L, index.Count); Assert.AreEqual(70L, index.SelectedPosition); Assert.IsTrue(fixture.Budget.Status().ReservedBytes > 0);
        var opened = index.ReadWindow(index.SelectedPosition, before: 30, after: 30);
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("newer", 999), Fact("older", -1), Fact(selected.Facts.FileId, 1000) with { DriveVersion = 2 }]);
        var source = fixture.Sources.Snapshot(); fixture.Sources.Update(fixture.Source, false, true, 1, source.Revision);
        Assert.IsNull(fixture.Catalog.ResolveVisible(selected.Id));
        CollectionAssert.AreEqual(opened.ToArray(), index.ReadWindow(index.SelectedPosition).ToArray());
        Assert.AreEqual(selected.Id, index.ReadWindow(index.SelectedPosition, 0, 0).Single().AssetId);
        Assert.AreEqual(1, index.ReadWindow(136, 0, 0).Count);
        index.Dispose(); Assert.IsTrue(index.ExitConfirmed); Assert.IsFalse(File.Exists(fixture.IndexPath)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => index.ReadWindow(0));
    }

    [TestMethod]
    public void CaptureHonorsAccountContextAndDoesNotReplaceAnUnavailableSelection()
    {
        using var fixture = new Fixture(); fixture.Add(1); var selected = fixture.Catalog.ReadPage().Assets.Single().Id;
        Assert.ThrowsExactly<CatalogSessionSelectionUnavailableException>(() => CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected,
            "other-account", fixture.Budget, CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.IndexPath)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        using var index = CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected, "account", fixture.Budget, CancellationToken.None);
        Assert.AreEqual(selected, index.ReadWindow(0).Single().AssetId);
    }

    [TestMethod]
    public void CancellationLowSpaceAndExistingFilesNeverPublishOrDeleteAnUnownedIndex()
    {
        using var fixture = new Fixture(); fixture.Add(1); var selected = fixture.Catalog.ReadPage().Assets.Single().Id;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected, null, fixture.Budget, canceled.Token));
        Assert.IsFalse(File.Exists(fixture.IndexPath)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        fixture.Space.Available = 0;
        Assert.ThrowsExactly<InsufficientStorageBudgetException>(() => CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected, null, fixture.Budget, CancellationToken.None));
        fixture.Space.Available = 50 * StorageBudgetManager.Gibibyte;
        File.WriteAllText(fixture.IndexPath, "synthetic existing content");
        Assert.ThrowsExactly<IOException>(() => CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected, null, fixture.Budget, CancellationToken.None));
        Assert.AreEqual("synthetic existing content", File.ReadAllText(fixture.IndexPath)); Assert.AreEqual(0L, fixture.Budget.Status().ReservedBytes);
        File.Delete(fixture.IndexPath); File.WriteAllText(fixture.IndexPath + "-journal", "synthetic unowned sidecar");
        Assert.ThrowsExactly<IOException>(() => CatalogViewerSessionIndex.Capture(fixture.Database, fixture.IndexPath, selected, null, fixture.Budget, CancellationToken.None));
        Assert.AreEqual("synthetic unowned sidecar", File.ReadAllText(fixture.IndexPath + "-journal")); Assert.IsFalse(File.Exists(fixture.IndexPath));
    }

    private static DiscoveredDriveAsset Fact(string file, long? captured) => new(file, 1, file + ".jpg", "image/jpeg", 40, 30, captured, null, null, 0);
    private sealed class Space : IStorageSpaceReader
    {
        public long Available { get; set; } = 50 * StorageBudgetManager.Gibibyte;
        public Action? Observed { get; set; }
        public StorageSpaceSnapshot Read() { Observed?.Invoke(); return new(100 * StorageBudgetManager.Gibibyte, Available, 0); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly WorkerProcessTests.WorkerTestDirectory _directory = new();
        public ProcessorStateDatabase Database { get; }
        public LibrarySourceRepository Sources { get; }
        public DiscoveredCatalogRepository Catalog { get; }
        public Space Space { get; } = new();
        public StorageBudgetManager Budget { get; }
        public Guid Source { get; } = Guid.NewGuid();
        public string DirectoryPath => _directory.Path;
        public string IndexPath => Path.Combine(_directory.Path, "private-viewer.sqlite");
        public Fixture()
        {
            Database = new(_directory.Path); Sources = new(Database); Catalog = new(Database); Budget = new(Space);
            var accounts = new DriveAccountRepository(Database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account", "Synthetic", null);
            Sources.Add(Source, "account", 1, "folder", "Synthetic", true, Sources.Snapshot().Revision);
        }
        public void Add(int count)
        {
            for (var offset = 0; offset < count; offset += 250)
                Catalog.ApplyBatch(Source, 1, 1, Enumerable.Range(offset, Math.Min(250, count - offset)).Select(index => Fact("file-" + index.ToString(CultureInfo.InvariantCulture), index / 7)).ToArray());
        }
        public void Dispose() { Database.Dispose(); _directory.Dispose(); }
    }
}
