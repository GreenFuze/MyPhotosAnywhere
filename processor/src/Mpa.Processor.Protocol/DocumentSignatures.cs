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

/// <summary>Owns a public-key verifier. Explicit owned scopes may reuse exact
/// successful cryptographic mathematics; callers must still establish actual
/// actor/library/epoch/history/permission on every read. No scope is a grant.</summary>
public sealed class ProtocolDocumentVerifier : IDisposable
{
    private readonly object _gate=new();
    private ECDsa? _key;
    private readonly CanonicalJson _canonical;
    private readonly ProtocolSignatureVerificationOwner.Scope? _mathematics;
    private bool _disposed;
    internal CanonicalProtocolPublicKey PublicKey { get; }
    public string KeyId => PublicKey.KeyId;

    public ProtocolDocumentVerifier(ReadOnlySpan<byte> publicKeyInfo, int documentLimit = 256 * 1024)
    {
        if(publicKeyInfo.Length is <1 or >512) throw new ProtocolFormatException("Public key exceeds its encoding bound.");
        _canonical=new CanonicalJson(documentLimit);_mathematics=ProtocolSignatureVerificationOwner.Capture();
        if(_mathematics is not null && CanonicalProtocolPublicKey.FindValidated(publicKeyInfo) is {} known) PublicKey=known;
        else (PublicKey,_key)=CanonicalProtocolPublicKey.ImportAndValidate(publicKeyInfo);
    }

    internal ProtocolDocumentVerifier(CanonicalProtocolPublicKey publicKey, int documentLimit = 256 * 1024)
    {
        ArgumentNullException.ThrowIfNull(publicKey);_canonical=new CanonicalJson(documentLimit);PublicKey=publicKey;
        _mathematics=ProtocolSignatureVerificationOwner.Capture();
        if(_mathematics is null) _key=publicKey.ImportForVerification();
    }

    public bool Verify(ReadOnlySpan<byte> document,DocumentSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        lock(_gate)
        {
            if(signature.Algorithm!="ES256" || signature.KeyId is null || signature.KeyId.Length!=64 ||
                signature.KeyId.Any(character=>character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new ProtocolFormatException("Unsupported signature/key encoding.");
            var bytes=SignatureEncoding.Decode(signature.Value);
            if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature.KeyId),Convert.FromHexString(KeyId))) return false;
            // Parsing, limits and canonical/algorithm/key/signature checks are never
            // memoized. A changed body, actual key or signature gets its own math.
            var canonical=_canonical.Encode(document);ObjectDisposedException.ThrowIf(_disposed,this);
            var scope=_mathematics is {IsActive:true}?_mathematics:null;
            var fingerprint=scope is null?null:ProtocolSignatureVerificationOwner.Fingerprint(KeyId,canonical,bytes);
            if(scope is not null && fingerprint is not null && scope.Contains(fingerprint)) return true;
            _key??=PublicKey.ImportForVerification();scope?.NativeAttempt();
            var valid=_key.VerifyData(canonical,bytes,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if(valid && scope is not null && fingerprint is not null) scope.Remember(fingerprint);
            return valid;
        }
    }
    public void Dispose()
    {lock(_gate) {if(_disposed) return;_key?.Dispose();_key=null;_disposed=true;}}
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
