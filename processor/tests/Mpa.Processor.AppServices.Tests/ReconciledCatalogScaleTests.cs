using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [TestCategory("LargeCatalogPublication")]
    [DataRow(10000)]
    [DataRow(100000)]
    public async Task ActualLargeGenerationMeasuresIncrementalPreparationAndInterleavedGalleryReads(int count)
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false);
        var feed = new RecordedDependencyFeed(fixture, generationWorkspace: true);
        var seed = Stopwatch.StartNew();
        for (var offset = 0; offset < count; offset += 250)
        {
            var start = offset; var size = Math.Min(250, count - offset);
            await fixture.Setup.Accounts.State.ExecuteAsync(database =>
            {
                var source = new LibrarySourceRepository(database).Snapshot().Sources.Single();
                var rows = Enumerable.Range(start, size).Select(index => new DiscoveredDriveAsset(
                    "scale-original-" + index.ToString(CultureInfo.InvariantCulture), 1, "Photo.jpg", "image/jpeg", 40, 30,
                    index / 7, null, null, 0)).ToArray();
                return new DiscoveredCatalogRepository(database).ApplyBatch(source.Id, source.Revision, 1, rows);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        var seedMs = seed.Elapsed.TotalMilliseconds; var importMs = -1.0;
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            if (mime == "application/gzip" && importMs < 0)
            {
                var importing = Stopwatch.StartNew(); await AddPreparationPhotoAsync(fixture, "scale-later-original").ConfigureAwait(false);
                importMs = importing.Elapsed.TotalMilliseconds;
            }
            return result;
        };
        await using var current = CatalogReconciler(fixture);
        await using var preparation = await PreparationOwnerAsync(fixture, current).ConfigureAwait(false);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var galleryStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var gallery = new List<double>(); var seek = new List<double>();
        var reading = SampleGalleryAsync(); var beforeReads = feed.MetadataReads.Count; var beforeInventories = feed.FullInventories;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true); var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew(); CapturedCatalogPublication prepared;
        try { prepared = await preparation.PrepareAsync(lifetime.Token).ConfigureAwait(false); }
        finally { await galleryStop.CancelAsync().ConfigureAwait(false); await reading.ConfigureAwait(false); }
        var elapsedMs = timer.Elapsed.TotalMilliseconds;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: true); var allocations = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
        Assert.AreEqual("dependencies-ready", prepared.Capture.Phase); Assert.IsNotNull(prepared.Plan);
        Assert.AreEqual((long)count, prepared.Basis.Snapshot.Assets); Assert.IsTrue(importMs >= 0); Assert.IsTrue(gallery.Count > 0); Assert.IsTrue(seek.Count > 0);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var later = new Mpa.Processor.Protocol.DriveAssetIdentity(prepared.Capture.Scope.LibraryId).Derive("permission-A", "scale-later-original");
            Assert.IsNotNull(new DiscoveredCatalogRepository(database).ResolveVisible(later));
            Assert.IsNull(new CatalogGenerationCaptureRepository(database, fixture.Budget).PublishedHead());
            Assert.AreEqual(prepared.Capture, new CatalogGenerationCaptureRepository(database, fixture.Budget).Active().Single());
            return true;
        }, lifetime.Token).ConfigureAwait(false);
        using (var actual = await fixture.Setup.Accounts.State.ExecuteAsync(CatalogFactsSnapshot.Open, lifetime.Token).ConfigureAwait(false))
        {
            long visible = 0; Guid? after = null;
            while (true)
            {
                var page = actual.ReadPage("", after, cancellationToken: lifetime.Token); visible += page.Assets.Count;
                if (!page.HasMore) break;
                Assert.IsTrue(page.Assets.Count > 0); after = page.Assets[^1].Id;
            }
            Assert.AreEqual((long)count + 1, visible);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            incrementalGenerationFixture = count, seedMs, preparationMs = elapsedMs, interleavedImportMs = importMs,
            metadataInventoryPages = feed.FullInventories - beforeInventories, metadataReads = feed.MetadataReads.Count - beforeReads,
            metadataReadBytes = feed.MetadataReads.Skip(beforeReads).Sum(file => (long)fixture.Cloud[file].Bytes.Length),
            generatedObjects = fixture.Cloud.Count, generatedMetadataBytes = fixture.Cloud.Values.Sum(item => (long)item.Bytes.Length),
            retainedSnapshotBytes = prepared.Basis.Snapshot.Bytes, managedBeforeBytes = managedBefore, managedAfterBytes = managedAfter,
            cumulativeManagedAllocationBytes = allocations, gallerySamples = gallery.Count, galleryP95Ms = Percentile(gallery), galleryMaxMs = gallery.Max(),
            seekSamples = seek.Count, seekP95Ms = Percentile(seek), seekMaxMs = seek.Max(),
            scope = "real native capture/private dependency owner/full prepared graph; synthetic Drive transport; shared-process memory; excludes final signed commit, live network, native UI and full processor feature workloads"
        }));

        async Task SampleGalleryAsync()
        {
            try
            {
                while (true)
                {
                    var started = Stopwatch.GetTimestamp();
                    var page = await fixture.Setup.Accounts.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ReadPage(), galleryStop.Token).ConfigureAwait(false);
                    gallery.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); Assert.AreEqual(60, page.Assets.Count);
                    started = Stopwatch.GetTimestamp();
                    var sought = await fixture.Setup.Accounts.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).SeekDate(count / 14), galleryStop.Token).ConfigureAwait(false);
                    seek.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds); Assert.AreEqual(60, sought.Assets.Count);
                    await Task.Delay(25, galleryStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (galleryStop.IsCancellationRequested) { }
        }
        static double Percentile(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);
    }
}
