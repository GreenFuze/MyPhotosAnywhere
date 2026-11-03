using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mpa.Processor.Protocol;
using System.Text.Json.Serialization;

namespace Mpa.Processor.Lan;

/// <summary>Borrowed from LanIdentityOwner. Its owner must outlive every server connection and
/// signing operation. TLS and document signing deliberately have independent private keys.</summary>
public sealed class ProcessorLanIdentity : IDisposable
{
    private readonly ECDsa _tlsKey;
    private readonly ECDsa _signingKey;
    private readonly ProtocolDocumentSigner _signer;
    private readonly object _gate = new();
    private bool _disposed;
    public Guid LibraryId { get; }
    public Guid ProcessorId { get; }
    public string DnsName => "mpa-" + ProcessorId.ToString("N") + ".local";
    public string TlsKeyId { get; }
    public string SigningKeyId => _signer.KeyId;
    [JsonIgnore]
    public X509Certificate2 ServerCertificate { get; }

    private ProcessorLanIdentity(Guid libraryId, Guid processorId, ECDsa tlsKey, ECDsa signingKey, X509Certificate2 certificate)
    {
        LibraryId = libraryId; ProcessorId = processorId; _tlsKey = tlsKey; _signingKey = signingKey;
        ServerCertificate = certificate;
        TlsKeyId = Convert.ToHexStringLower(SHA256.HashData(tlsKey.ExportSubjectPublicKeyInfo()));
        _signer = new ProtocolDocumentSigner(signingKey);
        if (TlsKeyId == _signer.KeyId) throw new InvalidDataException("TLS and document signing identities must use separate private keys.");
    }

    internal static ProcessorLanIdentity Create(Guid libraryId, Guid processorId, DateTimeOffset now)
    {
        var tls = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECDsa? signing = null; X509Certificate2? certificate = null;
        try
        {
            signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            certificate = CreateCertificate(processorId, tls, now);
            return new(libraryId, processorId, tls, signing, certificate);
        }
        catch { certificate?.Dispose(); signing?.Dispose(); tls.Dispose(); throw; }
    }

