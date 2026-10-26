using System.Security.Cryptography;

namespace Mpa.Processor.Protocol;

/// <summary>Immutable mathematical encoding evidence only. It is not an enrolled
/// actor, signature conclusion, epoch or permission. The only factory performs the
/// real native P-256/canonical checks; no native handle survives in this value.</summary>
internal sealed class CanonicalProtocolPublicKey
{
    private static readonly CanonicalKeyEvidencePool Evidence=new();
    private readonly byte[] _encoded;
    private readonly byte[] _x,_y;
    public string KeyId { get; }

    private CanonicalProtocolPublicKey(byte[] exported,ECParameters publicParameters)
    {
        if(publicParameters.D is not null || publicParameters.Q.X is not {Length:32} || publicParameters.Q.Y is not {Length:32})
            throw new ProtocolFormatException("Native canonical P-256 evidence requires only its exact public point.");
        _x=publicParameters.Q.X.ToArray();_y=publicParameters.Q.Y.ToArray();
        _encoded = exported;
        KeyId = Convert.ToHexStringLower(SHA256.HashData(exported));
    }

    // Transfers the real imported native owner to exactly one verifier. The exported
    // array is newly owned, private and never exposed through mutable memory.
    internal static (CanonicalProtocolPublicKey PublicKey, ECDsa NativeKey) ImportAndValidate(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 1 or > 512) throw new ProtocolFormatException("Public key exceeds its encoding bound.");
        // Own the exact bytes before lookup/import. This bounded pool retains
        // only successful native canonical P-256 encoding evidence, never native
        // handles, signatures, enrollment, actor scopes or current authority.
        var owned=encoded.ToArray();var encoding=Convert.ToBase64String(owned);
        if(Evidence.Find(encoding) is {} known) return (known,known.ImportForVerification());
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(owned, out var consumed);
            if (consumed != owned.Length) throw new ProtocolFormatException("Trailing public-key data.");
            SignatureEncoding.RequireP256(key);
            var exported = key.ExportSubjectPublicKeyInfo();
            if (!owned.AsSpan().SequenceEqual(exported)) throw new ProtocolFormatException("Non-canonical public-key data.");
            var evidence=new CanonicalProtocolPublicKey(exported,key.ExportParameters(includePrivateParameters:false));Evidence.Remember(encoding,evidence);
            return (evidence, key);
        }
        catch (CryptographicException error) { key.Dispose(); throw new ProtocolFormatException("Invalid public-key encoding.", error); }
        catch { key.Dispose(); throw; }
    }

    internal ECDsa ImportForVerification()
    {
        var key = ECDsa.Create();
        try
        {
            // Exact private copies of the point from the original strict native
            // canonical import. Each call still imports and owns a real public
            // key and performs native signature verification; no DER reparse or
            // provider curve/export queries are needed for this immutable value.
            key.ImportParameters(new ECParameters {Curve=ECCurve.NamedCurves.nistP256,Q=new ECPoint {X=_x.ToArray(),Y=_y.ToArray()}});
            return key;
        }
        catch (CryptographicException error) { key.Dispose(); throw new ProtocolFormatException("Invalid public-key encoding.", error); }
        catch { key.Dispose(); throw; }
    }

    /// <summary>Strictly bounded immutable mathematical evidence. A hit still
    /// imports an independently owned real key and verifies every actual signature.
    /// Permissions, policy, counters and source/Root observations are never pooled.</summary>
    private sealed class CanonicalKeyEvidencePool
    {
        private const int MaximumEntries=64;
        private readonly object _gate=new();
        private readonly Dictionary<string,CanonicalProtocolPublicKey> _values=new(StringComparer.Ordinal);
        private readonly Queue<string> _order=new();
        internal CanonicalProtocolPublicKey? Find(string encoding)
        {
            lock(_gate) {AssertBound();return _values.GetValueOrDefault(encoding);}
        }
        internal void Remember(string encoding,CanonicalProtocolPublicKey value)
        {
            if(Convert.ToBase64String(value._encoded)!=encoding) throw new InvalidOperationException("Canonical evidence changed its exact native encoding.");
            lock(_gate)
            {
                AssertBound();
                if(_values.TryGetValue(encoding,out var old))
                {
                    if(old.KeyId!=value.KeyId || !old._encoded.AsSpan().SequenceEqual(value._encoded)) throw new InvalidOperationException("Conflicting canonical key evidence.");
                    return;
                }
                if(_values.Count==MaximumEntries && !_values.Remove(_order.Dequeue())) throw new InvalidOperationException("Canonical evidence eviction lost its owner.");
                _values.Add(encoding,value);_order.Enqueue(encoding);AssertBound();
            }
        }
        private void AssertBound()
        {
            if(_values.Count>MaximumEntries || _values.Count!=_order.Count) throw new InvalidOperationException("Canonical key evidence exceeded its complete bound.");
        }
    }
}
