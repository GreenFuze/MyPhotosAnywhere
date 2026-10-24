using System.Globalization;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Pure bounded journal decoding shared by writable discovery and read-only snapshots.</summary>
internal sealed class SourceDiscoveryJournalCodec
{
    private readonly CanonicalJson _codec = new(32768);
    public SourceDiscoveryCheckpoint Checkpoint(string? value)
    {
        if (value is null) return new(null, 0, 0, false, false);
        using var json = Object(value, 5, "mpa-source-checkpoint-1"); var root = json.RootElement;
        var token = root.GetProperty("nextPageToken"); var next = token.ValueKind == JsonValueKind.Null ? null : token.GetString();
        if (next is not null && (next.Length is < 1 or > 4096 || next.Any(char.IsControl))) throw new InvalidDataException("Invalid saved source page token.");
        return new(next, Decimal(root, "observedItems", zero: true), Decimal(root, "failedItems", zero: true), root.GetProperty("incompleteSearch").GetBoolean(), true);
    }
    public SourceDiscoveryScope Scope(string value)
    {
        using var json = Object(value, 7, "mpa-source-discovery-1"); var root = json.RootElement;
        var raw = root.GetProperty("sourceId").GetString();
        if (!Guid.TryParseExact(raw, "D", out var id) || id == Guid.Empty || id.ToString("D") != raw) throw new InvalidDataException("Invalid captured source identity.");
        var account = root.GetProperty("accountId").GetString(); var folder = root.GetProperty("rootFolderId").GetString();
        if (!Identity(account) || !Identity(folder)) throw new InvalidDataException("Invalid captured Drive identity.");
        return new(id, Decimal(root, "sourceRevision", false), account!, Decimal(root, "accountRevision", false), folder!, root.GetProperty("includeDescendants").GetBoolean());
    }
    public SourceDiscoveryFolder Folder(string value)
    {
        using var json = Object(value, 4, "mpa-source-folder-1"); var root = json.RootElement;
        var folder = root.GetProperty("folderId").GetString(); var shared = root.GetProperty("sharedDriveId").GetString(); var depth = root.GetProperty("depth").GetInt32();
        if (!Identity(folder) || shared is not null && !Identity(shared) || depth is < 0 or > 256) throw new InvalidDataException("Invalid captured discovery folder.");
        return new(folder!, shared, depth);
    }
    public JsonDocument Object(string value, int count, string format)
    {
        if (Encoding.UTF8.GetByteCount(value) > 32768) throw new InvalidDataException("Captured discovery context exceeds its byte budget.");
        var canonical = _codec.Encode(Encoding.UTF8.GetBytes(value)); var json = JsonDocument.Parse(canonical);
        try
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != count || json.RootElement.GetProperty("format").GetString() != format)
                throw new InvalidDataException("Invalid captured discovery context.");
            return json;
        }
        catch { json.Dispose(); throw; }
    }
    public static long Decimal(JsonElement root, string name, bool zero)
    {
        var raw = root.GetProperty(name).GetString();
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < (zero ? 0 : 1) || number.ToString(CultureInfo.InvariantCulture) != raw)
            throw new InvalidDataException("Invalid captured discovery counter/revision.");
        return number;
    }
    public static bool Identity(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
