using System.Security.Cryptography;

namespace Mpa.Processor.Protocol;

public sealed record DocumentSignature(string Algorithm, string KeyId, string Value);

/// <summary>Signs canonical bytes. The caller owns the supplied key/secure-vault lease.</summary>
public sealed class ProtocolDocumentSigner
{
    private readonly ECDsa _key;
    private readonly CanonicalJson _canonical;
    public string KeyId { get; }

    public ProtocolDocumentSigner(ECDsa key, int documentLimit = 256 * 1024)
    {
        ArgumentNullException.ThrowIfNull(key);
        SignatureEncoding.RequireP256(key);
        _key = key;
        _canonical = new CanonicalJson(documentLimit);
        KeyId = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }

    public DocumentSignature Sign(ReadOnlySpan<byte> document)
    {
        var canonical = _canonical.Encode(document);
        var bytes = _key.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (bytes.Length != 64) throw new InvalidOperationException("P-256 signer returned an invalid signature length.");
        return new DocumentSignature("ES256", KeyId, SignatureEncoding.Encode(bytes));
    }
}

/// <summary>Owns an imported PUBLIC key. Its caller must separately establish actor/library/epoch
/// authority through pairing; a valid self-supplied key is never sufficient authority.</summary>
public sealed class ProtocolDocumentVerifier : IDisposable
{
    private readonly ECDsa _key;
    private readonly CanonicalJson _canonical;
    public string KeyId { get; }

    public ProtocolDocumentVerifier(ReadOnlySpan<byte> publicKeyInfo, int documentLimit = 256 * 1024)
    {
        if (publicKeyInfo.Length is < 1 or > 512) throw new ProtocolFormatException("Public key exceeds its encoding bound.");
        _canonical = new CanonicalJson(documentLimit);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(publicKeyInfo, out var consumed);
            if (consumed != publicKeyInfo.Length) throw new ProtocolFormatException("Trailing public-key data.");
            SignatureEncoding.RequireP256(key);
            if (!publicKeyInfo.SequenceEqual(key.ExportSubjectPublicKeyInfo())) throw new ProtocolFormatException("Non-canonical public-key data.");
            KeyId = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            _key = key;
        }
        catch (CryptographicException error)
        {
            key.Dispose();
            throw new ProtocolFormatException("Invalid public-key encoding.", error);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public bool Verify(ReadOnlySpan<byte> document, DocumentSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Algorithm != "ES256" || signature.KeyId is null || signature.KeyId.Length != 64 ||
            signature.KeyId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolFormatException("Unsupported signature/key encoding.");
        var bytes = SignatureEncoding.Decode(signature.Value);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature.KeyId), Convert.FromHexString(KeyId))) return false;
        var canonical = _canonical.Encode(document);
        return _key.VerifyData(canonical, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose() => _key.Dispose();
}

internal static class SignatureEncoding
{
    internal static void RequireP256(ECDsa key)
    {
        if (key.KeySize != 256 || key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new ProtocolFormatException("Protocol signatures require the NIST P-256 curve.");
    }

    internal static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Decode(string value)
    {
        if (value is null || value.Length != 86 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ProtocolFormatException("Signature must be canonical unpadded base64url for 64 bytes.");
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "==");
        if (bytes.Length != 64 || Encode(bytes) != value) throw new ProtocolFormatException("Non-canonical signature encoding.");
        return bytes;
    }
}
