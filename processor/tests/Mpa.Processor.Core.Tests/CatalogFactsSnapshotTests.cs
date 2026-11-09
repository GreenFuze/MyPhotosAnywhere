using System.Globalization;
using Mpa.Processor.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class CatalogFactsSnapshotTests
{
    [TestMethod]
    public void FrozenProjectionRetainsFactsAndMembershipsWhileTheLiveCatalogChanges()
    {
        using var fixture = new Fixture();
        fixture.Catalog.ApplyBatch(fixture.First, 1, 1, [Fact("same-file", -1)]);
        fixture.Catalog.ApplyBatch(fixture.Second, 1, 1, [Fact("same-file", -1) with { Depth = 2 }]);
        using var opened = CatalogFactsSnapshot.Open(fixture.Database);
        var original = opened.ReadPage("").Assets.Single();
        Assert.AreEqual(2, original.Sources.Count); Assert.AreEqual(2, original.Sources[1].Depth);
        fixture.Catalog.ApplyBatch(fixture.First, 1, 1, [Fact("same-file", 100) with { DriveVersion = 2 }, Fact("new-file", 0)]);
        fixture.Sources.Update(fixture.Second, false, true, 1, fixture.Sources.Snapshot().Revision);
        using var current = CatalogFactsSnapshot.Open(fixture.Database);
        var changed = current.ReadPage(""); Assert.AreEqual(2, changed.Assets.Count);
        Assert.AreEqual(1, changed.Assets.Single(item => item.FileId == "same-file").Sources.Count);
        Assert.IsTrue(current.DiscoveryRevision > opened.DiscoveryRevision);
        Assert.IsTrue(current.ConfigurationRevision > opened.ConfigurationRevision);
        var retained = opened.ReadPage("").Assets.Single();
        Assert.AreEqual(original.Id, retained.Id); Assert.AreEqual(-1L, retained.EffectiveUtc);
        Assert.AreEqual(1L, retained.DriveVersion); Assert.AreEqual(2, retained.Sources.Count);
        var prefix = retained.Id.ToString("N")[..2];
        var wire = new DriveCatalogFacts(opened.LibraryId);
        Assert.AreEqual(retained.Id, wire.Read(wire.Encode(prefix, [retained]), prefix).Assets.Single().Id);
    }

    [TestMethod]
    public void KeysetPagesAndEveryPartitionPreserveAllIdentitiesWithoutDuplicates()
    {
        using var fixture = new Fixture();
        for (var offset = 0; offset < 640; offset += 250)
            fixture.Catalog.ApplyBatch(fixture.First, 1, 1, Enumerable.Range(offset, Math.Min(250, 640 - offset))
                .Select(index => Fact("file-" + index.ToString(CultureInfo.InvariantCulture), index % 3 == 0 ? null : index - 300)).ToArray());
        using var snapshot = CatalogFactsSnapshot.Open(fixture.Database);
        var complete = ReadAll(snapshot, "", 17); Assert.AreEqual(640, complete.Count);
        CollectionAssert.AreEqual(complete.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray(), complete.ToArray());
        Assert.AreEqual(640, complete.Distinct().Count());
        var partitioned = new List<Guid>();
        foreach (var prefix in complete.Select(id => id.ToString("N")[..2]).Distinct().Order(StringComparer.Ordinal))
        {
            var ids = ReadAll(snapshot, prefix, 3);
            Assert.IsTrue(ids.All(id => id.ToString("N").StartsWith(prefix, StringComparison.Ordinal)));
            partitioned.AddRange(ids);
        }
        CollectionAssert.AreEqual(complete.ToArray(), partitioned.ToArray());
        var deepPrefix = complete[0].ToString("N")[..8];
        CollectionAssert.AreEqual(complete.Where(id => id.ToString("N").StartsWith(deepPrefix, StringComparison.Ordinal)).ToArray(),
            ReadAll(snapshot, deepPrefix, 1).ToArray());
    }

    [TestMethod]
    public void VisibilityUsesEnabledSourcesAndDepthWhileDisconnectedMetadataRemainsAvailable()
    {
        using var fixture = new Fixture();
        fixture.Catalog.ApplyBatch(fixture.First, 1, 1, [Fact("same-name", null)]);
        fixture.Catalog.ApplyBatch(fixture.Second, 1, 1, [Fact("descendant", 10) with { Depth = 1 }]);
        var pending = fixture.Accounts.Begin(Guid.NewGuid()); fixture.Accounts.Promote(pending.Id, "other-account", "Other", null);
        var third = Guid.NewGuid(); fixture.Sources.Add(third, "other-account", 1, "third-folder", "Other", true, fixture.Sources.Snapshot().Revision);
        fixture.Catalog.ApplyBatch(third, 1, 1, [Fact("same-name", null)]);
        fixture.Sources.Update(fixture.Second, true, false, 1, fixture.Sources.Snapshot().Revision);
        fixture.Accounts.Disconnect("account", 1);
        using var snapshot = CatalogFactsSnapshot.Open(fixture.Database);
        var rows = snapshot.ReadPage("").Assets;
        Assert.AreEqual(2, rows.Count); Assert.IsTrue(rows.All(item => item.FileId == "same-name"));
        Assert.AreNotEqual(rows[0].Id, rows[1].Id); Assert.AreEqual(2, rows.Select(item => item.AccountId).Distinct().Count());
        Assert.IsTrue(rows.All(item => item.EffectiveUtc is null));
    }

    [TestMethod]
    public void CancellationAndFailedCaptureRetainActualSnapshotOwnership()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory();
        using var database = new ProcessorStateDatabase(directory.Path);
        Assert.ThrowsExactly<InvalidDataException>(() => CatalogFactsSnapshot.Open(database));
        _ = new ProcessorLibraryRepository(database).GetOrCreateIdentity();
        using var snapshot = CatalogFactsSnapshot.Open(database);
        Assert.ThrowsExactly<InvalidOperationException>(database.Dispose);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => snapshot.ReadPage("", cancellationToken: canceled.Token));
        Assert.ThrowsExactly<ArgumentException>(() => snapshot.ReadPage("abc"));
        Assert.ThrowsExactly<ArgumentException>(() => snapshot.ReadPage("ff", Guid.Parse("11111111-1111-4111-8111-111111111111")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => snapshot.ReadPage("", limit: 251));
        Assert.AreEqual(0, snapshot.ReadPage("").Assets.Count);
        snapshot.Dispose(); snapshot.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => snapshot.ReadPage(""));
        database.Dispose();
    }

    private static List<Guid> ReadAll(CatalogFactsSnapshot snapshot, string prefix, int limit)
    {
        var result = new List<Guid>(); Guid? after = null;
        for (var pageNumber = 0; pageNumber < 1000; pageNumber++)
        {
            var page = snapshot.ReadPage(prefix, after, limit); result.AddRange(page.Assets.Select(item => item.Id));
            if (!page.HasMore) return result;
            Assert.AreEqual(limit, page.Assets.Count); after = page.Assets[^1].Id;
        }
        throw new InvalidOperationException("Synthetic partition paging did not terminate.");
    }
    private static DiscoveredDriveAsset Fact(string file, long? captured) => new(file, 1, "Same name.jpg", "image/jpeg", 40, 30, captured, null, null, 0);
    private sealed class Fixture : IDisposable
    {
        private readonly WorkerProcessTests.WorkerTestDirectory _directory = new();
        public ProcessorStateDatabase Database { get; }
        public DriveAccountRepository Accounts { get; }
        public LibrarySourceRepository Sources { get; }
        public DiscoveredCatalogRepository Catalog { get; }
        public Guid First { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");
        public Guid Second { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");
        public Fixture()
        {
            Database = new(_directory.Path); Accounts = new(Database); Sources = new(Database); Catalog = new(Database);
            var pending = Accounts.Begin(Guid.NewGuid()); Accounts.Promote(pending.Id, "account", "Synthetic", null);
            Sources.Add(First, "account", 1, "first-folder", "First", true, Sources.Snapshot().Revision);
            Sources.Add(Second, "account", 1, "second-folder", "Second", true, Sources.Snapshot().Revision);
        }
        public void Dispose() { Database.Dispose(); _directory.Dispose(); }
    }
}
