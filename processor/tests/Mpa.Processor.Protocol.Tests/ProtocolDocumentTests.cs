using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class ProtocolDocumentTests
{
    private const string Body = """
        {"format":"mpa","major":1,"minor":5,"libraryId":"94a81164-e980-4ec1-a566-cec36475b839",
        "actorId":"11111111-1111-4111-8111-111111111111","epoch":"1","sequence":"9223372036854775807",
        "kind":"human-operation","requiredCapabilities":["human.fields.v1"],"payload":{"value":"שלום"},"futureOptional":true}
        """;

    [TestMethod]
    public void ScopeCapabilitiesAndSignatureMustAllMatchBeforePayloadBecomesUsable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new ProtocolDocumentSigner(key);
        var actor = new TrustedProtocolActor(Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839"),
            Guid.Parse("11111111-1111-4111-8111-111111111111"), 1, key.ExportSubjectPublicKeyInfo(), ["human-operation"]);
        var reader = new ProtocolDocumentReader(["human.fields.v1"]);
        var body = Encoding.UTF8.GetBytes(Body);
        var accepted = reader.VerifyAndRead(body, signer.Sign(body), actor);
        Assert.AreEqual(long.MaxValue, accepted.Sequence);
        Assert.AreEqual(5, accepted.Minor); // Unknown minor/optional data is authenticated, not silently stripped.
        Assert.AreEqual("{\"value\":\"שלום\"}", accepted.CanonicalPayload);
        Assert.ThrowsExactly<ProtocolCompatibilityException>(() => new ProtocolDocumentReader([]).VerifyAndRead(body, signer.Sign(body), actor));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(Encoding.UTF8.GetBytes(Body.Replace("true", "false", StringComparison.Ordinal)), signer.Sign(body), actor));
        foreach (var changed in new[] { Body.Replace("\"epoch\":\"1\"", "\"epoch\":\"2\"", StringComparison.Ordinal),
            Body.Replace("human-operation", "catalog-commit", StringComparison.Ordinal), Body.Replace("111111111111", "222222222222", StringComparison.Ordinal) })
        {
            var bytes = Encoding.UTF8.GetBytes(changed);
            Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(bytes, signer.Sign(bytes), actor));
        }
    }

    [TestMethod]
    public void SignedMalformedCountersAndNewMajorNeverLookLikeEmptySuccess()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new ProtocolDocumentSigner(key);
        var actor = new TrustedProtocolActor(Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839"), Guid.Parse("11111111-1111-4111-8111-111111111111"), 1,
            key.ExportSubjectPublicKeyInfo(), ["human-operation"]);
        var reader = new ProtocolDocumentReader(["human.fields.v1"]);
        foreach (var counter in new[] { "0", "01", "+1", "-1", "9223372036854775808", "١" })
        {
            var body = Encoding.UTF8.GetBytes(Body.Replace("9223372036854775807", counter, StringComparison.Ordinal));
            Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(body, signer.Sign(body), actor));
        }
        var incompatible = Encoding.UTF8.GetBytes(Body.Replace("\"major\":1", "\"major\":2", StringComparison.Ordinal));
        Assert.ThrowsExactly<ProtocolCompatibilityException>(() => reader.VerifyAndRead(incompatible, signer.Sign(incompatible), actor));
    }
}
