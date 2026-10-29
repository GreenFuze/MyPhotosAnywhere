using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>Public connection information, NEVER enrollment or incoming authority.
/// The local setup owner must obtain independent human approval of this exact value,
/// bind the actual Google account and persist the pin before verifying Drive policies.
/// Contains no network endpoint, private key, Google credential or original-write grant.</summary>
public sealed class LibraryConnectionDescriptor
{
    public const string Prefix = "mpa-library-v1:";
    public const string CurrentPrefix = "mpa-library-v2:";
    public const int MaximumDocumentBytes = 2048;
    public const int MaximumCodeCharacters = 4096;
    private readonly byte[] _document;
    public Guid LibraryId { get; }
    public Guid ControllerId { get; }
    public long ControllerEpoch { get; }
    public CatalogWorkspace Workspace { get; }
    public string ControllerPublicKeySpki { get; }
    public string ControllerKeyId { get; }
    public string DocumentSha256 { get; }
    public string? CurrentFileId { get; }

    public LibraryConnectionDescriptor(Guid libraryId, Guid controllerId, long controllerEpoch,
        CatalogWorkspace workspace, ReadOnlySpan<byte> controllerPublicKey, string? currentFileId = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (libraryId == Guid.Empty || controllerId == Guid.Empty || controllerEpoch < 1 || workspace.FolderId == "root")
            throw new ProtocolFormatException("A concrete library, controller epoch and metadata workspace are required.");
        if (controllerPublicKey.Length is < 1 or > 512) throw new ProtocolFormatException("Invalid controller public-key bound.");
        if (currentFileId is not null)
        {
            CatalogPublicationReader.CheckOpaque(currentFileId);
            if (currentFileId == "root" || currentFileId == workspace.FolderId) throw new ProtocolFormatException("The current-control object must have its own concrete Drive identity.");
        }
        CurrentFileId = currentFileId;
        var owned = controllerPublicKey.ToArray();
        using var verifier = new ProtocolDocumentVerifier(owned);
        LibraryId = libraryId; ControllerId = controllerId; ControllerEpoch = controllerEpoch; Workspace = workspace;
        ControllerPublicKeySpki = Convert.ToBase64String(owned); ControllerKeyId = verifier.KeyId;
        var fields = new Dictionary<string, object?>
        {
            ["format"] = "mpa-library-connection", ["major"] = currentFileId is null ? 1 : 2, ["libraryId"] = libraryId.ToString("D"),
            ["controller"] = new { actorId = controllerId.ToString("D"), epoch = controllerEpoch.ToString(CultureInfo.InvariantCulture),
                publicKeySpki = ControllerPublicKeySpki, keyId = ControllerKeyId },
            ["workspace"] = new { accountId = workspace.AccountId, folderId = workspace.FolderId }
        };
        if (currentFileId is not null) fields.Add("currentFileId", currentFileId);
        _document = new CanonicalJson(MaximumDocumentBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(fields));
        DocumentSha256 = Convert.ToHexStringLower(SHA256.HashData(_document));
    }

    public string Encode() => (CurrentFileId is null ? Prefix : CurrentPrefix) + Convert.ToBase64String(_document).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static LibraryConnectionDescriptor Decode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var prefix = code.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix : code.StartsWith(CurrentPrefix, StringComparison.Ordinal) ? CurrentPrefix : null;
        if (code.Length > MaximumCodeCharacters || prefix is null)
            throw new ProtocolFormatException("Invalid or unsupported MPA library connection code.");
        var encoded = code[prefix.Length..];
        if (encoded.Length == 0 || encoded.Length % 4 == 1 ||
            encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ProtocolFormatException("Library connection code requires canonical unpadded base64url.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4)); }
        catch (FormatException error) { throw new ProtocolFormatException("Invalid library connection encoding.", error); }
        if (bytes.Length is < 1 or > MaximumDocumentBytes) throw new ProtocolFormatException("Library connection exceeds its byte bound.");
        var canonical = new CanonicalJson(MaximumDocumentBytes).Encode(bytes);
        if (!bytes.AsSpan().SequenceEqual(canonical)) throw new ProtocolFormatException("Library connection JSON must already be canonical.");
        using var document = JsonDocument.Parse(canonical); var root = document.RootElement;
        var current = prefix == CurrentPrefix;
        Object(root, current ? 6 : 5);
        if (Text(root, "format") != "mpa-library-connection" || !root.TryGetProperty("major", out var major) ||
            major.ValueKind != JsonValueKind.Number || !major.TryGetInt32(out var version) || version != (current ? 2 : 1))
            throw new ProtocolFormatException("Unsupported library connection format.");
        var controller = Property(root, "controller"); Object(controller, 4);
        var workspace = Property(root, "workspace"); Object(workspace, 2);
        var epochText = Text(controller, "epoch");
        if (epochText.Length is < 1 or > 19 || epochText[0] == '0' || epochText.Any(c => c is < '0' or > '9') ||
            !long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch < 1)
            throw new ProtocolFormatException("Invalid canonical controller epoch.");
        var spki = Text(controller, "publicKeySpki"); byte[] key;
        if (spki.Length is < 1 or > 684) throw new ProtocolFormatException("Invalid controller public-key bound.");
        try { key = Convert.FromBase64String(spki); }
        catch (FormatException error) { throw new ProtocolFormatException("Invalid controller public-key encoding.", error); }
        if (Convert.ToBase64String(key) != spki) throw new ProtocolFormatException("Non-canonical controller public key.");
        var result = new LibraryConnectionDescriptor(Uuid(root, "libraryId"), Uuid(controller, "actorId"), epoch,
            new(Text(workspace, "accountId"), Text(workspace, "folderId")), key, current ? Text(root, "currentFileId") : null);
        if (Text(controller, "keyId") != result.ControllerKeyId || result.Encode() != code)
            throw new ProtocolFormatException("Connection key identity, fields or wire encoding changed.");
        return result;
    }

    private static void Object(JsonElement root, int count)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != count)
            throw new ProtocolFormatException("Unexpected library connection fields.");
    }
    private static JsonElement Property(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        ? value : throw new ProtocolFormatException("Missing library connection field: " + name);
    private static string Text(JsonElement root, string name)
    {
        var value = Property(root, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ProtocolFormatException("Connection field must be text: " + name);
    }
    private static Guid Uuid(JsonElement root, string name)
    {
        var text = Text(root, name);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.ToString("D") != text)
            throw new ProtocolFormatException("Invalid canonical library connection identity.");
        return id;
    }
}
