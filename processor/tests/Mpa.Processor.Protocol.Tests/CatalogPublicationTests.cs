using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed partial class CatalogPublicationTests
{
    private static readonly CatalogWorkspace Workspace = new("test-account", "test-workspace");
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-publication.json")));
    private static byte[] Canonical(JsonElement value) => new CanonicalJson().Encode(Encoding.UTF8.GetBytes(value.GetRawText()));
    private static VerifiedProtocolDocument Verified(JsonElement payload, long sequence = 3) => new(Guid.NewGuid(), Guid.NewGuid(), 1, sequence,
        "catalog-commit", 0, Array.AsReadOnly(new[] { CatalogPublicationReader.Capability }), Encoding.UTF8.GetString(Canonical(payload)));

    [TestMethod]
    public void SharedCommitAndIndexFixturesRetainWorkspaceBoundsAndPartitionRoles()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var reader = new CatalogPublicationReader(Workspace);
        var commit = reader.ReadCommit(Verified(root.GetProperty("commit")));
        Assert.AreEqual(Guid.Parse("22222222-2222-4222-8222-222222222222"), commit.GenerationId);
        Assert.AreEqual(0L, commit.SettingsRevision); Assert.AreEqual(1L, commit.MinimumHistoryGeneration);
        Assert.AreEqual("previous-commit", commit.Parent!.FileId); Assert.AreEqual("root-index", commit.RootIndex.FileId);
        var index = reader.ReadIndex(Canonical(root.GetProperty("index")), "");
        Assert.AreEqual(2, index.Children.Count); Assert.AreEqual("0a", index.Children[0].Edge); Assert.IsNull(index.Children[0].Chunk);
        Assert.AreEqual(CatalogChunkDecoder.MaximumCompressedBytes, index.Children[1].Reference.Bytes);
        Assert.AreEqual(CatalogChunkDecoder.MaximumExpandedBytes, index.Children[1].Chunk!.ExpandedBytes);
        // A first generation can be empty, and a fourth-level partition must terminate in chunks.
        Assert.AreEqual(0, reader.ReadIndex("{\"children\":[],\"format\":\"mpa-catalog-index\",\"major\":1,\"prefix\":\"\"}"u8, "").Children.Count);
        var deepest = Encoding.UTF8.GetString(Canonical(root.GetProperty("index"))).Replace("\"prefix\":\"\"", "\"prefix\":\"000000\"", StringComparison.Ordinal)
            .Replace("\"kind\":\"index\"", "\"kind\":\"chunk\"", StringComparison.Ordinal).Replace("\"expanded\":null", "\"expanded\":{\"bytes\":\"1\",\"sha256\":\"" + new string('a', 64) + "\"}", StringComparison.Ordinal);
        Assert.AreEqual(2, reader.ReadIndex(Encoding.UTF8.GetBytes(deepest), "000000").Children.Count);
    }

    [TestMethod]
    public void BothConsumersRejectEverySharedMalformedCommitAndIndex()
    {
        using var fixture = Fixture(); var reader = new CatalogPublicationReader(Workspace);
        var commits = fixture.RootElement.GetProperty("invalidCommits"); var indexes = fixture.RootElement.GetProperty("invalidIndexes");
        Assert.AreEqual(18, commits.GetArrayLength()); Assert.AreEqual(21, indexes.GetArrayLength());
        foreach (var item in commits.EnumerateArray())
            Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadCommit(Verified(item.GetProperty("payload"), long.Parse(item.GetProperty("sequence").GetString()!, System.Globalization.CultureInfo.InvariantCulture))), item.GetProperty("name").GetString());
        foreach (var item in indexes.EnumerateArray())
            Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadIndex(Canonical(item.GetProperty("node")), item.GetProperty("prefix").GetString()!), item.GetProperty("name").GetString());
    }

    [TestMethod]
    public void SignatureAndEnrolledAuthorityPrecedeTypedCommitAndCanonicalIndexBytes()
    {
        using var fixture = Fixture(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new ProtocolDocumentSigner(key); var library = Guid.NewGuid(); var actor = Guid.NewGuid();
        var authority = new TrustedProtocolActor(library, actor, 1, key.ExportSubjectPublicKeyInfo(), ["catalog-commit"]);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa", major = 1, minor = 0, libraryId = library.ToString("D"), actorId = actor.ToString("D"),
            epoch = "1", sequence = "3", kind = "catalog-commit", requiredCapabilities = new[] { CatalogPublicationReader.Capability }, payload = fixture.RootElement.GetProperty("commit") });
        var verified = new ProtocolDocumentReader([CatalogPublicationReader.Capability]).VerifyAndRead(body, signer.Sign(body), authority);
        var reader = new CatalogPublicationReader(Workspace); Assert.AreEqual("root-index", reader.ReadCommit(verified).RootIndex.FileId);
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadCommit(verified with { Kind = "human-operation" }));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadCommit(verified with { RequiredCapabilities = Array.AsReadOnly(Array.Empty<string>()) }));
        var index = Canonical(fixture.RootElement.GetProperty("index"));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadIndex(Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(index)), ""));
        Assert.ThrowsExactly<ProtocolCompatibilityException>(() => reader.ReadIndex(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(index).Replace("\"major\":1", "\"major\":2", StringComparison.Ordinal)), ""));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.ReadIndex(new byte[CatalogPublicationReader.MaximumIndexBytes + 1], ""));
        var genesis = verified with { Sequence = 1, CanonicalPayload = verified.CanonicalPayload.Replace("\"parent\":" + fixture.RootElement.GetProperty("commit").GetProperty("parent").GetRawText(), "\"parent\":null", StringComparison.Ordinal) };
        Assert.IsNull(reader.ReadCommit(genesis).Parent);
    }
}
