using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>Previously selected workspace, not authority asserted by a received reference.</summary>
public sealed record CatalogWorkspace
{
    public string AccountId { get; }
    public string FolderId { get; }
    public CatalogWorkspace(string accountId, string folderId)
    { CatalogPublicationReader.CheckOpaque(accountId); CatalogPublicationReader.CheckOpaque(folderId); AccountId = accountId; FolderId = folderId; }
}

public sealed record CatalogObjectReference(string AccountId, string FileId, string WorkspaceId, long Bytes, string Sha256);
public sealed record CatalogChunkReference(CatalogObjectReference Encoded, long ExpandedBytes, string ExpandedSha256);
public sealed record CatalogCommit(Guid GenerationId, CatalogObjectReference? Parent, CatalogObjectReference RootIndex,
    CatalogObjectReference SourceCoverage, long SettingsRevision, long MinimumHistoryGeneration);
public sealed record CatalogIndexChild(string Edge, CatalogObjectReference Reference, CatalogChunkReference? Chunk);
public sealed record CatalogIndexNode(string Prefix, ReadOnlyCollection<CatalogIndexChild> Children);

/// <summary>Bounded portable publication schemas. Signature/approved actor verification must
/// precede ReadCommit. Actual Drive parent membership, referenced bytes/hashes, ancestry and
/// complete-tree validation remain the owning consumer's responsibility before activation.</summary>
public sealed class CatalogPublicationReader(CatalogWorkspace workspace)
{
    public const string Capability = "catalog.generations.v1";
    public const int MaximumIndexBytes = 256 * 1024;
    private readonly CatalogWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public CatalogCommit ReadCommit(VerifiedProtocolDocument verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (verified.Kind != "catalog-commit" || !verified.RequiredCapabilities.Contains(Capability, StringComparer.Ordinal))
            throw new ProtocolFormatException("A verified catalog commit with its required capability is required.");
        using var document = Canonical(Encoding.UTF8.GetBytes(verified.CanonicalPayload));
        var root = document.RootElement;
        var generation = Uuid(root, "generationId");
        var parentValue = Property(root, "parent");
        var parent = parentValue.ValueKind == JsonValueKind.Null ? null : Reference(parentValue, MaximumIndexBytes);
        if ((verified.Sequence == 1) != (parent is null)) throw new ProtocolFormatException("Only the first stream commit may omit its predecessor.");
        var index = Reference(Property(root, "rootIndex"), MaximumIndexBytes);
        var coverage = Reference(Property(root, "sourceCoverage"), MaximumIndexBytes);
        var settings = Decimal(root, "settingsRevision", allowZero: true);
        var minimum = Decimal(root, "minimumHistoryGeneration");
        if (minimum > verified.Sequence) throw new ProtocolFormatException("Required history cannot exceed the current stream sequence.");
        var ids = new HashSet<string>(StringComparer.Ordinal) { index.FileId, coverage.FileId };
        if (ids.Count != 2 || parent is not null && !ids.Add(parent.FileId))
            throw new ProtocolFormatException("Distinct catalog document roles cannot alias the same Drive object.");
        return new(generation, parent, index, coverage, settings, minimum);
    }

