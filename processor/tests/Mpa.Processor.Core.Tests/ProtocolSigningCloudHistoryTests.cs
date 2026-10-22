using System.Numerics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class ProtocolSigningJournalTests
{
    [TestMethod]
    public void ExactSealedCloudHistoryIsReadOnlyAndPreservesBothActorStreams()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var controller = fixture.Reserve(Guid.NewGuid());
        var first = fixture.Journal.Seal(controller.Id, controller.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(controller.Body.Span));
        var publisher = fixture.Journal.Reserve(Guid.NewGuid(), fixture.PublisherId, 1, "catalog-commit", n => fixture.Body(n, controller: false));
        var second = fixture.Journal.Seal(publisher.Id, publisher.BodySha256, new ProtocolDocumentSigner(fixture.Publisher).Sign(publisher.Body.Span));
        fixture.Space.Available = 0;
        Assert.AreEqual(first.Id, fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, first.Envelope!.Value.Span).Id);
        Assert.AreEqual(second.EnvelopeSha256, fixture.Journal.AssertRetainedCloudEnvelope(fixture.PublisherId, 1, second.Envelope!.Value.Span).EnvelopeSha256);
        Assert.AreEqual(2L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
    }

    [TestMethod]
    public void VerifiedCloudAheadOfRestoredJournalBlocksWithoutAdvancingOrReplacingItsCounter()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); fixture.Reserve(Guid.NewGuid());
        var future = Envelope(fixture, fixture.Body(2));
        Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, future));
        Assert.AreEqual(1L, fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
        Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
        // No recovery occurred implicitly. The next reservation is still exactly 2.
        Assert.AreEqual(2L, fixture.Reserve(Guid.NewGuid()).Sequence);
    }

    [TestMethod]
    public void CloudEnvelopeForAnUnsealedIntentRequiresRecoveryRatherThanAdoptingItsSignature()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var intent = fixture.Reserve(Guid.NewGuid()); var incoming = Envelope(fixture, intent.Body.ToArray());
        Assert.ThrowsExactly<SigningHistoryRecoveryRequiredException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, incoming));
        Assert.IsNull(fixture.Journal.Read(intent.Id)!.Envelope); Assert.AreEqual(1L, fixture.Scalar("SELECT count(*) FROM signing_intent"));
    }

    [TestMethod]
    public void ConflictingCloudBodyOrResignedEnvelopeCannotReplaceTheRetainedSealedBytes()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var intent = fixture.Reserve(Guid.NewGuid());
        var sealedIntent = fixture.Journal.Seal(intent.Id, intent.BodySha256, new ProtocolDocumentSigner(fixture.Controller).Sign(intent.Body.Span));
        var conflict = Envelope(fixture, fixture.Body(1, note: "other"));
        Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, conflict));
        using var original = JsonDocument.Parse(sealedIntent.Envelope!.Value);
        var value = original.RootElement.GetProperty("signature").GetProperty("value").GetString()!;
        var raw = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "==");
        var order = new BigInteger(Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"), isUnsigned: true, isBigEndian: true);
        var complement = (order - new BigInteger(raw.AsSpan(32), isUnsigned: true, isBigEndian: true)).ToByteArray(isUnsigned: true, isBigEndian: true);
        raw.AsSpan(32).Clear(); complement.CopyTo(raw.AsSpan(64 - complement.Length));
        var signature = new DocumentSignature("ES256", new ProtocolDocumentSigner(fixture.Controller).KeyId,
            Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        var changedEnvelope = new SignedProtocolDocuments(Array.Empty<string>()).Encode(intent.Body.Span, signature);
        // (r,n-s) is a distinct valid ECDSA signature for the same body. Even this
        // cannot replace the exact immutable envelope retained before publication.
        Assert.IsFalse(changedEnvelope.AsSpan().SequenceEqual(sealedIntent.Envelope.Value.Span));
        using var verifier = new ProtocolDocumentVerifier(fixture.Controller.ExportSubjectPublicKeyInfo()); Assert.IsTrue(verifier.Verify(intent.Body.Span, signature));
        Assert.ThrowsExactly<ProtocolSigningConflictException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, changedEnvelope));
        CollectionAssert.AreEqual(sealedIntent.Envelope.Value.ToArray(), fixture.Journal.Read(intent.Id)!.Envelope!.Value.ToArray());
    }

    [TestMethod]
    public void InvalidSignatureForeignActorAndIncompleteLocalHistoryFailWithoutRecoveryMutation()
    {
        using var fixture = new Fixture(); fixture.Bootstrap(); var body = fixture.Body(99);
        var wrong = new SignedProtocolDocuments(Array.Empty<string>()).Encode(body, new ProtocolDocumentSigner(fixture.Publisher).Sign(body));
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, wrong));
        var valid = Envelope(fixture, body);
        Assert.ThrowsExactly<ProtocolFormatException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.PublisherId, 1, valid));
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.AssertRetainedCloudEnvelope(Guid.NewGuid(), 1, valid));
        var first = fixture.Reserve(Guid.NewGuid()); fixture.Reserve(Guid.NewGuid());
        fixture.Sql("DELETE FROM signing_intent WHERE sequence=1");
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Journal.AssertRetainedCloudEnvelope(fixture.ControllerId, 1, Envelope(fixture, first.Body.ToArray())));
        Assert.AreEqual(2L, fixture.Scalar("SELECT sequence FROM signing_actor WHERE role='controller'"));
    }
    private static byte[] Envelope(Fixture fixture, byte[] body) => new SignedProtocolDocuments(Array.Empty<string>()).Encode(body, new ProtocolDocumentSigner(fixture.Controller).Sign(body));
}
