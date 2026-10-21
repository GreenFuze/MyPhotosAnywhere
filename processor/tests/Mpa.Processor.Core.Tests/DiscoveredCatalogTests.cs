using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class DiscoveredCatalogTests
{
    [TestMethod]
    public void OverlappingSourcesShareAnIdentityButDistinctAccountFilesNeverMerge()
    {
        using var fixture = new Fixture(); var second = fixture.AddSource("account-A", "folder-B");
        var otherAccount = fixture.AddSource("account-B", "folder-A"); var fact = Fact("same-file", 100);
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [fact]);
        fixture.Catalog.ApplyBatch(second, 1, 1, [fact with { Depth = 2 }]);
        fixture.Catalog.ApplyBatch(otherAccount, 1, 1, [fact]);
        var assets = fixture.Catalog.ReadPage().Assets;
        Assert.AreEqual(2, assets.Count); Assert.AreEqual(2, assets.Select(asset => asset.Id).Distinct().Count());
        Assert.AreEqual(1, fixture.Catalog.ReadPage(accountId: "account-A").Assets.Count);
        Assert.AreEqual(0, assets.Single(asset => asset.AccountId == "account-A").Facts.Depth);
        Assert.AreEqual("same-file", assets[0].Facts.FileId);
    }

    [TestMethod]
    public void KnownDatesUseCaptureThenModificationAndUnknownDatesAreLastWithCanonicalTies()
    {
        using var fixture = new Fixture();
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("a", 100), Fact("b", 100),
            Fact("modified", null) with { ModifiedUtc = 90 }, Fact("unknown", null), Fact("old", -100)]);
        var assets = fixture.Catalog.ReadPage().Assets;
        CollectionAssert.AreEqual(new long?[] { 100, 100, 90, -100, null }, assets.Select(asset => asset.EffectiveUtc).ToArray());
        Assert.IsTrue(string.CompareOrdinal(assets[0].Id.ToString("D"), assets[1].Id.ToString("D")) < 0);
        Assert.AreEqual(CatalogDateProvenance.DriveCapture, assets[0].DateProvenance);
        Assert.AreEqual(CatalogDateProvenance.DriveModification, assets[2].DateProvenance);
        Assert.AreEqual(CatalogDateProvenance.Unknown, assets[4].DateProvenance);
        Assert.IsNull(assets[4].Facts.ModifiedUtc);
    }

    [TestMethod]
    public void KeysetPagingWorksBothWaysAcrossTiesUnknownDatesAndDirectDateSeeking()
    {
        using var fixture = new Fixture();
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, Enumerable.Range(0, 137).Select(index =>
            Fact("file-" + index.ToString(CultureInfo.InvariantCulture), index < 120 ? index / 7 : null)).ToArray());
        var expected = fixture.Catalog.ReadPage(limit: 90).Assets.Concat(fixture.Catalog.ReadPage(
            Cursor(fixture.Catalog.ReadPage(limit: 90).Assets[^1]), limit: 90).Assets).ToArray();
        var actual = new List<CatalogAsset>(); CatalogCursor? cursor = null;
        do
        {
            var page = fixture.Catalog.ReadPage(cursor, limit: 11);
            actual.AddRange(page.Assets); if (!page.HasMore) break;
            cursor = Cursor(page.Assets[^1]);
        } while (true);
        Assert.AreEqual(137, actual.Count); Assert.AreEqual(137, actual.Select(asset => asset.Id).Distinct().Count());
        CollectionAssert.AreEqual(expected.Select(asset => asset.Id).ToArray(), actual.Select(asset => asset.Id).ToArray());
        var backwards = fixture.Catalog.ReadPage(Cursor(actual[22]), limit: 11, before: true);
        CollectionAssert.AreEqual(actual.Skip(11).Take(11).Select(asset => asset.Id).ToArray(), backwards.Assets.Select(asset => asset.Id).ToArray());
        var seek = fixture.Catalog.SeekDate(10, limit: 11);
        CollectionAssert.AreEqual(actual.Where(asset => asset.EffectiveUtc is null or <= 10).Take(11).Select(asset => asset.Id).ToArray(), seek.Assets.Select(asset => asset.Id).ToArray());
    }

    [TestMethod]
    public void SameRevisionConflictRollsBackItsBatchAndOlderDiscoveryCannotOverwriteNewFacts()
    {
        using var fixture = new Fixture();
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("existing", 100) with { DriveVersion = 2 }]);
        var prior = fixture.Catalog.ReadPage();
        Assert.ThrowsExactly<ConflictingDiscoveryException>(() => fixture.Catalog.ApplyBatch(fixture.Source, 1, 1,
            [Fact("new-before-conflict", 300), Fact("existing", 900) with { DriveVersion = 2 }]));
        Assert.AreEqual(1, fixture.Catalog.ReadPage().Assets.Count); Assert.AreEqual(prior.DiscoveryRevision, fixture.Catalog.ReadPage().DiscoveryRevision);
        Assert.AreEqual(0, fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("existing", 800)]));
        Assert.AreEqual(100L, fixture.Catalog.ReadPage().Assets.Single().EffectiveUtc);
        var oldId = fixture.Catalog.ReadPage().Assets.Single().Id;
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("existing", 500) with { DriveVersion = 3, Name = "Renamed.jpg" }]);
        Assert.AreEqual(oldId, fixture.Catalog.ReadPage().Assets.Single().Id);
        Assert.AreEqual("Renamed.jpg", fixture.Catalog.ResolveVisible(oldId)!.Facts.Name);
    }

    [TestMethod]
    public void SourcePolicyChangesFenceOldScansAndDoNotErasePreviouslyDiscoveredFacts()
    {
        using var fixture = new Fixture();
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("top-level", 100), Fact("nested", 90) with { Depth = 1 }]);
        var nested = fixture.Catalog.ReadPage().Assets.Single(asset => asset.Facts.FileId == "nested").Id;
        var configuration = fixture.Sources.Snapshot(); fixture.Sources.Update(fixture.Source, true, false, 1, configuration.Revision);
        Assert.AreEqual(1, fixture.Catalog.ReadPage().Assets.Count); Assert.IsNull(fixture.Catalog.ResolveVisible(nested));
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("stale", 200)]));
        fixture.Sources.Update(fixture.Source, false, false, 2, configuration.Revision + 1);
        Assert.AreEqual(0, fixture.Catalog.ReadPage().Assets.Count);
        fixture.Sources.Update(fixture.Source, true, true, 3, configuration.Revision + 2);
        Assert.AreEqual(2, fixture.Catalog.ReadPage().Assets.Count); Assert.AreEqual(nested, fixture.Catalog.ResolveVisible(nested)!.Id);
        new DriveAccountRepository(fixture.Database).Disconnect("account-A", 1);
        Assert.AreEqual(2, fixture.Catalog.ReadPage().Assets.Count, "Offline/disconnected sources retain indexed browsing facts.");
        Assert.ThrowsExactly<StaleLibraryConfigurationException>(() => fixture.Catalog.ApplyBatch(fixture.Source, 4, 1, [Fact("no-authority", 300)]));
    }

    [TestMethod]
    public void ImportingNewerAndOlderFactsDoesNotChangeExistingCursorIdentityOrPageOrder()
    {
        using var fixture = new Fixture();
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, Enumerable.Range(0, 90).Select(index => Fact("old-" + index.ToString(CultureInfo.InvariantCulture), 1000 - index)).ToArray());
        var first = fixture.Catalog.ReadPage(limit: 20); var anchor = Cursor(first.Assets[^1]);
        fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("newer", 2000), Fact("older", 100)]);
        var following = fixture.Catalog.ReadPage(anchor, limit: 20);
        Assert.AreEqual(20, following.Assets.Count); Assert.AreEqual(980L, following.Assets[0].EffectiveUtc);
        Assert.AreEqual(anchor.Id, fixture.Catalog.ResolveVisible(anchor.Id)!.Id);
        Assert.IsFalse(following.Assets.Any(asset => first.Assets.Any(prior => prior.Id == asset.Id)));
    }

    [TestMethod]
    public void InvalidBatchesAndCursorsFailBeforeChangingTheProjection()
    {
        using var fixture = new Fixture();
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("good", 1), Fact("bad", long.MinValue)]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, [Fact("repeat", 1), Fact("repeat", 1)]));
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Catalog.ReadPage(new(null, Guid.Empty)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fixture.Catalog.ReadPage(limit: 1000));
        Assert.AreEqual(0, fixture.Catalog.ReadPage().Assets.Count); Assert.AreEqual(1L, fixture.Catalog.ReadPage().DiscoveryRevision);
    }

    [TestMethod]
    public void ReopenAndSchemaThreeUpgradePreserveAccountsSourcesAndUnrelatedJobs()
    {
        using var directory = new WorkerProcessTests.WorkerTestDirectory(); Guid source; Guid library; var job = Guid.NewGuid();
        using (var fixture = new Fixture(directory.Path))
        {
            source = fixture.Source; library = new ProcessorLibraryRepository(fixture.Database).GetOrCreateIdentity();
            fixture.Catalog.ApplyBatch(source, 1, 1, [Fact("durable", 1)]);
            var jobs = new DurableJobRepository(fixture.Database); jobs.Create(job, "source-scan", "{\"destination\":\"captured\"}"u8);
            jobs.AddItem(job, new(Guid.NewGuid(), new string('a', 64), [new("metadata", new string('b', 64), [])]));
        }
        Guid originalId;
        using (var reopened = new ProcessorStateDatabase(directory.Path))
        {
            originalId = new DiscoveredCatalogRepository(reopened).ReadPage().Assets.Single().Id;
            Assert.AreEqual(library, new ProcessorLibraryRepository(reopened).GetOrCreateIdentity());
        }
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.Path, "processor.db"), Pooling = false }.ToString()))
        {
            old.Open(); using var command = old.CreateCommand();
            command.CommandText = "ALTER TABLE job_item DROP COLUMN captured_context; DROP TABLE source_asset; DROP TABLE discovered_asset; DROP TABLE discovery_revision; PRAGMA user_version=3;"; command.ExecuteNonQuery();
        }
        using var upgraded = new ProcessorStateDatabase(directory.Path);
        var sources = new LibrarySourceRepository(upgraded);
        Assert.AreEqual(source, sources.Snapshot().Sources.Single().Id); Assert.AreEqual(1, sources.PendingPublications().Count);
        Assert.AreEqual(library, new ProcessorLibraryRepository(upgraded).GetOrCreateIdentity());
        var claimed = new DurableJobRepository(upgraded).ClaimNext()!; Assert.AreEqual(job, claimed.JobId);
        Assert.AreEqual("{\"destination\":\"captured\"}", claimed.CapturedContext);
        var catalog = new DiscoveredCatalogRepository(upgraded); catalog.ApplyBatch(source, 1, 1, [Fact("durable", 1)]);
        Assert.AreEqual(originalId, catalog.ReadPage().Assets.Single().Id);
    }

    [TestMethod]
    [DataRow(10000)]
    [DataRow(100000)]
    public void LargeDiscoveryFixturesKeepQueriesAndReturnedPagesBounded(int count)
    {
        using var fixture = new Fixture(); var clock = Stopwatch.StartNew();
        for (var offset = 0; offset < count; offset += 250)
            fixture.Catalog.ApplyBatch(fixture.Source, 1, 1, Enumerable.Range(offset, Math.Min(250, count - offset)).Select(index =>
                Fact("scale-" + index.ToString(CultureInfo.InvariantCulture), index / 7)).ToArray());
        var preparation = clock.Elapsed; clock.Restart();
        var page = fixture.Catalog.SeekDate(count / 14); Assert.AreEqual(60, page.Assets.Count);
        var seek = clock.Elapsed; clock.Restart(); long maximumQueryTicks = 0; var seen = 0; CatalogCursor? cursor = null;
        do
        {
            var start = Stopwatch.GetTimestamp(); page = fixture.Catalog.ReadPage(cursor);
            maximumQueryTicks = Math.Max(maximumQueryTicks, Stopwatch.GetTimestamp() - start);
            Assert.IsTrue(page.Assets.Count <= 60); seen += page.Assets.Count;
            if (!page.HasMore) break;
            Assert.AreEqual(60, page.Assets.Count); var next = Cursor(page.Assets[^1]); Assert.AreNotEqual(cursor, next); cursor = next;
        } while (true);
        Assert.AreEqual(count, seen);
        var fullWalkMilliseconds = clock.Elapsed.TotalMilliseconds;
        // Reuse the already prepared 10k/100k database instead of creating another
        // large fixture just to measure the portable metadata projection.
        using (var facts = CatalogFactsSnapshot.Open(fixture.Database))
        {
            var factsClock = Stopwatch.StartNew(); long maximumFactsTicks = 0; var factsSeen = 0; Guid? after = null;
            while (true)
            {
                var start = Stopwatch.GetTimestamp(); var factsPage = facts.ReadPage("", after);
                maximumFactsTicks = Math.Max(maximumFactsTicks, Stopwatch.GetTimestamp() - start);
                Assert.IsTrue(factsPage.Assets.Count <= 250);
                foreach (var asset in factsPage.Assets)
                {
                    if (after is not null) Assert.IsTrue(StringComparer.Ordinal.Compare(after.Value.ToString("D"), asset.Id.ToString("D")) < 0);
                    Assert.AreEqual(1, asset.Sources.Count); after = asset.Id; factsSeen++;
                }
                if (!factsPage.HasMore) break;
                Assert.AreEqual(250, factsPage.Assets.Count);
            }
            Assert.AreEqual(count, factsSeen);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { catalogFactsFixture = count, walkMs = factsClock.Elapsed.TotalMilliseconds,
                maxPageMs = maximumFactsTicks * 1000.0 / Stopwatch.Frequency, retainedAssetPageBound = 250,
                scope = "read-only snapshot projection; compression/publication and concurrent native UI responsiveness remain separate" }));
        }
        using var sessionDirectory = new WorkerProcessTests.WorkerTestDirectory();
        var selected = fixture.Catalog.ReadPage().Assets[30]; var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess(); process.Refresh(); var workingBefore = process.WorkingSet64;
        var allocated = GC.GetTotalAllocatedBytes(precise: true); var snapshotClock = Stopwatch.StartNew();
        using var viewer = CatalogViewerSessionIndex.Capture(fixture.Database, Path.Combine(sessionDirectory.Path, "viewer.sqlite"), selected.Id, null,
            new(new ViewerScaleSpace()), CancellationToken.None);
        var snapshotElapsed = snapshotClock.Elapsed; var captureAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: true); process.Refresh(); var workingAfter = process.WorkingSet64;
        Assert.AreEqual((long)count, viewer.Count); var window = viewer.ReadWindow(count / 2, 44, 45); Assert.AreEqual(90, window.Count);
        Assert.AreEqual(90, window.Select(entry => entry.AssetId).Distinct().Count());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => viewer.ReadWindow(0, 45, 45));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { viewerFixture = count, snapshotMs = snapshotElapsed.TotalMilliseconds,
            cumulativeManagedAllocationBytes = captureAllocated, managedBeforeBytes = managedBefore, managedAfterBytes = managedAfter,
            processWorkingSetBeforeBytes = workingBefore, processWorkingSetAfterBytes = workingAfter, indexBytes = new FileInfo(Path.Combine(sessionDirectory.Path, "viewer.sqlite")).Length, windowBound = 90,
            scope = "single shared-test-process memory observations; native UI/import responsiveness and dedicated process benchmarks remain separate" }));
        Console.WriteLine(JsonSerializerForMetrics(count, preparation.TotalMilliseconds, seek.TotalMilliseconds,
            maximumQueryTicks * 1000.0 / Stopwatch.Frequency, fullWalkMilliseconds));
    }
    private sealed class ViewerScaleSpace : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 50 * StorageBudgetManager.Gibibyte, 0); }
    private static string JsonSerializerForMetrics(int count, double preparation, double seek, double maxPage, double fullWalk)
        => System.Text.Json.JsonSerializer.Serialize(new { fixture = count, preparationMs = preparation, seekMs = seek, maxPageMs = maxPage,
            fullWalkMs = fullWalk, retainedPageBound = 60, scope = "local discovery projection only; publication, import and native UI responsiveness remain separate" });
    private static CatalogCursor Cursor(CatalogAsset asset) => new(asset.EffectiveUtc, asset.Id);
    private static DiscoveredDriveAsset Fact(string file, long? captured) => new(file, 1, file + ".jpg", "image/jpeg", 400, 300, captured, null, null, 0);
    private sealed class Fixture : IDisposable
    {
        private readonly WorkerProcessTests.WorkerTestDirectory? _directory;
        public ProcessorStateDatabase Database { get; }
        public LibrarySourceRepository Sources { get; }
        public DiscoveredCatalogRepository Catalog { get; }
        public Guid Source { get; }
        public Fixture(string? directory = null)
        {
            if (directory is null) { _directory = new WorkerProcessTests.WorkerTestDirectory(); directory = _directory.Path; }
            Database = new ProcessorStateDatabase(directory); Sources = new(Database); Catalog = new(Database); Source = AddSource("account-A", "folder-A");
        }
        public Guid AddSource(string account, string folder)
        {
            var accounts = new DriveAccountRepository(Database);
            if (!accounts.Accounts().Any(existing => existing.PermissionId == account))
            { var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, account, "Synthetic account", null); }
            var id = Guid.NewGuid(); Sources.Add(id, account, 1, folder, "Synthetic folder", true, Sources.Snapshot().Revision); return id;
        }
        public void Dispose() { Database.Dispose(); _directory?.Dispose(); }
    }
}
