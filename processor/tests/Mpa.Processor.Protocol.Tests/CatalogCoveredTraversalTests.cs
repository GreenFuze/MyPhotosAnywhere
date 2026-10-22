using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CatalogCoveredTraversalTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly CatalogWorkspace Workspace = new("workspace-account", "workspace-folder");
    private static readonly string[] CompleteReads = ["coverage-only", "coverage-page-0", "coverage-page-1", "index-root", "index-56", "leaf-5603", "leaf-5687"];
    private static readonly string[] EmptyReads = ["coverage-only", "index-root"];
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-covered-traversal.json")));
    private static VerifiedProtocolDocument Verified(JsonElement graph) => new(Guid.Parse(graph.GetProperty("libraryId").GetString()!), Guid.NewGuid(), 1,
        long.Parse(graph.GetProperty("sequence").GetString()!, CultureInfo.InvariantCulture), "catalog-commit", 0,
        Array.AsReadOnly(graph.GetProperty("requiredCapabilities").EnumerateArray().Select(value => value.GetString()!).ToArray()),
        Encoding.UTF8.GetString(new CanonicalJson().Encode(Encoding.UTF8.GetBytes(graph.GetProperty("commit").GetRawText()))));
    private static CatalogFactsTraversal Reader(JsonElement graph, Source source) => new(Library, Workspace, source, graph.GetProperty("objectBudget").GetInt32());

    [TestMethod]
    public async Task CompleteCoveragePrecedesConfigurationAdmissionAndEveryPhotoRead()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var source = new Source(graph);
        var approved = false; var ids = new List<Guid>();
        await foreach (var leaf in Reader(graph, source).ReadCoveredLeavesAsync(Verified(graph), (coverage, token) =>
        {
            token.ThrowIfCancellationRequested(); Assert.AreEqual(3, source.Reads.Count);
            Assert.AreEqual(3, coverage.Root.SourceCount); Assert.AreEqual(7L, coverage.Root.ConfigurationRevision);
            Assert.AreEqual(31L, coverage.Root.DiscoveryRevision); Assert.AreEqual(3, coverage.Sources.Count);
            Assert.AreEqual("account-A", coverage.Sources[0].AccountId); Assert.AreEqual("partial", coverage.Sources[0].State);
            Assert.AreEqual("disconnected", coverage.Sources[1].State); Assert.AreEqual("offline", coverage.Sources[2].State);
            Assert.ThrowsExactly<NotSupportedException>(() => ((IList<CatalogSourceCoverage>)coverage.Sources).Clear());
            approved = true; return Task.CompletedTask;
        }).ConfigureAwait(false))
        { Assert.IsTrue(approved); ids.AddRange(leaf.Facts.Assets.Select(asset => asset.Id)); }
        CollectionAssert.AreEqual(fixture.RootElement.GetProperty("expectedIds").EnumerateArray().Select(value => Guid.Parse(value.GetString()!)).ToArray(), ids.ToArray());
        CollectionAssert.AreEqual(CompleteReads, source.Reads.ToArray());
    }

    [TestMethod]
    public async Task EmptyCatalogStillValidatesCoverageAndConfiguration()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("empty"); var source = new Source(graph); var approved = 0; var leaves = 0;
        await foreach (var _ in Reader(graph, source).ReadCoveredLeavesAsync(Verified(graph), (coverage, _) =>
        { Assert.AreEqual(0, coverage.Root.SourceCount); Assert.AreEqual(1, source.Reads.Count); approved++; return Task.CompletedTask; }).ConfigureAwait(false)) leaves++;
        Assert.AreEqual(1, approved); Assert.AreEqual(0, leaves);
        CollectionAssert.AreEqual(EmptyReads, source.Reads.ToArray());
    }

    [TestMethod]
    public async Task BothConsumersRejectEveryFailedCoverageAliasedRoleOrConflictingMembership()
    {
        using var fixture = Fixture(); var cases = fixture.RootElement.GetProperty("invalid"); Assert.AreEqual(22, cases.GetArrayLength());
        foreach (var item in cases.EnumerateArray())
        {
            var graph = item.GetProperty("graph"); var source = new Source(graph); var emitted = 0;
            async Task Read()
            {
                await foreach (var _ in Reader(graph, source).ReadCoveredLeavesAsync(Verified(graph), (_, _) => Task.CompletedTask).ConfigureAwait(false)) emitted++;
            }
            var name = item.GetProperty("name").GetString();
            switch (item.GetProperty("error").GetString())
            {
                case "format": await Assert.ThrowsExactlyAsync<ProtocolFormatException>(Read, name).ConfigureAwait(false); break;
                case "compatibility": await Assert.ThrowsExactlyAsync<ProtocolCompatibilityException>(Read, name).ConfigureAwait(false); break;
                case "limit": await Assert.ThrowsExactlyAsync<CatalogTraversalLimitException>(Read, name).ConfigureAwait(false); break;
                case "io": await Assert.ThrowsExactlyAsync<IOException>(Read, name).ConfigureAwait(false); break;
                default: throw new InvalidDataException("Unknown fixture failure category.");
            }
            Assert.AreEqual(item.GetProperty("emittedBeforeFailure").GetInt32(), emitted, name);
        }
    }

    [TestMethod]
    public async Task RejectedConfigurationAndCancellationNeverReadFactsOrBecomeSuccessfulEmptyResults()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var source = new Source(graph);
        async Task ReadRejected()
        {
            await foreach (var _ in Reader(graph, source).ReadCoveredLeavesAsync(Verified(graph), (_, _) => throw new InvalidDataException("Unapproved source configuration.")).ConfigureAwait(false))
                Assert.Fail("Rejected configuration cannot emit facts.");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(ReadRejected).ConfigureAwait(false); Assert.AreEqual(3, source.Reads.Count);
        using var canceled = new CancellationTokenSource(); source = new(graph); source.AfterRead = _ => canceled.Cancel();
        async Task ReadCanceled()
        {
            await foreach (var _ in Reader(graph, source).ReadCoveredLeavesAsync(Verified(graph), (_, _) => Task.CompletedTask, canceled.Token).ConfigureAwait(false))
                Assert.Fail("Canceled coverage cannot emit facts.");
        }
        await Assert.ThrowsAsync<OperationCanceledException>(ReadCanceled).ConfigureAwait(false); Assert.AreEqual(1, source.Reads.Count);
        Assert.ThrowsExactly<ArgumentNullException>(() => Reader(graph, source).ReadCoveredLeavesAsync(Verified(graph), null!));
    }

    [TestMethod]
    public async Task AbandonedStagingReadDoesNotReadTheNextPhotoChunk()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var source = new Source(graph);
        await using (var reader = Reader(graph, source).ReadCoveredLeavesAsync(Verified(graph), (_, _) => Task.CompletedTask).GetAsyncEnumerator())
        { Assert.IsTrue(await reader.MoveNextAsync().ConfigureAwait(false)); Assert.AreEqual("5603", reader.Current.Prefix); }
        Assert.AreEqual(6, source.Reads.Count); Assert.IsFalse(source.Reads.Contains("leaf-5687", StringComparer.Ordinal));
    }
    private sealed class Source(JsonElement graph) : ICatalogObjectSource
    {
        private readonly Dictionary<string, byte[]> _objects = graph.GetProperty("objects").EnumerateObject().ToDictionary(item => item.Name, item => Convert.FromBase64String(item.Value.GetString()!), StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public Action<CatalogObjectReference>? AfterRead { get; set; }
        public Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads.Add(reference.FileId); AfterRead?.Invoke(reference);
            return Task.FromResult<ReadOnlyMemory<byte>>(_objects.TryGetValue(reference.FileId, out var bytes) ? bytes : throw new IOException("Synthetic catalog object is unavailable."));
        }
    }
}
