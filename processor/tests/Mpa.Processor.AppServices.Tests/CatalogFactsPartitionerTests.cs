using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class CatalogFactsPartitionerTests
{
    [TestMethod]
    [DataRow(2, CatalogChunkDecoder.MaximumCompressedBytes)]
    [DataRow(1000, 1024)]
    public async Task DensePartitionsSplitForRecordOrCompressionBoundsAndRoundTripWithoutLoss(int assetLimit, int compressedLimit)
    {
        using var fixture = new Fixture(); var identity = new DriveAssetIdentity(fixture.Library);
        var files = Enumerable.Range(0, 50000).Select(index => "dense-" + index.ToString(CultureInfo.InvariantCulture))
            .Where(file => identity.Derive("account", file).ToString("N").StartsWith("ab", StringComparison.Ordinal)).Take(12).ToArray();
        Assert.AreEqual(12, files.Length);
        fixture.Add(files.Select(file => Fact(file) with { Name = DenseName(file) }).ToArray());
        using var frozen = fixture.Freeze(); var partitioner = new CatalogFactsPartitioner(frozen, assetLimit, compressedLimit: compressedLimit);
        var chunks = partitioner.ReadChunks().ToArray(); Assert.IsTrue(chunks.Length > 1); Assert.IsTrue(chunks.All(chunk => chunk.Prefix.Length >= 4));
        var ids = new List<Guid>(); var decoder = new CatalogChunkDecoder();
        for (var index = 0; index < chunks.Length; index++)
        {
            var chunk = chunks[index]; Assert.IsTrue(chunk.Assets <= assetLimit); Assert.IsTrue(chunk.Compressed.Length <= compressedLimit);
            var reference = new CatalogChunkReference(new("account", "chunk-" + index.ToString(CultureInfo.InvariantCulture), "metadata", chunk.Compressed.Length, chunk.CompressedSha256), chunk.ExpandedBytes, chunk.ExpandedSha256);
            var decoded = await decoder.DecodeAsync(chunk.Compressed, reference, CancellationToken.None).ConfigureAwait(false);
            var facts = new DriveCatalogFacts(frozen.LibraryId).Read(decoded, chunk.Prefix); Assert.AreEqual(chunk.Assets, facts.Assets.Count);
            ids.AddRange(facts.Assets.Select(asset => asset.Id));
        }
        CollectionAssert.AreEquivalent(files.Select(file => identity.Derive("account", file)).ToArray(), ids); Assert.AreEqual(12, ids.Distinct().Count());
        CollectionAssert.AreEqual(ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray(), ids);
        var replay = partitioner.ReadChunks().ToArray(); CollectionAssert.AreEqual(chunks.Select(chunk => chunk.CompressedSha256).ToArray(), replay.Select(chunk => chunk.CompressedSha256).ToArray());
        Assert.AreEqual(12L, frozen.Count);
    }

    [TestMethod]
    public void EmptyLibraryUsesNoFactsChunkAndHasAValidEmptyRootIndex()
    {
        using var fixture = new Fixture(); using var frozen = fixture.Freeze(); Assert.AreEqual(0, new CatalogFactsPartitioner(frozen).ReadChunks().Count());
        var bytes = new CatalogIndexDocumentWriter(Fixture.Workspace).Encode("", []);
        Assert.AreEqual(0, new CatalogPublicationReader(Fixture.Workspace).ReadIndex(bytes, "").Children.Count);
    }

    [TestMethod]
    public void UnsupportedCapacityCancellationAndBudgetNeverTruncateAPartition()
    {
        using var fixture = new Fixture(); fixture.Add([Fact("retained")]); using var frozen = fixture.Freeze();
        Assert.ThrowsExactly<CatalogPartitionCapacityException>(() => new CatalogFactsPartitioner(frozen, expandedLimit: 256).ReadChunks().ToArray());
        Assert.ThrowsExactly<CatalogPartitionCapacityException>(() => new CatalogFactsPartitioner(frozen, compressedLimit: 64).ReadChunks().ToArray());
        Assert.ThrowsExactly<CatalogTraversalLimitException>(() => new CatalogFactsPartitioner(frozen, objectBudget: 3).ReadChunks().ToArray());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => new CatalogFactsPartitioner(frozen).ReadChunks(canceled.Token).ToArray());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CatalogFactsPartitioner(frozen, maximumAssets: 1001));
        Assert.AreEqual(1L, frozen.Count); Assert.AreEqual("retained", frozen.ReadPage("").Assets.Single().FileId);
    }

    [TestMethod]
    public void IndexWriterSortsCapturedReferencesAndRejectsAliasingScopeAndExpansionMismatch()
    {
        var writer = new CatalogIndexDocumentWriter(Fixture.Workspace); var first = Reference("first"); var second = Reference("second");
        var bytes = writer.Encode("", [new("ff", second, null), new("00", first, null)]);
        var node = new CatalogPublicationReader(Fixture.Workspace).ReadIndex(bytes, ""); Assert.AreEqual("00", node.Children[0].Edge);
        Assert.ThrowsExactly<ProtocolFormatException>(() => writer.Encode("", [new("00", first, null), new("ff", first, null)]));
        Assert.ThrowsExactly<ProtocolFormatException>(() => writer.Encode("", [new("00", first with { WorkspaceId = "another-folder" }, null)]));
        Assert.ThrowsExactly<ProtocolFormatException>(() => writer.Encode("", [new("00", first, new(second, 1, second.Sha256))]));
        Assert.ThrowsExactly<ProtocolFormatException>(() => writer.Encode("00", []));
        Assert.ThrowsExactly<CatalogPartitionCapacityException>(() => writer.Encode("", Enumerable.Repeat(new CatalogIndexChild("00", first, null), 257)));
    }

    private static CatalogObjectReference Reference(string file) => new("account", file, "metadata", 1, new string('a', 64));
    private static string DenseName(string file) => string.Concat(Enumerable.Range(0, 12).Select(part => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file + "-" + part.ToString(CultureInfo.InvariantCulture))))));
    private static DiscoveredDriveAsset Fact(string file) => new(file, 1, "Photo.jpg", "image/jpeg", 40, 30, 100, null, null, 0);
    private sealed class Space : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
    private sealed class Fixture : IDisposable
    {
        public static readonly CatalogWorkspace Workspace = new("account", "metadata");
        private readonly string _path = Path.Combine(Path.GetTempPath(), "mpa-partition-test-" + Guid.NewGuid().ToString("N"));
        private readonly ProcessorStateDatabase _database;
        private readonly CatalogPreparationDirectory _directory;
        private readonly StorageBudgetManager _storage = new(new Space());
        private readonly Guid _source = Guid.NewGuid();
        public Guid Library { get; }
        public Fixture()
        {
            _database = new(_path); Library = new ProcessorLibraryRepository(_database).GetOrCreateIdentity();
            var accounts = new DriveAccountRepository(_database); var pending = accounts.Begin(Guid.NewGuid()); accounts.Promote(pending.Id, "account", "Synthetic", null);
            new LibrarySourceRepository(_database).Add(_source, "account", 1, "photos", "Photos", true, 1); _directory = new(_path, _database.InstallationId);
        }
        public void Add(DiscoveredDriveAsset[] facts) => new DiscoveredCatalogRepository(_database).ApplyBatch(_source, 1, 1, facts);
        public FrozenCatalogSnapshot Freeze() => FrozenCatalogSnapshot.CaptureAndRelease(CatalogFactsSnapshot.Open(_database), _directory, _storage);
        public void Dispose() { _directory.Dispose(); _database.Dispose(); Directory.Delete(_path, recursive: true); }
    }
}
