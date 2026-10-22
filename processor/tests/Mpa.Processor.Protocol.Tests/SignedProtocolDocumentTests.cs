using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class SignedProtocolDocumentTests
{
    private static readonly Guid Library = Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839");
    private static readonly Guid Actor = Guid.Parse("6c803c09-3b11-49cb-a3dc-1d42c3dc6f30");
    private static readonly string[] Kinds = ["user-operation"];
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signed-document.json")));
    private static TrustedProtocolActor Authority(JsonElement fixture, Guid? library = null, Guid? actor = null, long epoch = 1, string[]? kinds = null) =>
        new(library ?? Library, actor ?? Actor, epoch, Convert.FromBase64String(fixture.GetProperty("publicKeySpki").GetString()!), kinds ?? Kinds);
    private static DocumentSignature Signature(JsonElement fixture)
    {
        var signature = fixture.GetProperty("signature");
        return new(signature.GetProperty("algorithm").GetString()!, signature.GetProperty("keyId").GetString()!, signature.GetProperty("value").GetString()!);
    }

    [TestMethod]
    public void BothConsumersEncodeIdenticalIndependentSignedFileAndVerifyAnEnrolledActor()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var codec = new SignedProtocolDocuments([]);
        var expected = Encoding.UTF8.GetBytes(root.GetProperty("envelope").GetString()!);
        CollectionAssert.AreEqual(expected, codec.Encode(Encoding.UTF8.GetBytes(root.GetProperty("body").GetString()!), Signature(root)));
        var verified = codec.VerifyAndRead(expected, Authority(root));
        Assert.AreEqual(Library, verified.LibraryId); Assert.AreEqual(Actor, verified.ActorId); Assert.AreEqual(1L, verified.Sequence);
        using var payload = JsonDocument.Parse(verified.CanonicalPayload); Assert.AreEqual("שלום 📷", payload.RootElement.GetProperty("caption").GetString());
    }

    [TestMethod]
    public void BothConsumersRejectEveryMalformedUntrustedAndAlteredEnvelope()
    {
        using var fixture = Fixture(); var codec = new SignedProtocolDocuments([]); var root = fixture.RootElement; var authority = Authority(root);
        var cases = root.GetProperty("invalid"); Assert.AreEqual(15, cases.GetArrayLength());
        foreach (var row in cases.EnumerateArray())
        {
            var bytes = Encoding.UTF8.GetBytes(row.GetProperty("json").GetString()!);
            if (row.GetProperty("compatibility").GetBoolean()) Assert.ThrowsExactly<ProtocolCompatibilityException>(() => codec.VerifyAndRead(bytes, authority), row.GetProperty("name").GetString());
            else Assert.ThrowsExactly<ProtocolFormatException>(() => codec.VerifyAndRead(bytes, authority), row.GetProperty("name").GetString());
        }
    }

    [TestMethod]
    public void ValidSignatureCannotAuthorizeAnotherLibraryActorEpochOrDocumentKind()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var codec = new SignedProtocolDocuments([]);
        var bytes = Encoding.UTF8.GetBytes(root.GetProperty("envelope").GetString()!);
        var denied = new[] { Authority(root, library: Guid.NewGuid()), Authority(root, actor: Guid.NewGuid()), Authority(root, epoch: 2), Authority(root, kinds: ["catalog-commit"]) };
        foreach (var authority in denied) Assert.ThrowsExactly<ProtocolFormatException>(() => codec.VerifyAndRead(bytes, authority));
    }

    [TestMethod]
    public void TotalFileBoundIncludesSignatureAndPackagingDoesNotAuthorizeABody()
    {
        using var fixture = Fixture(); var root = fixture.RootElement; var codec = new SignedProtocolDocuments([]); var authority = Authority(root);
        var signature = Signature(root);
        var body = JsonNode.Parse(root.GetProperty("body").GetString()!)!.AsObject();
        var basic = new CanonicalJson().Encode(Encoding.UTF8.GetBytes(body.ToJsonString()));
        var partLength = (ProtocolDocumentReader.MaximumDocumentBytes - basic.Length - 160) / 8;
        body["padding"] = new JsonArray(Enumerable.Range(0, 8).Select(_ => (JsonNode)JsonValue.Create(new string('x', partLength))!).ToArray());
        var nearLimit = new CanonicalJson().Encode(Encoding.UTF8.GetBytes(body.ToJsonString()));
        Assert.IsTrue(nearLimit.Length < ProtocolDocumentReader.MaximumDocumentBytes);
        Assert.ThrowsExactly<ProtocolFormatException>(() => codec.Encode(nearLimit, signature));
        Assert.ThrowsExactly<ProtocolFormatException>(() => codec.VerifyAndRead(new byte[ProtocolDocumentReader.MaximumDocumentBytes + 1], authority));
        Assert.ThrowsExactly<ProtocolFormatException>(() => codec.Encode(" {\"unapproved\":true}"u8, signature));
        var packed = codec.Encode("{\"unapproved\":true}"u8, signature);
        Assert.ThrowsExactly<ProtocolFormatException>(() => codec.VerifyAndRead(packed, authority));
    }
}
