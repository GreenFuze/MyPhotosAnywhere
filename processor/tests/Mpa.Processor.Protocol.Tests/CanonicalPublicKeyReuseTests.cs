using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CanonicalPublicKeyReuseTests
{
    private const string Body = """
        {"format":"mpa","major":1,"minor":0,"libraryId":"94a81164-e980-4ec1-a566-cec36475b839",
        "actorId":"11111111-1111-4111-8111-111111111111","epoch":"1","sequence":"1",
        "kind":"human-operation","requiredCapabilities":["human.fields.v1"],"payload":{"name":"Original"}}
        """;

    [TestMethod]
    public void CopiedActorKeySurvivesCallerMutationAndNeverCachesAnAcceptedSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = key.ExportSubjectPublicKeyInfo(); var actor = Actor(bytes); Array.Fill(bytes, (byte)0);
        var body = Encoding.UTF8.GetBytes(Body); var signature = new ProtocolDocumentSigner(key).Sign(body);
        var reader = new ProtocolDocumentReader(["human.fields.v1"]); Assert.AreEqual(1L, reader.VerifyAndRead(body, signature, actor).Sequence);
        var changed = Encoding.UTF8.GetBytes(Body.Replace("Original", "Changed", StringComparison.Ordinal));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(changed, signature, actor));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(body, new ProtocolDocumentSigner(other).Sign(body), actor));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(body, signature with { KeyId = new string('0', 64) }, actor));
        var nextEpoch = Encoding.UTF8.GetBytes(Body.Replace("\"epoch\":\"1\"", "\"epoch\":\"2\"", StringComparison.Ordinal));
        Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(nextEpoch, new ProtocolDocumentSigner(key).Sign(nextEpoch), actor));
        Assert.AreEqual("{\"name\":\"Original\"}", reader.VerifyAndRead(body, signature, actor).CanonicalPayload);
        Assert.ThrowsExactly<ProtocolCompatibilityException>(() => new ProtocolDocumentReader([]).VerifyAndRead(body, signature, actor));
    }

    [TestMethod]
    public async Task ConcurrentActorReadersOwnIndependentNativeVerifiersAndRejectEveryTamperedBody()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var actor = Actor(key.ExportSubjectPublicKeyInfo());
        var bytes = Encoding.UTF8.GetBytes(Body); var signature = new ProtocolDocumentSigner(key).Sign(bytes);
        var tampered = Encoding.UTF8.GetBytes(Body.Replace("Original", "Tampered", StringComparison.Ordinal));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var reader = new ProtocolDocumentReader(["human.fields.v1"]);
            for (var repeat = 0; repeat < 8; repeat++)
            {
                Assert.AreEqual(1L, reader.VerifyAndRead(bytes, signature, actor).Sequence);
                Assert.ThrowsExactly<ProtocolFormatException>(() => reader.VerifyAndRead(tampered, signature, actor));
            }
        }))).ConfigureAwait(false);
        Assert.AreEqual(1L, new ProtocolDocumentReader(["human.fields.v1"]).VerifyAndRead(bytes, signature, actor).Sequence);
    }

    [TestMethod]
    public void AllPublicEntryPointsKeepStrictCurvePrivateEncodingTrailingDataAndSizeRejection()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        foreach (var encoded in new byte[][] { [], [0], new byte[513], key.ExportPkcs8PrivateKey(), wrong.ExportSubjectPublicKeyInfo(),
            rsa.ExportSubjectPublicKeyInfo(), [.. key.ExportSubjectPublicKeyInfo(), 0] })
        {
            Assert.ThrowsExactly<ProtocolFormatException>(() => { using var verifier = new ProtocolDocumentVerifier(encoded); });
            Assert.ThrowsExactly<ProtocolFormatException>(() => Actor(encoded));
        }
        var valid = key.ExportSubjectPublicKeyInfo(); using var strict = new ProtocolDocumentVerifier(valid);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(valid)), strict.KeyId);
        Assert.IsTrue(strict.Verify("{}"u8, new ProtocolDocumentSigner(key).Sign("{}"u8)));
    }

    private static TrustedProtocolActor Actor(byte[] key) => new(Guid.Parse("94a81164-e980-4ec1-a566-cec36475b839"),
        Guid.Parse("11111111-1111-4111-8111-111111111111"), 1, key, ["human-operation"]);
}
