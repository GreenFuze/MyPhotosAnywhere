using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public sealed class ProtocolCompatibilityException(string message) : InvalidOperationException(message);

/// <summary>Constructed only from a previously approved pairing record, never from a received document.</summary>
public sealed class TrustedProtocolActor
{
    public Guid LibraryId { get; }
    public Guid ActorId { get; }
    public long Epoch { get; }
    private readonly byte[] _publicKey;
    private readonly HashSet<string> _kinds;
    internal ReadOnlySpan<byte> PublicKey => _publicKey;
    internal bool CanPublish(string kind) => _kinds.Contains(kind);

    public TrustedProtocolActor(Guid libraryId, Guid actorId, long epoch, ReadOnlySpan<byte> publicKey, IEnumerable<string> allowedKinds)
    {
        ArgumentNullException.ThrowIfNull(allowedKinds);
        if (libraryId == Guid.Empty || actorId == Guid.Empty || epoch < 1) throw new ArgumentException("Invalid approved actor context.");
        using var check = new ProtocolDocumentVerifier(publicKey);
        _publicKey = publicKey.ToArray();
        _kinds = new HashSet<string>(allowedKinds, StringComparer.Ordinal);
        if (_kinds.Count is < 1 or > 32 || _kinds.Any(kind => !ProtocolDocumentReader.IsIdentifier(kind)))
            throw new ArgumentException("Invalid approved document kinds.", nameof(allowedKinds));
        LibraryId = libraryId; ActorId = actorId; Epoch = epoch;
    }
}

public sealed record VerifiedProtocolDocument(Guid LibraryId, Guid ActorId, long Epoch, long Sequence,
    string Kind, int Minor, ReadOnlyCollection<string> RequiredCapabilities, string CanonicalPayload);

public sealed class ProtocolDocumentReader
{
    public const int MaximumDocumentBytes = 256 * 1024;
    private readonly HashSet<string> _capabilities;

    public ProtocolDocumentReader(IEnumerable<string> supportedCapabilities)
    {
        ArgumentNullException.ThrowIfNull(supportedCapabilities);
        _capabilities = new HashSet<string>(supportedCapabilities, StringComparer.Ordinal);
        if (_capabilities.Count > 64 || _capabilities.Any(capability => !IsIdentifier(capability)))
            throw new ArgumentException("Invalid supported capabilities.", nameof(supportedCapabilities));
    }

    public VerifiedProtocolDocument VerifyAndRead(ReadOnlySpan<byte> body, DocumentSignature signature, TrustedProtocolActor authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        using var verifier = new ProtocolDocumentVerifier(authority.PublicKey, MaximumDocumentBytes);
        if (!verifier.Verify(body, signature)) throw new ProtocolFormatException("Document signature does not match the approved actor.");
        return ReadBody(body, authority);
    }

    /// <summary>Validate an owned outgoing body before reserving/signing it. Returns no
    /// verified document and grants no incoming authority; admission still verifies its signature.</summary>
    public void ValidateForSigning(ReadOnlySpan<byte> body, TrustedProtocolActor ownedIdentity)
    {
        ArgumentNullException.ThrowIfNull(ownedIdentity);
        _ = ReadBody(body, ownedIdentity);
    }

    private VerifiedProtocolDocument ReadBody(ReadOnlySpan<byte> body, TrustedProtocolActor authority)
    {
        var canonical = new CanonicalJson(MaximumDocumentBytes).Encode(body);
        using var document = JsonDocument.Parse(canonical);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || String(root, "format") != "mpa") throw new ProtocolFormatException("Invalid protocol document format.");
        var library = Uuid(root, "libraryId");
        var actor = Uuid(root, "actorId");
        var epoch = Decimal(root, "epoch");
        var sequence = Decimal(root, "sequence");
        var kind = String(root, "kind");
        if (library != authority.LibraryId || actor != authority.ActorId || epoch != authority.Epoch || !authority.CanPublish(kind))
            throw new ProtocolFormatException("Document is outside the approved actor/library/epoch/kind scope.");
        var major = Integer(root, "major");
        var minor = Integer(root, "minor");
        if (major != 1) throw new ProtocolCompatibilityException("This MPA library protocol requires a compatible app/Processor update.");
        var capabilities = Property(root, "requiredCapabilities");
        if (capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() > 64)
            throw new ProtocolFormatException("Invalid required capability list.");
        var required = new List<string>();
        foreach (var item in capabilities.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !IsIdentifier(item.GetString()!) ||
                (required.Count != 0 && StringComparer.Ordinal.Compare(required[^1], item.GetString()) >= 0))
                throw new ProtocolFormatException("Capabilities must be unique and ordinally sorted.");
            required.Add(item.GetString()!);
        }
        if (required.Any(capability => !_capabilities.Contains(capability)))
            throw new ProtocolCompatibilityException("This document requires an unsupported capability. Keep the last verified library view.");
        var payload = Property(root, "payload");
        if (payload.ValueKind != JsonValueKind.Object) throw new ProtocolFormatException("Document payload must be an object.");
        return new(library, actor, epoch, sequence, kind, minor, required.AsReadOnly(),
            Encoding.UTF8.GetString(new CanonicalJson(MaximumDocumentBytes).Encode(Encoding.UTF8.GetBytes(payload.GetRawText()))));
    }

    internal static bool IsIdentifier(string value) => value.Length is >= 1 and <= 64 &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private static JsonElement Property(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value :
        throw new ProtocolFormatException("Missing required protocol field: " + name);

    private static string String(JsonElement root, string name)
    {
        var value = Property(root, name);
        if (value.ValueKind != JsonValueKind.String) throw new ProtocolFormatException("Expected string: " + name);
        return value.GetString()!;
    }

    private static Guid Uuid(JsonElement root, string name)
    {
        var value = String(root, name);
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new ProtocolFormatException("Expected nonzero canonical UUID: " + name);
        return id;
    }

    private static long Decimal(JsonElement root, string name)
    {
        var value = String(root, name);
        if (value.Length is < 1 or > 19 || value[0] == '0' || value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new ProtocolFormatException("Expected positive canonical int64 decimal string: " + name);
        return number;
    }

    private static int Integer(JsonElement root, string name)
    {
        var value = Property(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0) throw new ProtocolFormatException("Expected nonnegative int32: " + name);
        return number;
    }
}
