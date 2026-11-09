using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>A controller-signed publisher policy, not source/recognition/original-action
/// permission. The owner must select the current controller sequence, honor revocation
/// and separately validate source policy, ancestry and all generation dependencies.</summary>
public sealed class CatalogPublisherAuthorization
{
    public const string Capability = "library.publishers.v1";
    public Guid LibraryId { get; }
    public Guid ControllerId { get; }
    public long ControllerEpoch { get; }
    public long Sequence { get; }
    public CatalogWorkspace Workspace { get; }
    public TrustedProtocolActor? Publisher { get; }
    public string? PublisherKeyId { get; }
    public string EnvelopeSha256 { get; }
    internal CatalogPublisherAuthorization(VerifiedProtocolDocument approval, CatalogWorkspace workspace,
        TrustedProtocolActor? publisher, string? keyId, string envelopeHash)
    {
        LibraryId = approval.LibraryId; ControllerId = approval.ActorId; ControllerEpoch = approval.Epoch; Sequence = approval.Sequence;
        Workspace = workspace; Publisher = publisher; PublisherKeyId = keyId; EnvelopeSha256 = envelopeHash;
    }
}

public sealed partial class CatalogPublicationReader
{
    public CatalogPublisherAuthorization ReadPublisherAuthorization(ReadOnlySpan<byte> signedEnvelope, TrustedProtocolActor enrolledController)
    {
        ArgumentNullException.ThrowIfNull(enrolledController);
        var approval = new SignedProtocolDocuments([CatalogPublisherAuthorization.Capability]).VerifyAndRead(signedEnvelope, enrolledController);
        if (approval.Kind != "library-publisher-authorization" || !approval.RequiredCapabilities.Contains(CatalogPublisherAuthorization.Capability, StringComparer.Ordinal))
            throw new ProtocolFormatException("An enrolled controller's explicit publisher authorization is required.");
        using var document = Canonical(Encoding.UTF8.GetBytes(approval.CanonicalPayload)); var root = document.RootElement;
        if (root.EnumerateObject().Count() != 2) throw new ProtocolFormatException("Unexpected publisher policy fields.");
        var workspace = Property(root, "workspace");
        if (workspace.ValueKind != JsonValueKind.Object || workspace.EnumerateObject().Count() != 2 ||
            Text(workspace, "accountId") != _workspace.AccountId || Text(workspace, "folderId") != _workspace.FolderId || _workspace.FolderId == "root")
            throw new ProtocolFormatException("Publisher authorization cannot choose a different metadata workspace.");
        var publisher = Property(root, "publisher"); var envelopeHash = Convert.ToHexStringLower(SHA256.HashData(signedEnvelope));
        if (publisher.ValueKind == JsonValueKind.Null) return new(approval, _workspace, null, null, envelopeHash);
        if (publisher.ValueKind != JsonValueKind.Object || publisher.EnumerateObject().Count() != 4)
            throw new ProtocolFormatException("Invalid publisher enrollment fields.");
        var actor = Uuid(publisher, "actorId"); var epoch = Decimal(publisher, "epoch");
        var encoded = Text(publisher, "publicKeySpki"); var keyId = Text(publisher, "keyId");
        if (actor == approval.ActorId || encoded.Length is < 1 or > 684) throw new ProtocolFormatException("The catalog publisher requires a separate actor and bounded public key.");
        byte[] publicKey;
        try { publicKey = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new ProtocolFormatException("Invalid publisher public-key encoding.", error); }
        if (Convert.ToBase64String(publicKey) != encoded) throw new ProtocolFormatException("Publisher public key is not canonical base64.");
        using var verifier = new ProtocolDocumentVerifier(publicKey);
        if (keyId != verifier.KeyId || keyId == enrolledController.PublicKey.KeyId) throw new ProtocolFormatException("The catalog publisher requires its own matching public signing key.");
        return new(approval, _workspace, new(approval.LibraryId, actor, epoch, verifier.PublicKey, ["catalog-commit"]), keyId, envelopeHash);
    }
}
