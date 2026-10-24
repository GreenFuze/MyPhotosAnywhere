using System.Buffers.Binary;
using System.Security.Cryptography;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed record LibrarySigningPublicIdentity(Guid LibraryId, Guid InstallationId, Guid ControllerId, long ControllerEpoch,
    string ControllerPublicKeySpki, string ControllerKeyId, Guid PublisherId, long PublisherEpoch, string PublisherPublicKeySpki, string PublisherKeyId);

/// <summary>Native private-key owner; only its startup owner may create/import/dispose it.
/// Private records are binary, bounded and transient, never JSON strings, files or UI data.</summary>
internal sealed class LibrarySigningKeys : IDisposable
{
    private readonly ECDsa _controller;
    private readonly ECDsa _publisher;
    private bool _closed;
    public LibrarySigningPublicIdentity Public { get; }
    private static readonly string[] ControllerKinds = ["library-publisher-authorization", "library-source-configuration"];
    private static readonly string[] PublisherKinds = ["catalog-commit"];
    private static readonly string[] Capabilities = [CatalogPublisherAuthorization.Capability, CatalogSourceConfiguration.Capability,
        CatalogPublicationReader.Capability, DriveCatalogFacts.Capability];

    private LibrarySigningKeys(Guid library, Guid installation, Guid controller, long controllerEpoch, Guid publisher, long publisherEpoch,
        ECDsa controllerKey, ECDsa publisherKey)
    {
        if (library == Guid.Empty || installation == Guid.Empty || controller == Guid.Empty || publisher == Guid.Empty || controller == publisher ||
            controllerEpoch < 1 || publisherEpoch < 1) throw new InvalidDataException("Stored signing identities/epochs are invalid.");
        var controllerSigner = new ProtocolDocumentSigner(controllerKey); var publisherSigner = new ProtocolDocumentSigner(publisherKey);
        if (controllerSigner.KeyId == publisherSigner.KeyId) throw new InvalidDataException("Controller and publisher signing keys must be separate.");
        Public = new(library, installation, controller, controllerEpoch, Convert.ToBase64String(controllerKey.ExportSubjectPublicKeyInfo()), controllerSigner.KeyId,
            publisher, publisherEpoch, Convert.ToBase64String(publisherKey.ExportSubjectPublicKeyInfo()), publisherSigner.KeyId);
        _controller = controllerKey; _publisher = publisherKey;
    }

    internal static LibrarySigningKeys Create(Guid library, Guid installation)
    {
        var controller = ECDsa.Create(ECCurve.NamedCurves.nistP256); ECDsa? publisher = null;
        try { publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256); return new(library, installation, Guid.NewGuid(), 1, Guid.NewGuid(), 1, controller, publisher); }
        catch { publisher?.Dispose(); controller.Dispose(); throw; }
    }

    internal byte[] EncodePrivateRecord()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var controller = _controller.ExportPkcs8PrivateKey(); byte[]? publisher = null;
        try
        {
            publisher = _publisher.ExportPkcs8PrivateKey();
            if (controller.Length is < 1 or > 512 || publisher.Length is < 1 or > 512) throw new InvalidDataException("Native P-256 private-key encoding exceeds its bound.");
            var bytes = new byte[96 + controller.Length + publisher.Length]; "MPASIG01"u8.CopyTo(bytes); var offset = 8;
            foreach (var id in new[] { Public.LibraryId, Public.InstallationId, Public.ControllerId, Public.PublisherId })
            { id.ToByteArray(bigEndian: true).CopyTo(bytes, offset); offset += 16; }
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), Public.ControllerEpoch); offset += 8;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), Public.PublisherEpoch); offset += 8;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), controller.Length); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), publisher.Length); offset += 4;
            controller.CopyTo(bytes, offset); publisher.CopyTo(bytes, offset + controller.Length); return bytes;
        }
        finally { CryptographicOperations.ZeroMemory(controller); if (publisher is not null) CryptographicOperations.ZeroMemory(publisher); }
    }

    internal static LibrarySigningKeys Decode(ReadOnlySpan<byte> bytes, Guid library, Guid installation)
    {
        if (bytes.Length is < 98 or > 1120 || !bytes[..8].SequenceEqual("MPASIG01"u8)) throw new InvalidDataException("Unknown native library-signing record.");
        var storedLibrary = new Guid(bytes.Slice(8, 16), bigEndian: true); var storedInstallation = new Guid(bytes.Slice(24, 16), bigEndian: true);
        if (storedLibrary != library || storedInstallation != installation) throw new InvalidDataException("Private signing record belongs to another library/installation; never replace it.");
        var controller = new Guid(bytes.Slice(40, 16), bigEndian: true); var publisher = new Guid(bytes.Slice(56, 16), bigEndian: true);
        var controllerEpoch = BinaryPrimitives.ReadInt64LittleEndian(bytes[72..]); var publisherEpoch = BinaryPrimitives.ReadInt64LittleEndian(bytes[80..]);
        var controllerBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes[88..]); var publisherBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes[92..]);
        if (controllerBytes is < 1 or > 512 || publisherBytes is < 1 or > 512 || bytes.Length != 96 + controllerBytes + publisherBytes)
            throw new InvalidDataException("Truncated, oversized or trailing native signing-key data.");
        var controllerKey = ECDsa.Create(); ECDsa? publisherKey = null;
        try
        {
            controllerKey.ImportPkcs8PrivateKey(bytes.Slice(96, controllerBytes), out var first);
            publisherKey = ECDsa.Create(); publisherKey.ImportPkcs8PrivateKey(bytes.Slice(96 + controllerBytes, publisherBytes), out var second);
            if (first != controllerBytes || second != publisherBytes) throw new InvalidDataException("Native signing keys contain trailing data.");
            return new(library, installation, controller, controllerEpoch, publisher, publisherEpoch, controllerKey, publisherKey);
        }
        catch (Exception error) when (error is CryptographicException or ProtocolFormatException or ArgumentException)
        { publisherKey?.Dispose(); controllerKey.Dispose(); throw new InvalidDataException("Native signing keys cannot be decoded.", error); }
        catch { publisherKey?.Dispose(); controllerKey.Dispose(); throw; }
    }

    internal DocumentSignature Sign(bool controller, ReadOnlySpan<byte> canonicalDocument)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var bytes = new CanonicalJson().Encode(canonicalDocument);
        if (!canonicalDocument.SequenceEqual(bytes)) throw new ProtocolFormatException("Persist canonical document bytes before signing.");
        var key = controller ? _controller : _publisher;
        var authority = new TrustedProtocolActor(Public.LibraryId, controller ? Public.ControllerId : Public.PublisherId,
            controller ? Public.ControllerEpoch : Public.PublisherEpoch, key.ExportSubjectPublicKeyInfo(), controller ? ControllerKinds : PublisherKinds);
        var signature = new ProtocolDocumentSigner(key).Sign(bytes);
        _ = new ProtocolDocumentReader(Capabilities).VerifyAndRead(bytes, signature, authority);
        return signature;
    }
    public void Dispose() { if (_closed) return; try { _controller.Dispose(); } finally { _publisher.Dispose(); _closed = true; } }
}
