using System.Security.Cryptography;

namespace Mpa.Processor.Protocol;

/// <summary>Immutable mathematical encoding evidence only. It is not an enrolled
/// actor, signature conclusion, epoch or permission. The only factory performs the
/// real native P-256/canonical checks; no native handle survives in this value.</summary>
internal sealed class CanonicalProtocolPublicKey
{
    private readonly byte[] _encoded;
    public string KeyId { get; }

    private CanonicalProtocolPublicKey(byte[] exported)
    {
        _encoded = exported;
        KeyId = Convert.ToHexStringLower(SHA256.HashData(exported));
    }

    // Transfers the real imported native owner to exactly one verifier. The exported
    // array is newly owned, private and never exposed through mutable memory.
    internal static (CanonicalProtocolPublicKey PublicKey, ECDsa NativeKey) ImportAndValidate(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 1 or > 512) throw new ProtocolFormatException("Public key exceeds its encoding bound.");
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(encoded, out var consumed);
            if (consumed != encoded.Length) throw new ProtocolFormatException("Trailing public-key data.");
            SignatureEncoding.RequireP256(key);
            var exported = key.ExportSubjectPublicKeyInfo();
            if (!encoded.SequenceEqual(exported)) throw new ProtocolFormatException("Non-canonical public-key data.");
            return (new(exported), key);
        }
        catch (CryptographicException error) { key.Dispose(); throw new ProtocolFormatException("Invalid public-key encoding.", error); }
        catch { key.Dispose(); throw; }
    }

    internal ECDsa ImportForVerification()
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(_encoded, out var consumed);
            if (consumed != _encoded.Length) throw new ProtocolFormatException("Trailing public-key data.");
            return key;
        }
        catch (CryptographicException error) { key.Dispose(); throw new ProtocolFormatException("Invalid public-key encoding.", error); }
        catch { key.Dispose(); throw; }
    }
}
