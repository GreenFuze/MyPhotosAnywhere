using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CanonicalProtocolPublicKeyPoolTests
{
    [TestMethod]
    public void RepeatedCanonicalEncodingOwnsFreshNativeVerifiersAfterEveryEarlierVerifierIsDisposed()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var encoded=key.ExportSubjectPublicKeyInfo();var original=encoded.ToArray();
        var signature=new ProtocolDocumentSigner(key).Sign("{}"u8);var first=new ProtocolDocumentVerifier(encoded);
        Assert.IsTrue(first.Verify("{}"u8,signature));first.Dispose();Array.Fill(encoded,(byte)0);
        using var second=new ProtocolDocumentVerifier(original);Assert.IsTrue(second.Verify("{}"u8,signature));
        Assert.ThrowsExactly<ObjectDisposedException>(()=>first.Verify("{}"u8,signature));
        Assert.IsFalse(second.Verify("{\"changed\":true}"u8,signature));
        using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.IsFalse(second.Verify("{}"u8,new ProtocolDocumentSigner(other).Sign("{}"u8)));
    }

    [TestMethod]
    public void NativeValidatedPublicEncodingPoolStaysBoundedAndEvictedEncodingsAreValidatedAgain()
    {
        using var first=ECDsa.Create(ECCurve.NamedCurves.nistP256);var encoded=first.ExportSubjectPublicKeyInfo();
        using(var initial=new ProtocolDocumentVerifier(encoded)) Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(encoded)),initial.KeyId);
        var key=Convert.ToBase64String(encoded);Assert.IsTrue(Entries().Contains(key));var originalEvidence=Entries()[key];
        for(var i=0;i<80;i++)
        {
            using var generated=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var verifier=new ProtocolDocumentVerifier(generated.ExportSubjectPublicKeyInfo());
            Assert.IsTrue(Entries().Count<=64);
        }
        Assert.AreEqual(64,Entries().Count);Assert.IsFalse(Entries().Contains(key));
        using var reopened=new ProtocolDocumentVerifier(encoded);Assert.IsTrue(reopened.Verify("{}"u8,new ProtocolDocumentSigner(first).Sign("{}"u8)));
        Assert.AreNotSame(originalEvidence,Entries()[key]);Assert.AreEqual(64,Entries().Count);
    }

    [TestMethod]
    public void RejectedPrivateWrongCurveAndNoncanonicalInputsCannotEnterOrEvictValidatedEncodingEvidence()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var wrong=ECDsa.Create(ECCurve.NamedCurves.nistP384);using var rsa=RSA.Create(2048);
        var encoded=key.ExportSubjectPublicKeyInfo();using var warm=new ProtocolDocumentVerifier(encoded);var mapKey=Convert.ToBase64String(encoded);
        var evidence=Entries()[mapKey];var count=Entries().Count;
        foreach(var invalid in new byte[][] {[],[0],new byte[513],key.ExportPkcs8PrivateKey(),wrong.ExportSubjectPublicKeyInfo(),rsa.ExportSubjectPublicKeyInfo(),[..encoded,0]})
        {
            Assert.ThrowsExactly<ProtocolFormatException>(()=>{using var rejected=new ProtocolDocumentVerifier(invalid);});
            Assert.AreEqual(count,Entries().Count);Assert.AreSame(evidence,Entries()[mapKey]);
        }
        Assert.IsTrue(warm.Verify("{}"u8,new ProtocolDocumentSigner(key).Sign("{}"u8)));
    }

    [TestMethod]
    public async Task ConcurrentWarmEncodingHitsStillVerifyEverySignatureWithIndependentOwnedNativeKeys()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var encoded=key.ExportSubjectPublicKeyInfo();var signature=new ProtocolDocumentSigner(key).Sign("{}"u8);
        using(var warm=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(warm.Verify("{}"u8,signature));
        await Task.WhenAll(Enumerable.Range(0,16).Select(_=>Task.Run(()=>
        {
            for(var i=0;i<8;i++)
            {
                using var verifier=new ProtocolDocumentVerifier(encoded);Assert.IsTrue(verifier.Verify("{}"u8,signature));
                Assert.IsFalse(verifier.Verify("{\"tampered\":true}"u8,signature));
            }
        }))).ConfigureAwait(false);
        Assert.IsTrue(Entries().Count<=64);
    }

    // Inspect the actual private bound without adding a production/test-only API.
    // Cryptographic, mutation, disposal and concurrent behavior use public APIs above.
    private static IDictionary Entries()
    {
        var type=typeof(ProtocolDocumentVerifier).Assembly.GetType("Mpa.Processor.Protocol.CanonicalProtocolPublicKey",true)!;
        var pool=type.GetField("Evidence",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        return (IDictionary)pool.GetType().GetField("_values",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(pool)!;
    }
}