    private static X509Certificate2 CreateCertificate(Guid processorId, ECDsa key, DateTimeOffset now)
    {
        var request = new CertificateRequest(new X500DistinguishedName("CN=MPA Processor " + processorId.ToString("N")), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("mpa-" + processorId.ToString("N") + ".local");
        request.CertificateExtensions.Add(names.Build());
        return ForTransport(request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2)));
    }

    internal static X509Certificate2 ForTransport(X509Certificate2 owned)
    {
        if (!OperatingSystem.IsWindows()) return owned;
        // Schannel requires a named key container. Import only in memory, CurrentUser, without
        // PersistKeySet/Exportable: .NET owns deletion of this temporary OS-protected container.
        // The durable PKCS8 record stays in the credential vault; no PFX file is written.
        using (owned)
        {
            var pfx = owned.Export(X509ContentType.Pkcs12);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }

    public byte[] ExportSigningPublicKey()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _signingKey.ExportSubjectPublicKeyInfo(); }
    }
    public DocumentSignature SignDocument(ReadOnlySpan<byte> document)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _signer.Sign(document); }
    }

    internal byte[] EncodePrivateRecord()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var tls = _tlsKey.ExportPkcs8PrivateKey();
            byte[]? signing = null;
            try
            {
                signing = _signingKey.ExportPkcs8PrivateKey();
                var certificate = ServerCertificate.RawData;
                var output = new byte[checked(8 + 16 + 16 + 12 + certificate.Length + tls.Length + signing.Length)];
                "MPALAN01"u8.CopyTo(output);
                if (!LibraryId.TryWriteBytes(output.AsSpan(8, 16), bigEndian: true, out var first) || first != 16 ||
                    !ProcessorId.TryWriteBytes(output.AsSpan(24, 16), bigEndian: true, out var second) || second != 16)
                    throw new InvalidOperationException("LAN identity UUID encoding failed.");
                var cursor = 40;
                WritePart(output, ref cursor, certificate); WritePart(output, ref cursor, tls); WritePart(output, ref cursor, signing);
                return output;
            }
            finally { CryptographicOperations.ZeroMemory(tls); if (signing is not null) CryptographicOperations.ZeroMemory(signing); }
        }
    }

    internal static ProcessorLanIdentity Decode(ReadOnlySpan<byte> bytes, Guid libraryId, Guid processorId, DateTimeOffset now, out bool renew)
    {
        if (bytes.Length is < 52 or > 16384 || !bytes[..8].SequenceEqual("MPALAN01"u8) ||
            new Guid(bytes.Slice(8, 16), bigEndian: true) != libraryId || new Guid(bytes.Slice(24, 16), bigEndian: true) != processorId)
            throw new InvalidDataException("Protected LAN identity does not match this library/Processor. Existing keys were preserved.");
        var cursor = 40;
        var publicCertificate = ReadPart(bytes, ref cursor, 4096);
        var tlsBytes = ReadPart(bytes, ref cursor, 4096);
        var signingBytes = ReadPart(bytes, ref cursor, 4096);
        if (cursor != bytes.Length) throw new InvalidDataException("Trailing LAN identity data.");
        var tls = ECDsa.Create(); ECDsa? signing = null; X509Certificate2? certificate = null;
        try
        {
            tls.ImportPkcs8PrivateKey(tlsBytes, out var tlsRead);
            if (tlsRead != tlsBytes.Length || tls.KeySize != 256 || tls.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("Invalid LAN TLS private-key format.");
            signing = ECDsa.Create(); signing.ImportPkcs8PrivateKey(signingBytes, out var signingRead);
            if (signingRead != signingBytes.Length) throw new InvalidDataException("Trailing signing private-key data.");
            using var stored = X509CertificateLoader.LoadCertificate(publicCertificate);
            if (!stored.RawData.AsSpan().SequenceEqual(publicCertificate) || stored.GetNameInfo(X509NameType.DnsName, false) != "mpa-" + processorId.ToString("N") + ".local")
                throw new InvalidDataException("LAN certificate encoding/name does not match its Processor identity.");
            using var certificateKey = stored.GetECDsaPublicKey() ?? throw new InvalidDataException("LAN certificate is not an elliptic-curve certificate.");
            if (!certificateKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(tls.ExportSubjectPublicKeyInfo()))
                throw new InvalidDataException("LAN certificate does not match its protected private key.");
            if (new DateTimeOffset(stored.NotBefore.ToUniversalTime()) > now.AddMinutes(5))
                throw new InvalidDataException("The stored LAN certificate is not valid at the current computer time. Check its date; no replacement key was generated.");
            renew = new DateTimeOffset(stored.NotAfter.ToUniversalTime()) <= now.AddDays(30);
            certificate = renew ? CreateCertificate(processorId, tls, now) : ForTransport(stored.CopyWithPrivateKey(tls));
            return new(libraryId, processorId, tls, signing, certificate);
        }
        catch { certificate?.Dispose(); signing?.Dispose(); tls.Dispose(); throw; }
    }

    private static void WritePart(Span<byte> output, ref int cursor, ReadOnlySpan<byte> part)
    {
        BinaryPrimitives.WriteInt32BigEndian(output.Slice(cursor, 4), part.Length); cursor += 4;
        part.CopyTo(output[cursor..]); cursor += part.Length;
    }
    private static ReadOnlySpan<byte> ReadPart(ReadOnlySpan<byte> input, ref int cursor, int bound)
    {
        if (cursor > input.Length - 4) throw new InvalidDataException("Truncated LAN identity record.");
        var count = BinaryPrimitives.ReadInt32BigEndian(input.Slice(cursor, 4)); cursor += 4;
        if (count < 1 || count > bound || count > input.Length - cursor) throw new InvalidDataException("LAN identity component exceeds its bound.");
        var part = input.Slice(cursor, count); cursor += count; return part;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            try { ServerCertificate.Dispose(); }
            finally { try { _tlsKey.Dispose(); } finally { _signingKey.Dispose(); } }
        }
    }
    public override string ToString() => "[protected Processor LAN identity]";
}
