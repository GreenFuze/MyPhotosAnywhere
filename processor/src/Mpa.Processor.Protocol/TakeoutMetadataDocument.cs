using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public sealed class TakeoutMetadataIssueException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Bounded opaque metadata artifact contract, separate from signed
/// protocol JSON. Original bytes remain provenance; imported names are not person
/// identities and no field authorizes mutation of human metadata or originals.</summary>
public sealed class TakeoutMetadataDocument
{
    public const int MaximumSourceBytes = 128 * 1024;
    public const int MaximumArtifactBytes = 1024 * 1024;
    public static string RecipeSha256 { get; } = Convert.ToHexStringLower(SHA256.HashData("mpa.takeout-metadata:1:bounded-original-sidecar"u8));
    private static readonly string[] MediaMarkers = ["photoTakenTime", "creationTime", "geoData", "geoDataExif", "googlePhotosOrigin"];
    private readonly byte[] _raw;
    public string SourceSha256 { get; }
    public string Title { get; }
    public string Description { get; }
    public long? CapturedUtc { get; }
    public double? Latitude { get; }
    public double? Longitude { get; }
    public IReadOnlyList<string> ImportedNames { get; }
    private TakeoutMetadataDocument(byte[] raw,string title,string description,long? capturedUtc,double? latitude,double? longitude,List<string> importedNames)
    {
        _raw=raw;Title=title;Description=description;CapturedUtc=capturedUtc;Latitude=latitude;Longitude=longitude;
        ImportedNames=Array.AsReadOnly(importedNames.ToArray());SourceSha256=Convert.ToHexStringLower(SHA256.HashData(raw));
    }
    public byte[] CopyRawSource() => _raw.ToArray();
    public static TakeoutMetadataDocument ParseSource(ReadOnlyMemory<byte> source)
    {
        if(source.Length is <1 or >MaximumSourceBytes) throw Issue("metadata-too-large","Takeout sidecar exceeds its bounded metadata stage.");
        var raw=source.ToArray();
        try
        {
            using var parsed=JsonDocument.Parse(raw,new JsonDocumentOptions {MaxDepth=32});
        var root = parsed.RootElement;
        ValidateUniqueFields(root);
        if (root.ValueKind != JsonValueKind.Object || !MediaMarkers.Any(name => root.TryGetProperty(name, out _)))
            throw Issue("unsupported-metadata", "Input is not a Google Photos media sidecar.");
        var title = Text(root, "title", true)!;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 1024 || title.Contains('/') || title.Contains((char)92) || title.Contains((char)0))
            throw Issue("invalid-metadata", "Takeout title is not a bounded media filename.");
        var description = Text(root, "description", false) ?? "";
        long? capturedUtc = null;
        if (root.TryGetProperty("photoTakenTime", out var taken) && taken.ValueKind != JsonValueKind.Null)
        {
            var timestamp = Text(taken, "timestamp", true)!;
            if (!long.TryParse(timestamp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) || seconds.ToString(CultureInfo.InvariantCulture) != timestamp)
                throw Issue("invalid-metadata", "Takeout capture timestamp is not canonical integer seconds.");
            try { capturedUtc = DateTimeOffset.FromUnixTimeSeconds(seconds).ToUnixTimeMilliseconds(); }
            catch (ArgumentOutOfRangeException) { throw Issue("invalid-metadata", "Takeout capture timestamp is outside the supported range."); }
        }
        double? latitude = null, longitude = null;
        if ((root.TryGetProperty("geoData", out var geo) && geo.ValueKind != JsonValueKind.Null) ||
            (root.TryGetProperty("geoDataExif", out geo) && geo.ValueKind != JsonValueKind.Null))
        {
            if (geo.ValueKind != JsonValueKind.Object) throw Issue("invalid-metadata", "Takeout location is not an object.");
            latitude = Number(geo, "latitude"); longitude = Number(geo, "longitude");
            if ((latitude is null) != (longitude is null) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
                throw Issue("invalid-metadata", "Takeout location is incomplete or outside geographic bounds.");
        }
        var importedNames = new List<string>();
        if (root.TryGetProperty("people", out var people) && people.ValueKind != JsonValueKind.Null)
        {
            if (people.ValueKind != JsonValueKind.Array || people.GetArrayLength() > 100) throw Issue("invalid-metadata", "Takeout people list exceeds its bounded metadata contract.");
            foreach (var person in people.EnumerateArray())
            {
                var name = Text(person, "name", false);
                if (name is null) continue;
                if (name.Length > 200) throw Issue("invalid-metadata", "Takeout imported name is too long.");
                importedNames.Add(name);
            }
        }

        return new(raw,title,description,capturedUtc,latitude,longitude,importedNames);
        }
        catch(JsonException) {throw Issue("invalid-metadata","Takeout sidecar is malformed or exceeds nesting limits.");}
        catch(InvalidOperationException) {throw Issue("invalid-metadata","Takeout sidecar contains invalid text or field encoding.");}
    }
    public byte[] EncodeArtifact()
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-takeout-metadata",major=1,sourceBytes=_raw.Length.ToString(CultureInfo.InvariantCulture),sourceSha256=SourceSha256,recipeSha256=RecipeSha256,
            title=Title,description=Description,capturedUtc=CapturedUtc,latitude=Latitude,longitude=Longitude,importedNames=ImportedNames,
            rawSidecarBase64=Convert.ToBase64String(_raw)
        });
        if(bytes.Length>MaximumArtifactBytes) throw Issue("metadata-too-large","Takeout staged result exceeds its bounded artifact budget.");
        return bytes;
    }
    /// <summary>Called only after independently verifying the exact staged file's
    /// descriptor. Recompute facts from the original sidecar, rejecting substituted
    /// provenance, fields, recipes, duplicate/unknown artifact properties or aliases.</summary>
    public static TakeoutMetadataDocument ReadVerifiedArtifact(ReadOnlyMemory<byte> artifact,ArtifactDescriptor original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if(artifact.Length is <1 or >MaximumArtifactBytes || original.Bytes>MaximumSourceBytes)
            throw new ProtocolFormatException("Takeout artifact exceeds its fixed contract.");
        try
        {
            using var parsed=JsonDocument.Parse(artifact,new JsonDocumentOptions {MaxDepth=32});
            if(parsed.RootElement.ValueKind!=JsonValueKind.Object || !parsed.RootElement.TryGetProperty("rawSidecarBase64",out var rawField) || rawField.ValueKind!=JsonValueKind.String)
                throw new ProtocolFormatException("Takeout artifact lost original provenance.");
            var encoded=rawField.GetString()!;
            if(encoded.Length>((MaximumSourceBytes+2)/3)*4) throw new ProtocolFormatException("Takeout artifact provenance exceeds its bound.");
            var raw=Convert.FromBase64String(encoded);
            if(raw.LongLength!=original.Bytes || Convert.ToBase64String(raw)!=encoded || Convert.ToHexStringLower(SHA256.HashData(raw))!=original.Sha256)
                throw new ProtocolFormatException("Takeout artifact substituted its original source.");
            var document=ParseSource(raw);
            if(!document.EncodeArtifact().AsSpan().SequenceEqual(artifact.Span))
                throw new ProtocolFormatException("Takeout artifact facts differ from the original sidecar and fixed recipe.");
            return document;
        }
        catch(Exception error) when(error is JsonException or FormatException or InvalidOperationException or TakeoutMetadataIssueException)
        {throw new ProtocolFormatException("Invalid Takeout staged artifact.",error);}
    }
    private static TakeoutMetadataIssueException Issue(string code, string message) => new(code, message);
    private static string? Text(JsonElement value, string name, bool required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Issue("invalid-metadata", "Takeout field owner is not an object.");
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
        {
            if (required) throw Issue("invalid-metadata", "Required Takeout text is missing.");
            return null;
        }
        if (field.ValueKind != JsonValueKind.String) throw Issue("invalid-metadata", "Takeout text has a different JSON type.");
        return field.GetString();
    }
    private static double? Number(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var result) || !double.IsFinite(result))
            throw Issue("invalid-metadata", "Takeout coordinate is not a finite JSON number.");
        return result;
    }
    private static void ValidateUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Issue("invalid-metadata", "Takeout metadata contains duplicate JSON fields.");
                ValidateUniqueFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUniqueFields(item);
    }
}
