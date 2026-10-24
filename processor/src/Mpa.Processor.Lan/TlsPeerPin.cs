using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mpa.Processor.Lan;

/// <summary>Private paired-key trust only. A discovery advertisement is never a pin source.
/// Chain trust may be replaced by an authenticated OOB/paired pin; name, time and key checks remain.</summary>
public static class TlsPeerPin
{
    public static bool Matches(X509Certificate? certificate, string expectedKeyId, SslPolicyErrors errors, DateTimeOffset now)
    {
        if (certificate is not X509Certificate2 leaf || expectedKeyId is not { Length: 64 } ||
            expectedKeyId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0 || leaf.RawData.Length > 4096 ||
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime()) > now || new DateTimeOffset(leaf.NotAfter.ToUniversalTime()) <= now)
            return false;
        try
        {
            using var key = leaf.GetECDsaPublicKey();
            if (key is null || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") return false;
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(key.ExportSubjectPublicKeyInfo()), Convert.FromHexString(expectedKeyId));
        }
        catch (CryptographicException) { return false; } // Untrusted/unsupported peer certificate, never an accepted identity.
    }
}
