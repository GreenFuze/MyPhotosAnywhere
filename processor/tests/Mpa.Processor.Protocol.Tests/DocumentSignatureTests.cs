using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class DocumentSignatureTests
{
    [TestMethod]
    public void PublicOnlyGoldenRemainsVerifiable()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signature.json")));
        var root = fixture.RootElement;
        using var verifier = new ProtocolDocumentVerifier(Convert.FromBase64String(root.GetProperty("publicKeySpki").GetString()!));
        var signature = new DocumentSignature(root.GetProperty("Algorithm").GetString()!, root.GetProperty("KeyId").GetString()!, root.GetProperty("Value").GetString()!);
        var body = Encoding.UTF8.GetBytes(root.GetProperty("body").GetString()!);
        Assert.IsTrue(verifier.Verify(body, signature));
        Assert.IsFalse(verifier.Verify("{}"u8, signature));
        Assert.ThrowsExactly<ProtocolFormatException>(() => { using var invalid = new ProtocolDocumentVerifier(new byte[] { 0 }); });
    }

    [TestMethod]
    public void SignatureCoversEveryFieldAndIgnoresOnlySerializationOrder()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new ProtocolDocumentSigner(key);
        using var verifier = new ProtocolDocumentVerifier(key.ExportSubjectPublicKeyInfo());
        var signature = signer.Sign("{\"z\":1,\"a\":\"שלום\"}"u8);
        Assert.IsTrue(verifier.Verify(" {\"a\":\"שלום\", \"z\":1} "u8, signature));
        Assert.IsFalse(verifier.Verify("{\"a\":\"שלום\",\"z\":2}"u8, signature));
        Assert.IsFalse(verifier.Verify("{\"a\":\"שלום\",\"z\":1,\"future\":true}"u8, signature));
        var altered = Convert.FromBase64String(signature.Value.Replace('-', '+').Replace('_', '/') + "==");
        altered[0] ^= 1;
        var encoded = Convert.ToBase64String(altered).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.IsFalse(verifier.Verify("{\"a\":\"שלום\",\"z\":1}"u8, signature with { Value = encoded }));
    }

    [TestMethod]
    public void UnknownKeyCurveEncodingAndOversizedDocumentsAreRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var signer = new ProtocolDocumentSigner(key);
        using var verifier = new ProtocolDocumentVerifier(other.ExportSubjectPublicKeyInfo());
        var signature = signer.Sign("{}"u8);
        Assert.IsFalse(verifier.Verify("{}"u8, signature));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new ProtocolDocumentSigner(wrongCurve));
        Assert.ThrowsExactly<ProtocolFormatException>(() => verifier.Verify("{}"u8, signature with { Algorithm = "none" }));
        Assert.ThrowsExactly<ProtocolFormatException>(() => verifier.Verify("{}"u8, signature with { Value = signature.Value + "==" }));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new ProtocolDocumentSigner(key, 1).Sign("{}"u8));
        var appended = key.ExportSubjectPublicKeyInfo().Concat(new byte[] { 0 }).ToArray();
        Assert.ThrowsExactly<ProtocolFormatException>(() => { using var invalid = new ProtocolDocumentVerifier(appended); });
    }
}
