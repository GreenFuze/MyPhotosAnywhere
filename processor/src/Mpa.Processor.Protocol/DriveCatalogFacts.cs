using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public sealed record DriveCatalogSource(Guid Id, long Revision, int Depth);
public sealed record DriveCatalogAsset(Guid Id, string AccountId, string FileId, long DriveVersion, string Name, string MimeType,
    int? Width, int? Height, long? CapturedUtc, string? RawCapture, long? ModifiedUtc, ReadOnlyCollection<DriveCatalogSource> Sources)
{
    public long? EffectiveUtc => CapturedUtc ?? ModifiedUtc;
}
public sealed record DriveCatalogChunk(string Prefix, ReadOnlyCollection<DriveCatalogAsset> Assets);
public sealed class CatalogChunkCapacityException() : InvalidOperationException("This identity partition exceeds a catalog chunk bound. Split it before publication; never truncate its records.");

/// <summary>Observed Drive facts only, never proof of downloaded/verified original bytes.
/// A consumer must additionally validate signed authority, source coverage/revisions,
/// workspace membership and complete generation dependencies before activating these rows.</summary>
public sealed class DriveCatalogFacts
{
    public const string Capability = "catalog.drive-facts.v1";
    public const int MaximumAssets = 1000;
    public const int MaximumSources = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Guid _library;
    private readonly DriveAssetIdentity _identity;
    public DriveCatalogFacts(Guid library)
    { _identity = new(library); _library = library; }

