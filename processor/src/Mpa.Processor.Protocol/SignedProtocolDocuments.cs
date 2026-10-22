using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>One bounded immutable Drive file containing a canonical document and its
/// detached signature. No embedded public key is accepted as authority. Verification
/// requires an independently enrolled actor; packaging alone grants no permission.</summary>
public sealed class SignedProtocolDocuments(IEnumerable<string> supportedCapabilities)
{
    private readonly CanonicalJson _codec = new(ProtocolDocumentReader.MaximumDocumentBytes);
    private readonly ProtocolDocumentReader _reader = new(supportedCapabilities);

    public byte[] Encode(ReadOnlySpan<byte> canonicalDocument, DocumentSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature); ValidateSignature(signature);
        var body = _codec.Encode(canonicalDocument);
        if (!canonicalDocument.SequenceEqual(body)) throw new ProtocolFormatException("Signed document input must already be canonical JSON.");
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ProtocolFormatException("Signed document must be an object.");
        return _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-signed-document", major = 1,
            document = document.RootElement, signature = new { algorithm = signature.Algorithm, keyId = signature.KeyId, value = signature.Value } }));
    }

    public VerifiedProtocolDocument VerifyAndRead(ReadOnlySpan<byte> canonicalEnvelope, TrustedProtocolActor authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var bytes = _codec.Encode(canonicalEnvelope);
        if (!canonicalEnvelope.SequenceEqual(bytes)) throw new ProtocolFormatException("Signed envelope must already be canonical JSON.");
        using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
        Fields(root, "format", "major", "document", "signature");
        if (Text(root, "format") != "mpa-signed-document") throw new ProtocolFormatException("Unsupported signed document format.");
        var majorValue = root.GetProperty("major");
        if (majorValue.ValueKind != JsonValueKind.Number || !majorValue.TryGetInt32(out var major)) throw new ProtocolFormatException("Signed document major must be an integer.");
        if (major != 1) throw new ProtocolCompatibilityException("This signed document requires a compatible MPA update.");
        var body = root.GetProperty("document");
        if (body.ValueKind != JsonValueKind.Object) throw new ProtocolFormatException("Signed document must be an object.");
        var signature = root.GetProperty("signature"); Fields(signature, "algorithm", "keyId", "value");
        var captured = new DocumentSignature(Text(signature, "algorithm"), Text(signature, "keyId"), Text(signature, "value"));
        ValidateSignature(captured);
        return _reader.VerifyAndRead(JsonSerializer.SerializeToUtf8Bytes(body), captured, authority);
    }

    private static void ValidateSignature(DocumentSignature signature)
    {
        if (signature.Algorithm != "ES256" || signature.KeyId is not { Length: 64 } ||
            signature.KeyId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolFormatException("Unsupported signature/key encoding.");
        _ = SignatureEncoding.Decode(signature.Value);
    }
    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length ||
            fields.Any(field => !value.TryGetProperty(field, out _))) throw new ProtocolFormatException("Unexpected signed document fields.");
    }
    private static string Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) throw new ProtocolFormatException("Missing signed document text field.");
        return field.GetString()!;
    }
}
