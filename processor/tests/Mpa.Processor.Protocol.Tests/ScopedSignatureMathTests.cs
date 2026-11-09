using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class ScopedSignatureMathTests
{
    private static readonly byte[] Document="{\"value\":1}"u8.ToArray();
    [TestMethod]
    public void ScopedSignatureMathReusesOnlyActualSuccessfulExactVerificationAcrossOwnedScopes()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signer=new ProtocolDocumentSigner(key);var signature=signer.Sign(Document);var encoded=key.ExportSubjectPublicKeyInfo();
        using var owner=new ProtocolSignatureVerificationOwner();
        using(owner.EnterScope())
        {
            using(var first=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(first.Verify(Document,signature));
            using(var second=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(second.Verify(Document,signature));
            Assert.AreEqual(new ProtocolSignatureVerificationStatistics(1,1,1,1),owner.Statistics);
        }
        using(owner.EnterScope())
        {
            using var reopened=new ProtocolDocumentVerifier(encoded);Assert.IsTrue(reopened.Verify(Document,signature));
            Assert.AreEqual(new ProtocolSignatureVerificationStatistics(1,1,1,2),owner.Statistics);
        }
        Assert.AreEqual(0,owner.Statistics.ActiveScopes);owner.Dispose();Assert.AreEqual(0,owner.Statistics.Entries);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ScopedSignatureMathChangedBodyActualKeyOrSignatureNeverReusesPositiveEvidence(int changed)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature=new ProtocolDocumentSigner(key).Sign(Document);var encoded=key.ExportSubjectPublicKeyInfo();
        using var owner=new ProtocolSignatureVerificationOwner();using var scope=owner.EnterScope();
        using(var first=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(first.Verify(Document,signature));
        var doc=changed==0?"{\"value\":2}"u8.ToArray():Document;
        if(changed==1) encoded=other.ExportSubjectPublicKeyInfo();
        if(changed==2)
        {
            var bytes=Decode(signature.Value);bytes[0]^=1;signature=signature with {Value=Encode(bytes)};
        }
        using var rejected=new ProtocolDocumentVerifier(encoded);Assert.IsFalse(rejected.Verify(doc,signature));
        Assert.AreEqual(0L,owner.Statistics.Hits);Assert.AreEqual(1,owner.Statistics.Entries);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ScopedSignatureMathStillRejectsMalformedAlgorithmKeyOrSignatureOnAnOtherwiseWarmEntry(int malformed)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signature=new ProtocolDocumentSigner(key).Sign(Document);var encoded=key.ExportSubjectPublicKeyInfo();
        using var owner=new ProtocolSignatureVerificationOwner();using var scope=owner.EnterScope();
        using var verifier=new ProtocolDocumentVerifier(encoded);Assert.IsTrue(verifier.Verify(Document,signature));
        var bad=malformed switch {0=>signature with {Algorithm="none"},1=>signature with {KeyId=new string('G',64)},2=>signature with {Value=signature.Value+"="},_=>throw new ArgumentOutOfRangeException(nameof(malformed))};
        Assert.ThrowsExactly<ProtocolFormatException>(()=>verifier.Verify(Document,bad));Assert.AreEqual(0L,owner.Statistics.Hits);Assert.AreEqual(1,owner.Statistics.Entries);
    }

    [TestMethod]
    public void ScopedSignatureMathWarmEvidenceDoesNotBypassEachVerifiersCanonicalDocumentLimit()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var doc=Encoding.UTF8.GetBytes("{\"value\":\""+new string('x',80)+"\"}");var signature=new ProtocolDocumentSigner(key).Sign(doc);var encoded=key.ExportSubjectPublicKeyInfo();
        using var owner=new ProtocolSignatureVerificationOwner();using var scope=owner.EnterScope();
        using(var valid=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(valid.Verify(doc,signature));
        using var bounded=new ProtocolDocumentVerifier(encoded,documentLimit:32);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>bounded.Verify(doc,signature));Assert.AreEqual(0L,owner.Statistics.Hits);
    }

    [TestMethod]
    public void ScopedSignatureMathEvictsAtItsExactOwnedBoundAndNeverRetainsFailedInputs()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signer=new ProtocolDocumentSigner(key);var encoded=key.ExportSubjectPublicKeyInfo();
        var docs=Enumerable.Range(0,3).Select(n=>Encoding.UTF8.GetBytes("{\"value\":"+n+"}")).ToArray();var signatures=docs.Select(doc=>signer.Sign(doc)).ToArray();
        using var owner=new ProtocolSignatureVerificationOwner(capacity:2);using var scope=owner.EnterScope();
        for(var i=0;i<3;i++) {using var verifier=new ProtocolDocumentVerifier(encoded);Assert.IsTrue(verifier.Verify(docs[i],signatures[i]));Assert.IsTrue(owner.Statistics.Entries<=2);}
        using(var evicted=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(evicted.Verify(docs[0],signatures[0]));
        Assert.AreEqual(new ProtocolSignatureVerificationStatistics(2,1,4,0),owner.Statistics);
        using(var bad=new ProtocolDocumentVerifier(encoded)) Assert.IsFalse(bad.Verify(docs[1],signatures[0]));
        Assert.AreEqual(new ProtocolSignatureVerificationStatistics(2,1,5,0),owner.Statistics);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>new ProtocolSignatureVerificationOwner(0));Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>new ProtocolSignatureVerificationOwner(1025));
    }

    [TestMethod]
    public void ScopedSignatureMathOwnerCannotRetireWhileActualScopesRemainAndDisposedEvidenceCannotBeReentered()
    {
        using var owner=new ProtocolSignatureVerificationOwner();using var scope=owner.EnterScope();
        Assert.ThrowsExactly<InvalidOperationException>(owner.Dispose);Assert.AreEqual(1,owner.Statistics.ActiveScopes);
        scope.Dispose();scope.Dispose();owner.Dispose();owner.Dispose();Assert.AreEqual(0,owner.Statistics.Entries);
        Assert.ThrowsExactly<ObjectDisposedException>(()=>owner.EnterScope());
    }

    [TestMethod]
    public async Task ScopedSignatureMathEscapedExecutionContextCannotUseAnAlreadyClosedScope()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signature=new ProtocolDocumentSigner(key).Sign(Document);var encoded=key.ExportSubjectPublicKeyInfo();
        using var owner=new ProtocolSignatureVerificationOwner();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Task<bool> escaped;
        using(owner.EnterScope())
        {
            using(var first=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(first.Verify(Document,signature));
            escaped=Task.Run(async()=>{await release.Task.ConfigureAwait(false);using var verifier=new ProtocolDocumentVerifier(encoded);return verifier.Verify(Document,signature);});
        }
        release.TrySetResult();Assert.IsTrue(await escaped.ConfigureAwait(false));Assert.AreEqual(new ProtocolSignatureVerificationStatistics(1,0,1,0),owner.Statistics);
    }

    [TestMethod]
    public void ScopedSignatureMathNestedIndependentOwnersRestoreTheirExactPreviousContext()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signature=new ProtocolDocumentSigner(key).Sign(Document);var encoded=key.ExportSubjectPublicKeyInfo();
        using var outer=new ProtocolSignatureVerificationOwner();using var inner=new ProtocolSignatureVerificationOwner();
        using(outer.EnterScope())
        {
            using(var first=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(first.Verify(Document,signature));
            using(inner.EnterScope()) {using var separate=new ProtocolDocumentVerifier(encoded);Assert.IsTrue(separate.Verify(Document,signature));}
            using(var restored=new ProtocolDocumentVerifier(encoded)) Assert.IsTrue(restored.Verify(Document,signature));
        }
        Assert.AreEqual(new ProtocolSignatureVerificationStatistics(1,0,1,1),outer.Statistics);Assert.AreEqual(new ProtocolSignatureVerificationStatistics(1,0,1,0),inner.Statistics);
    }

    [TestMethod]
    public void ScopedSignatureMathCanonicalKeyEvidenceNeverAliasesTheCallersMutableEncoding()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signature=new ProtocolDocumentSigner(key).Sign(Document);var encoded=key.ExportSubjectPublicKeyInfo();
        using var owner=new ProtocolSignatureVerificationOwner();using var scope=owner.EnterScope();
        using var verifier=new ProtocolDocumentVerifier(encoded);encoded[0]^=1;Assert.IsTrue(verifier.Verify(Document,signature));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>new ProtocolDocumentVerifier(encoded));Assert.AreEqual(1,owner.Statistics.Entries);
    }

    [TestMethod]
    public void ScopedSignatureMathDisposedVerifiersCannotReturnACachedSuccess()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var signature=new ProtocolDocumentSigner(key).Sign(Document);var encoded=key.ExportSubjectPublicKeyInfo();
        using var owner=new ProtocolSignatureVerificationOwner();using var scope=owner.EnterScope();
        var verifier=new ProtocolDocumentVerifier(encoded);Assert.IsTrue(verifier.Verify(Document,signature));verifier.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(()=>verifier.Verify(Document,signature));Assert.AreEqual(0L,owner.Statistics.Hits);
    }
    private static byte[] Decode(string value)=>Convert.FromBase64String(value.Replace('-','+').Replace('_','/')+"==");
    private static string Encode(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
}
