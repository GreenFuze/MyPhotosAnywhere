using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CatalogSourceConfigurationTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly CatalogWorkspace Workspace = new("workspace-account", "workspace-folder");
    private static JsonDocument Fixture(string name = "catalog-source-configuration") => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
    private static byte[] Bytes(JsonElement value) => new CanonicalJson().Encode(Encoding.UTF8.GetBytes(value.GetRawText()));
    private static VerifiedProtocolDocument Verified(JsonElement graph, string payload = "payload") => new(Guid.Parse(graph.GetProperty("libraryId").GetString()!), Guid.NewGuid(), 1,
        graph.TryGetProperty("sequence", out var sequence) ? long.Parse(sequence.GetString()!, CultureInfo.InvariantCulture) : 1,
        graph.TryGetProperty("kind", out var kind) ? kind.GetString()! : "catalog-commit", 0,
        Array.AsReadOnly(graph.GetProperty("requiredCapabilities").EnumerateArray().Select(value => value.GetString()!).ToArray()), Encoding.UTF8.GetString(Bytes(graph.GetProperty(payload))));
    private static CatalogSourceConfiguration Read(JsonElement graph) => new CatalogPublicationReader(Workspace).ReadSourceConfiguration(Verified(graph), Library);
    private static CatalogSourceCoverageSet Coverage(JsonElement graph)
    {
        var reader = new CatalogPublicationReader(Workspace); var root = reader.ReadCoverageRoot(Bytes(graph.GetProperty("root")), Library);
        return new(root, graph.GetProperty("pages").EnumerateArray().Select((page, index) => reader.ReadCoveragePage(Bytes(page), root, root.Pages[index])));
    }

    [TestMethod]
    public void ApprovedPoliciesAdmitHonestPartialAndDisconnectedCoverageWithoutGrantingNewSources()
    {
        using var fixture = Fixture(); using var coverage = Fixture("catalog-coverage"); var configuration = Read(fixture.RootElement.GetProperty("valid"));
        configuration.ValidateCoverage(Coverage(coverage.RootElement.GetProperty("valid")));
        Assert.AreEqual(7L, configuration.Revision); Assert.AreEqual(3, configuration.Sources.Count); Assert.AreEqual(3, configuration.Accounts.Count);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<CatalogApprovedSource>)configuration.Sources).Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<string>)configuration.Accounts).Clear());
    }

    [TestMethod]
    public void EmptyAndMaximumSourceAndAccountBoundsRemainUsable()
    {
        using var fixture = Fixture(); using var coverage = Fixture("catalog-coverage");
        Read(fixture.RootElement.GetProperty("empty")).ValidateCoverage(Coverage(coverage.RootElement.GetProperty("empty")));
        var maximum = Read(fixture.RootElement.GetProperty("maximum")); Assert.AreEqual(256, maximum.Sources.Count);
        maximum.ValidateCoverage(Coverage(coverage.RootElement.GetProperty("maximum")));
        Assert.AreEqual(128, Read(fixture.RootElement.GetProperty("maximumAccounts")).Accounts.Count);
    }

    [TestMethod]
    public void BothConsumersRejectAllMalformedUnscopedAndUnassociatedConfigurations()
    {
        using var fixture = Fixture(); var rows = fixture.RootElement.GetProperty("invalid"); Assert.AreEqual(30, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray()) Assert.ThrowsExactly<ProtocolFormatException>(() => Read(row.GetProperty("graph")), row.GetProperty("name").GetString());
    }

    [TestMethod]
    public void SelfConsistentCoverageCannotRetargetOrEnablePoliciesFromAnotherConfiguration()
    {
        using var fixture = Fixture(); using var coverageFixture = Fixture("catalog-coverage"); var coverage = Coverage(coverageFixture.RootElement.GetProperty("valid"));
        var rows = fixture.RootElement.GetProperty("coverageConflicts"); Assert.AreEqual(9, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            var configuration = Read(row.GetProperty("graph"));
            Assert.ThrowsExactly<ProtocolFormatException>(() => configuration.ValidateCoverage(coverage), row.GetProperty("name").GetString());
        }
    }

    [TestMethod]
    public void ControllerSignatureMustMatchThePreviouslyEnrolledActor()
    {
        using var fixture = Fixture(); var graph = fixture.RootElement.GetProperty("valid"); var actor = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new ProtocolDocumentSigner(key); var body = JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0,
            libraryId = Library.ToString("D"), actorId = actor.ToString("D"), epoch = "1", sequence = "1", kind = "library-source-configuration",
            requiredCapabilities = graph.GetProperty("requiredCapabilities"), payload = graph.GetProperty("payload") });
        var reader = new ProtocolDocumentReader([CatalogSourceConfiguration.Capability]); var signature = signer.Sign(body);
        var wrong = new TrustedProtocolActor(Library, actor, 1, other.ExportSubjectPublicKeyInfo(), ["library-source-configuration"]);
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(body, signature, wrong));
        var enrolled = new TrustedProtocolActor(Library, actor, 1, key.ExportSubjectPublicKeyInfo(), ["library-source-configuration"]);
        var verified = reader.VerifyAndRead(body, signature, enrolled);
        Assert.AreEqual(3, new CatalogPublicationReader(Workspace).ReadSourceConfiguration(verified, Library).Sources.Count);
    }

    [TestMethod]
    public async Task CoveredTraversalUsesTheApprovedConfigurationBeforeAnyLeafIsEmitted()
    {
        using var fixture = Fixture(); using var traversal = Fixture("catalog-covered-traversal"); var graph = traversal.RootElement.GetProperty("valid");
        var configuration = Read(fixture.RootElement.GetProperty("coveredTraversalConfiguration")); var source = new Source(graph); var admitted = 0; var leaves = 0;
        await foreach (var _ in new CatalogFactsTraversal(Library, Workspace, source).ReadCoveredLeavesAsync(Verified(graph, "commit"), (coverage, token) =>
        { token.ThrowIfCancellationRequested(); configuration.ValidateCoverage(coverage); admitted++; return Task.CompletedTask; }).ConfigureAwait(false))
        { Assert.AreEqual(1, admitted); leaves++; }
        Assert.AreEqual(2, leaves);
    }
    private sealed class Source(JsonElement graph) : ICatalogObjectSource
    {
        private readonly Dictionary<string, byte[]> _objects = graph.GetProperty("objects").EnumerateObject().ToDictionary(item => item.Name, item => Convert.FromBase64String(item.Value.GetString()!), StringComparer.Ordinal);
        public Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<ReadOnlyMemory<byte>>(_objects[reference.FileId]); }
    }
}
