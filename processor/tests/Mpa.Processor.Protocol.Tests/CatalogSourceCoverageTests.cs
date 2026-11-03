using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CatalogSourceCoverageTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly CatalogWorkspace Workspace = new("workspace-account", "workspace-folder");
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-coverage.json")));
    private static byte[] Bytes(JsonElement value) => new CanonicalJson(CatalogPublicationReader.MaximumIndexBytes).Encode(Encoding.UTF8.GetBytes(value.GetRawText()));
    private static CatalogSourceCoverageSet Read(JsonElement graph, Action<DriveCatalogAsset>? onAsset = null)
    {
        var reader = new CatalogPublicationReader(Workspace); var root = reader.ReadCoverageRoot(Bytes(graph.GetProperty("root")), Library);
        var pages = new List<CatalogCoveragePage>();
        var indices = graph.TryGetProperty("consumePages", out var explicitIndices)
            ? explicitIndices.EnumerateArray().Select(value => value.GetInt32()) : Enumerable.Range(0, graph.GetProperty("pages").GetArrayLength());
        foreach (var index in indices)
            pages.Add(reader.ReadCoveragePage(Bytes(graph.GetProperty("pages")[index]), root, root.Pages[index]));
        var coverage = new CatalogSourceCoverageSet(root, pages);
        foreach (var row in graph.GetProperty("assets").EnumerateArray())
        {
            var memberships = row.GetProperty("sources").EnumerateArray().Select(value => new DriveCatalogSource(Guid.Parse(value.GetProperty("id").GetString()!),
                long.Parse(value.GetProperty("revision").GetString()!, CultureInfo.InvariantCulture), value.GetProperty("depth").GetInt32())).ToArray();
            var asset = new DriveCatalogAsset(Guid.NewGuid(), row.GetProperty("accountId").GetString()!, "synthetic-file", 1, "photo.jpg", "image/jpeg",
                null, null, null, null, null, new ReadOnlyCollection<DriveCatalogSource>(memberships));
            coverage.ValidateAsset(asset); onAsset?.Invoke(asset);
        }
        return coverage;
    }

    [TestMethod]
    public void BoundedCoveragePreservesPartialDisconnectedAndEmptyLibraryFacts()
    {
        using var fixture = Fixture(); var accepted = 0;
        var coverage = Read(fixture.RootElement.GetProperty("valid"), _ => accepted++);
        Assert.AreEqual(3, accepted); Assert.AreEqual(3, coverage.Root.SourceCount);
        Assert.AreEqual(7L, coverage.Root.ConfigurationRevision); Assert.AreEqual(31L, coverage.Root.DiscoveryRevision);
        Assert.AreEqual(0, Read(fixture.RootElement.GetProperty("empty")).Root.SourceCount);
        Assert.AreEqual(8, Read(fixture.RootElement.GetProperty("states")).Root.SourceCount);
        Assert.AreEqual(256, Read(fixture.RootElement.GetProperty("maximum")).Root.SourceCount);
    }

    [TestMethod]
    public void BothConsumersRejectEveryIndependentMalformedCoverageAndMembershipCase()
    {
        using var fixture = Fixture(); var rows = fixture.RootElement.GetProperty("invalid"); Assert.AreEqual(44, rows.GetArrayLength());
        foreach (var item in rows.EnumerateArray())
        {
            var graph = item.GetProperty("graph"); var name = item.GetProperty("name").GetString();
            if (name == "unsupported-major") Assert.ThrowsExactly<ProtocolCompatibilityException>(() => Read(graph), name);
            else Assert.ThrowsExactly<ProtocolFormatException>(() => Read(graph), name);
        }
    }

    [TestMethod]
    public void CoverageRequiresCanonicalBoundedInputAndAnEstablishedLibrary()
    {
        using var fixture = Fixture(); var reader = new CatalogPublicationReader(Workspace);
        var bytes = Bytes(fixture.RootElement.GetProperty("valid").GetProperty("root"));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadCoverageRoot([.. bytes, (byte)' '], Library));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadCoverageRoot(new byte[CatalogPublicationReader.MaximumIndexBytes + 1], Library));
        Assert.ThrowsExactly<ArgumentException>(() => reader.ReadCoverageRoot(bytes, Guid.Empty));
    }

    [TestMethod]
    public void CoveragePageCannotBeReadUsingAnotherSelectedWorkspace()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var reader = new CatalogPublicationReader(Workspace);
        var root = reader.ReadCoverageRoot(Bytes(graph.GetProperty("root")), Library);
        var foreign = new CatalogPublicationReader(new("other-account", "other-workspace"));
        Assert.ThrowsExactly<ProtocolFormatException>(() => foreign.ReadCoveragePage(Bytes(graph.GetProperty("pages")[0]), root, root.Pages[0]));
    }
}