    public DriveCatalogChunk Read(ReadOnlySpan<byte> canonicalBytes, string expectedPrefix)
    {
        CheckPrefix(expectedPrefix);
        var canonical = new CanonicalJson().Encode(canonicalBytes);
        if (!canonicalBytes.SequenceEqual(canonical)) throw new ProtocolFormatException("Drive facts must already be canonical JSON.");
        using var document = JsonDocument.Parse(canonical); var root = document.RootElement;
        if (Text(root, "format") != "mpa-drive-facts" || Integer(root, "major") != 1)
            throw new ProtocolCompatibilityException("These Drive facts require a compatible MPA update.");
        if (Uuid(root, "libraryId") != _library || Text(root, "prefix") != expectedPrefix)
            throw new ProtocolFormatException("Drive facts do not match their captured library/identity partition.");
        var rows = Property(root, "assets");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > MaximumAssets)
            throw new ProtocolFormatException("Drive facts require a bounded nonempty identity partition.");
        var assets = new List<DriveCatalogAsset>(); string? previous = null;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Uuid(row, "id"); var sources = Property(row, "sources");
            if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() is < 1 or > MaximumSources)
                throw new ProtocolFormatException("Drive facts require bounded captured source memberships.");
            var memberships = new List<DriveCatalogSource>(); string? priorSource = null;
            foreach (var source in sources.EnumerateArray())
            {
                var sourceId = Uuid(source, "id"); var text = sourceId.ToString("D");
                if (priorSource is not null && StringComparer.Ordinal.Compare(priorSource, text) >= 0)
                    throw new ProtocolFormatException("Source memberships must be unique and ordinally sorted.");
                priorSource = text;
                memberships.Add(new(sourceId, Positive(source, "revision"), Integer(source, "depth")));
            }
            var asset = new DriveCatalogAsset(id, Text(row, "accountId"), Text(row, "fileId"), Positive(row, "driveVersion"), Text(row, "name"), Text(row, "mimeType"),
                OptionalInteger(row, "width"), OptionalInteger(row, "height"), Timestamp(row, "capturedUtc"), OptionalText(row, "rawCapture"), Timestamp(row, "modifiedUtc"), memberships.AsReadOnly());
            Validate(asset, expectedPrefix);
            var key = id.ToString("D");
            if (previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0)
                throw new ProtocolFormatException("Partition asset identities must be unique and ordinally sorted.");
            previous = key; assets.Add(asset);
        }
        return new(expectedPrefix, assets.AsReadOnly());
    }

    public byte[] Encode(string prefix, IEnumerable<DriveCatalogAsset> source)
    {
        ArgumentNullException.ThrowIfNull(source); CheckPrefix(prefix);
        var assets = source.Take(MaximumAssets + 1).OrderBy(asset => asset.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        if (assets.Length > MaximumAssets) throw new CatalogChunkCapacityException();
        if (assets.Length == 0) throw new ProtocolFormatException("An empty generation uses an empty root index, not an empty facts chunk.");
        var tail = Encoding.UTF8.GetBytes("],\"format\":\"mpa-drive-facts\",\"libraryId\":\"" + _library.ToString("D") + "\",\"major\":1,\"prefix\":\"" + prefix + "\"}");
        using var output = new MemoryStream(4096); output.Write("{\"assets\":["u8); Guid? previous = null; var comma = false;
        foreach (var asset in assets)
        {
            Validate(asset, prefix);
            if (previous == asset.Id) throw new ProtocolFormatException("A partition cannot publish duplicate identities.");
            previous = asset.Id;
            var sources = asset.Sources.OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal).ToArray();
            if (sources.Select(item => item.Id).Distinct().Count() != sources.Length) throw new ProtocolFormatException("A record cannot duplicate its source membership.");
            // At most one bounded record is serialized before checking remaining chunk
            // space. No full-library JSON or over-limit chunk buffer is materialized.
            var bytes = new CanonicalJson(64 * 1024).Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = asset.Id.ToString("D"), accountId = asset.AccountId, fileId = asset.FileId,
                driveVersion = asset.DriveVersion.ToString(CultureInfo.InvariantCulture), name = asset.Name, mimeType = asset.MimeType,
                width = asset.Width, height = asset.Height, capturedUtc = asset.CapturedUtc?.ToString(CultureInfo.InvariantCulture),
                rawCapture = asset.RawCapture, modifiedUtc = asset.ModifiedUtc?.ToString(CultureInfo.InvariantCulture),
                sources = sources.Select(item => new { id = item.Id.ToString("D"), revision = item.Revision.ToString(CultureInfo.InvariantCulture), depth = item.Depth })
            }));
            var required = output.Length + (comma ? 1 : 0) + bytes.Length + tail.Length;
            if (required > CatalogChunkDecoder.MaximumExpandedBytes) throw new CatalogChunkCapacityException();
            if (required > output.Capacity) output.Capacity = (int)Math.Min(CatalogChunkDecoder.MaximumExpandedBytes, Math.Max(required, output.Capacity * 2L));
            if (comma) output.WriteByte((byte)','); output.Write(bytes); comma = true;
        }
        output.Write(tail); return output.ToArray();
    }

    private void Validate(DriveCatalogAsset asset, string prefix)
    {
        ArgumentNullException.ThrowIfNull(asset);
        CatalogPublicationReader.CheckOpaque(asset.AccountId); CatalogPublicationReader.CheckOpaque(asset.FileId);
        if (asset.Id == Guid.Empty || _identity.Derive(asset.AccountId, asset.FileId) != asset.Id || !asset.Id.ToString("N").StartsWith(prefix, StringComparison.Ordinal) ||
            asset.DriveVersion < 1 || string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 4096 || asset.Name.Any(char.IsControl) ||
            asset.MimeType is null || asset.MimeType.Length > 256 || asset.MimeType.Any(char.IsControl) ||
            !(asset.MimeType.StartsWith("image/", StringComparison.Ordinal) || asset.MimeType.StartsWith("video/", StringComparison.Ordinal)) ||
            asset.Width is <= 0 or > 1000000 || asset.Height is <= 0 or > 1000000 || !Time(asset.CapturedUtc) || !Time(asset.ModifiedUtc) || asset.RawCapture is { Length: > 128 } ||
            asset.Sources is null || asset.Sources.Count is < 1 or > MaximumSources || asset.Sources.Any(item => item.Id == Guid.Empty || item.Revision < 1 || item.Depth is < 0 or > 256))
            throw new ProtocolFormatException("Invalid observed Drive facts or captured source membership.");
        try
        {
            _ = StrictUtf8.GetByteCount(asset.Name); _ = StrictUtf8.GetByteCount(asset.MimeType);
            if (asset.RawCapture is not null) _ = StrictUtf8.GetByteCount(asset.RawCapture);
        }
        catch (EncoderFallbackException error) { throw new ProtocolFormatException("Observed Drive text contains an invalid Unicode scalar sequence.", error); }
    }
    private static bool Time(long? value) => value is null or (>= -62135596800000L and <= 253402300799999L);
    private static void CheckPrefix(string prefix)
    {
        if (prefix is null || prefix.Length is < 2 or > 8 || prefix.Length % 2 != 0 || prefix.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolFormatException("Drive facts require a complete two-digit identity partition path.");
    }
    private static JsonElement Property(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        ? value : throw new ProtocolFormatException("Missing Drive facts field: " + name);
    private static string Text(JsonElement root, string name)
    {
        var value = Property(root, name); return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ProtocolFormatException("Drive facts field must be a string: " + name);
    }
    private static string? OptionalText(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.Null ? null : Text(root, name);
    private static int Integer(JsonElement root, string name)
    {
        var value = Property(root, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : throw new ProtocolFormatException("Drive facts field must be a nonnegative int32: " + name);
    }
    private static int? OptionalInteger(JsonElement root, string name) => Property(root, name).ValueKind == JsonValueKind.Null ? null : Integer(root, name);
    private static long Positive(JsonElement root, string name)
    {
        var text = Text(root, name);
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 && value.ToString(CultureInfo.InvariantCulture) == text
            ? value : throw new ProtocolFormatException("Drive facts counter must be a positive canonical int64 string: " + name);
    }
    private static long? Timestamp(JsonElement root, string name)
    {
        if (Property(root, name).ValueKind == JsonValueKind.Null) return null;
        var text = Text(root, name);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || !Time(value) || value.ToString(CultureInfo.InvariantCulture) != text)
            throw new ProtocolFormatException("Drive facts timestamp must be a bounded canonical signed millisecond string: " + name);
        return value;
    }
    private static Guid Uuid(JsonElement root, string name)
    {
        var text = Text(root, name);
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && id.ToString("D") == text ? id : throw new ProtocolFormatException("Invalid canonical Drive facts identity: " + name);
    }
}
