using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CatalogFactsTraversalTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly CatalogWorkspace Workspace = new("workspace-account", "workspace-folder");
    private static readonly string[] CompleteReads = ["index-root", "index-56", "leaf-5603", "leaf-5687"];
    private static readonly string[] EmptyReads = ["index-root"];
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-traversal.json")));
    private static VerifiedProtocolDocument Verified(JsonElement graph) => new(Guid.Parse(graph.GetProperty("libraryId").GetString()!), Guid.NewGuid(), 1,
        long.Parse(graph.GetProperty("sequence").GetString()!, CultureInfo.InvariantCulture), "catalog-commit", 0,
        Array.AsReadOnly(graph.GetProperty("requiredCapabilities").EnumerateArray().Select(value => value.GetString()!).ToArray()),
        Encoding.UTF8.GetString(new CanonicalJson().Encode(Encoding.UTF8.GetBytes(graph.GetProperty("commit").GetRawText()))));
    private static async Task<List<CatalogFactsLeaf>> CollectAsync(CatalogFactsTraversal reader, VerifiedProtocolDocument verified, CancellationToken token = default)
    {
        var result = new List<CatalogFactsLeaf>();
        await foreach (var leaf in reader.ReadLeavesAsync(verified, token).ConfigureAwait(false)) result.Add(leaf);
        return result;
    }
    [TestMethod]
    public async Task BothConsumersWalkTheIndependentTreeInIdentityOrderAndValidateEmptyRoot()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var source = new Source(graph);
        var leaves = await CollectAsync(new(Library, Workspace, source), Verified(graph)).ConfigureAwait(false);
        CollectionAssert.AreEqual(fixture.RootElement.GetProperty("expectedIds").EnumerateArray().Select(value => Guid.Parse(value.GetString()!)).ToArray(), leaves.SelectMany(leaf => leaf.Facts.Assets).Select(asset => asset.Id).ToArray());
        CollectionAssert.AreEqual(CompleteReads, source.Reads.ToArray());
        graph = fixture.RootElement.GetProperty("empty"); source = new(graph);
        Assert.AreEqual(0, (await CollectAsync(new(Library, Workspace, source, 2), Verified(graph)).ConfigureAwait(false)).Count);
        CollectionAssert.AreEqual(EmptyReads, source.Reads.ToArray());
    }
    [TestMethod]
    public async Task BothConsumersRejectEveryMalformedLateFailedAliasedOrOverBudgetTree()
    {
        using var fixture = Fixture(); var cases = fixture.RootElement.GetProperty("invalid"); Assert.AreEqual(21, cases.GetArrayLength());
        foreach (var item in cases.EnumerateArray())
        {
            var graph = item.GetProperty("graph"); var source = new Source(graph);
            var reader = new CatalogFactsTraversal(Library, Workspace, source, graph.GetProperty("objectBudget").GetInt32()); var emitted = 0;
            async Task Read() { await foreach (var _ in reader.ReadLeavesAsync(Verified(graph)).ConfigureAwait(false)) emitted++; }
            var name = item.GetProperty("name").GetString();
            switch (item.GetProperty("error").GetString())
            {
                case "format": await Assert.ThrowsExactlyAsync<ProtocolFormatException>(Read, name).ConfigureAwait(false); break;
                case "compatibility": await Assert.ThrowsExactlyAsync<ProtocolCompatibilityException>(Read, name).ConfigureAwait(false); break;
                case "limit": await Assert.ThrowsExactlyAsync<CatalogTraversalLimitException>(Read, name).ConfigureAwait(false); break;
                case "io": await Assert.ThrowsExactlyAsync<IOException>(Read, name).ConfigureAwait(false); break;
                default: throw new InvalidDataException("Unknown shared traversal failure category.");
            }
            Assert.AreEqual(item.GetProperty("emittedBeforeFailure").GetInt32(), emitted, name);
        }
    }
    [TestMethod]
    public async Task StoppingCollectionDoesNotPrefetchAndLateCancellationCannotEmitALeaf()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var source = new Source(graph);
        await using (var enumeration = new CatalogFactsTraversal(Library, Workspace, source).ReadLeavesAsync(Verified(graph)).GetAsyncEnumerator())
        { Assert.IsTrue(await enumeration.MoveNextAsync().ConfigureAwait(false)); Assert.AreEqual("5603", enumeration.Current.Prefix); }
        Assert.AreEqual(3, source.Reads.Count);
        using var canceled = new CancellationTokenSource(); source = new(graph);
        source.AfterRead = reference => { if (reference.FileId == "leaf-5603") canceled.Cancel(); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => CollectAsync(new(Library, Workspace, source), Verified(graph), canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(3, source.Reads.Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CatalogFactsTraversal(Library, Workspace, source, 0));
    }
    [TestMethod]
    public async Task ApprovedSignatureAndCapabilitiesPrecedeEveryTreeRead()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var source = new Source(graph);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var signer = new ProtocolDocumentSigner(key); var actor = Guid.NewGuid();
        var authority = new TrustedProtocolActor(Library, actor, 1, key.ExportSubjectPublicKeyInfo(), ["catalog-commit"]);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0, libraryId = Library.ToString("D"), actorId = actor.ToString("D"),
            epoch = "1", sequence = "1", kind = "catalog-commit", requiredCapabilities = new[] { DriveCatalogFacts.Capability, CatalogPublicationReader.Capability }, payload = graph.GetProperty("commit") });
        var signature = signer.Sign(body); var protocol = new ProtocolDocumentReader([DriveCatalogFacts.Capability, CatalogPublicationReader.Capability]);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"settingsRevision\":\"1\"", "\"settingsRevision\":\"2\"", StringComparison.Ordinal));
        Assert.IsFalse(body.AsSpan().SequenceEqual(changed));
        Assert.ThrowsExactly<ProtocolFormatException>(() => protocol.VerifyAndRead(changed, signature, authority)); Assert.AreEqual(0, source.Reads.Count);
        var verified = protocol.VerifyAndRead(body, signature, authority);
        Assert.AreEqual(2, (await CollectAsync(new(Library, Workspace, source), verified).ConfigureAwait(false)).Count);
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