    public CatalogIndexNode ReadIndex(ReadOnlySpan<byte> canonicalBytes, string expectedPrefix)
    {
        CheckPrefix(expectedPrefix);
        using var document = Canonical(canonicalBytes);
        var root = document.RootElement;
        var majorValue = Property(root, "major");
        if (majorValue.ValueKind != JsonValueKind.Number || !majorValue.TryGetInt32(out var major))
            throw new ProtocolFormatException("Catalog major must be an integer.");
        if (Text(root, "format") != "mpa-catalog-index" || major != 1)
            throw new ProtocolCompatibilityException("This catalog index requires a compatible MPA update.");
        if (Text(root, "prefix") != expectedPrefix) throw new ProtocolFormatException("Catalog index does not match its parent's identity partition.");
        var children = Property(root, "children");
        if (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() > 256 || expectedPrefix.Length >= 8 ||
            children.GetArrayLength() == 0 && expectedPrefix.Length != 0)
            throw new ProtocolFormatException("Invalid bounded catalog index children.");
        var entries = new List<CatalogIndexChild>(); var ids = new HashSet<string>(StringComparer.Ordinal); string? previous = null;
        foreach (var child in children.EnumerateArray())
        {
            var edge = Text(child, "edge");
            if (edge.Length != 2 || !edge.All(IsHex) || previous is not null && StringComparer.Ordinal.Compare(previous, edge) >= 0)
                throw new ProtocolFormatException("Catalog edges must be unique sorted hexadecimal bytes.");
            previous = edge;
            var kind = Text(child, "kind");
            if (kind is not ("index" or "chunk") || kind == "index" && expectedPrefix.Length >= 6)
                throw new ProtocolFormatException("Catalog index exceeds its four identity partition levels.");
            var reference = Reference(Property(child, "object"), kind == "chunk" ? CatalogChunkDecoder.MaximumCompressedBytes : MaximumIndexBytes);
            if (!ids.Add(reference.FileId)) throw new ProtocolFormatException("Catalog children cannot share a Drive object identity.");
            CatalogChunkReference? chunk = null;
            var expanded = Property(child, "expanded");
            if (kind == "chunk")
            {
                var bytes = Decimal(expanded, "bytes"); var hash = Digest(expanded, "sha256");
                if (bytes > CatalogChunkDecoder.MaximumExpandedBytes) throw new ProtocolFormatException("Expanded catalog chunk exceeds its bound.");
                chunk = new(reference, bytes, hash);
            }
            else if (expanded.ValueKind != JsonValueKind.Null) throw new ProtocolFormatException("Only chunk objects declare expanded bytes.");
            entries.Add(new(edge, reference, chunk));
        }
        return new(expectedPrefix, entries.AsReadOnly());
    }

    private CatalogObjectReference Reference(JsonElement value, long maximum)
    {
        var account = Text(value, "accountId"); var file = Text(value, "fileId"); var folder = Text(value, "workspaceId");
        CheckOpaque(account); CheckOpaque(file); CheckOpaque(folder);
        if (account != _workspace.AccountId || folder != _workspace.FolderId || file == folder)
            throw new ProtocolFormatException("Catalog reference is outside the selected metadata workspace.");
        var bytes = Decimal(value, "bytes");
        if (bytes > maximum) throw new ProtocolFormatException("Catalog object exceeds its role's byte bound.");
        return new(account, file, folder, bytes, Digest(value, "sha256"));
    }
    private static JsonDocument Canonical(ReadOnlySpan<byte> bytes)
    {
        var encoded = new CanonicalJson(MaximumIndexBytes).Encode(bytes);
        if (!bytes.SequenceEqual(encoded)) throw new ProtocolFormatException("Catalog documents must already use canonical JSON bytes.");
        return JsonDocument.Parse(encoded);
    }
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        ? value : throw new ProtocolFormatException("Missing catalog field: " + name);
    private static string Text(JsonElement root, string name)
    {
        var value = Property(root, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ProtocolFormatException("Catalog field must be a string: " + name);
    }
    private static long Decimal(JsonElement root, string name, bool allowZero = false)
    {
        var value = Text(root, name);
        if (value.Length is < 1 or > 19 || value.Length > 1 && value[0] == '0' || value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < (allowZero ? 0 : 1))
            throw new ProtocolFormatException("Invalid canonical catalog counter: " + name);
        return result;
    }
    private static string Digest(JsonElement root, string name)
    {
        var value = Text(root, name);
        if (value.Length != 64 || !value.All(IsHex)) throw new ProtocolFormatException("Catalog digest must be lowercase SHA-256.");
        return value;
    }
    private static Guid Uuid(JsonElement root, string name)
    {
        var value = Text(root, name);
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value) throw new ProtocolFormatException("Invalid catalog generation identity.");
        return id;
    }
    internal static void CheckOpaque(string value)
    {
        if (value is null || value.Length is < 1 or > 256 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ProtocolFormatException("Catalog references require opaque Drive identities, never paths or URLs.");
    }
    private static void CheckPrefix(string prefix)
    {
        if (prefix is null || prefix.Length > 6 || prefix.Length % 2 != 0 || !prefix.All(IsHex)) throw new ProtocolFormatException("Invalid catalog index prefix.");
    }
    private static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
