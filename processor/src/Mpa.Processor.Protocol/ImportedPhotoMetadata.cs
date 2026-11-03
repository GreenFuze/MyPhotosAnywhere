using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public sealed record ImportedMetadataFingerprint(long Bytes,string Sha256);

/// <summary>Portable imported observations, separate from observed Drive fields
/// and human corrections. These values are neither original-backup receipts nor
/// face/person identities. The owning signed catalog must require Capability.</summary>
public sealed class ImportedPhotoMetadata
{
    public const string Capability="catalog.imported-metadata.v1";
    public const int MaximumEncodedBytes=64*1024;
    public Guid AssetId{get;}
    public Guid ImportId{get;}
    public Guid JobId{get;}
    public string OriginalSha256{get;}
    public long OriginalVersion{get;}
    public string SidecarKey{get;}
    public ImportedMetadataFingerprint Source{get;}
    public ImportedMetadataFingerprint Artifact{get;}
    public string Title{get;}
    public string Description{get;}
    public long? CapturedUtc{get;}
    public double? Latitude{get;}
    public double? Longitude{get;}
    public ReadOnlyCollection<string> ImportedNames{get;}
    private ImportedPhotoMetadata(Guid asset,Guid import,Guid job,string original,long originalVersion,string sidecar,ImportedMetadataFingerprint source,ImportedMetadataFingerprint artifact,
        string title,string description,long? captured,double? latitude,double? longitude,IEnumerable<string> names)
    {
        AssetId=asset;ImportId=import;JobId=job;OriginalSha256=original;OriginalVersion=originalVersion;SidecarKey=sidecar;Source=source;Artifact=artifact;
        Title=title;Description=description;CapturedUtc=captured;Latitude=latitude;Longitude=longitude;
        ImportedNames=Array.AsReadOnly(names.Take(101).ToArray());Validate();
    }
    /// <summary>The native owner must first verify the actual ready receipt,
    /// retained raw input and artifact. Constructing a DTO grants no authority.</summary>
    public static ImportedPhotoMetadata FromTakeout(Guid asset,Guid import,Guid job,string original,long originalVersion,string sidecar,
        ArtifactDescriptor source,ArtifactDescriptor artifact,TakeoutMetadataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(artifact);
        var encoded=document.EncodeArtifact();
        if(source.Sha256!=document.SourceSha256||source.Bytes!=document.CopyRawSource().LongLength||
            artifact.Bytes!=encoded.LongLength||artifact.Sha256!=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(encoded)))
            throw new ProtocolFormatException("Imported photo observations changed their exact source or analysis artifact.");
        return new(asset,import,job,original,originalVersion,sidecar,new(source.Bytes,source.Sha256),new(artifact.Bytes,artifact.Sha256),document.Title,document.Description,document.CapturedUtc,document.Latitude,document.Longitude,document.ImportedNames);
    }
    public byte[] Encode()
    {
        Validate();var bytes=new CanonicalJson(4*TakeoutMetadataDocument.MaximumSourceBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-imported-photo-metadata",major=1,assetId=AssetId.ToString("D"),importId=ImportId.ToString("D"),jobId=JobId.ToString("D"),
            originalSha256=OriginalSha256,originalVersion=OriginalVersion.ToString(CultureInfo.InvariantCulture),sidecarKey=SidecarKey,sourceBytes=Source.Bytes.ToString(CultureInfo.InvariantCulture),sourceSha256=Source.Sha256,
            artifactBytes=Artifact.Bytes.ToString(CultureInfo.InvariantCulture),artifactSha256=Artifact.Sha256,recipeSha256=TakeoutMetadataDocument.RecipeSha256,
            title=Title,description=Description,capturedUtc=CapturedUtc?.ToString(CultureInfo.InvariantCulture),
            latitude=Coordinate(Latitude),longitude=Coordinate(Longitude),importedNames=ImportedNames
        }));
        if(bytes.Length>MaximumEncodedBytes)throw new CatalogChunkCapacityException();return bytes;
    }
    public static ImportedPhotoMetadata Read(ReadOnlySpan<byte> canonical)
    {
        if(canonical.Length is <1 or >MaximumEncodedBytes||!new CanonicalJson(MaximumEncodedBytes).Encode(canonical).AsSpan().SequenceEqual(canonical))
            throw new ProtocolFormatException("Imported photo observations require bounded canonical bytes.");
        try
        {
            using var document=JsonDocument.Parse(canonical.ToArray());var root=document.RootElement;
            if(Text(root,"format")!="mpa-imported-photo-metadata"||root.GetProperty("major").GetInt32()!=1||Text(root,"recipeSha256")!=TakeoutMetadataDocument.RecipeSha256)
                throw new ProtocolCompatibilityException("Imported photo observations require a supported metadata recipe/profile.");
            var names=root.GetProperty("importedNames");if(names.ValueKind!=JsonValueKind.Array||names.GetArrayLength()>100)throw new ProtocolFormatException("Imported name hints exceed their supported bound.");
            var result=new ImportedPhotoMetadata(Id(root,"assetId"),Id(root,"importId"),Id(root,"jobId"),Text(root,"originalSha256"),Decimal(root,"originalVersion"),Text(root,"sidecarKey"),
                new(Decimal(root,"sourceBytes"),Text(root,"sourceSha256")),new(Decimal(root,"artifactBytes"),Text(root,"artifactSha256")),
                Text(root,"title"),Text(root,"description"),Time(root,"capturedUtc"),ReadCoordinate(root,"latitude"),ReadCoordinate(root,"longitude"),names.EnumerateArray().Select(value=>value.ValueKind==JsonValueKind.String?value.GetString()!:throw new ProtocolFormatException("Imported name hint must be text.")));
            if(!result.Encode().AsSpan().SequenceEqual(canonical))throw new ProtocolFormatException("Imported photo observations changed their closed schema or canonical values.");return result;
        }
        catch(Exception error)when(error is not(ProtocolCompatibilityException or ProtocolFormatException or CatalogChunkCapacityException)&&error is (JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException))
        {throw new ProtocolFormatException("Imported photo observations have invalid fields.",error);}
    }
    private void Validate()
    {
        if(AssetId==Guid.Empty||ImportId==Guid.Empty||JobId==Guid.Empty||!Hash(OriginalSha256)||OriginalVersion<1||!Hash(SidecarKey)||
            Source is null||Source.Bytes is <1 or >TakeoutMetadataDocument.MaximumSourceBytes||!Hash(Source.Sha256)||Artifact is null||Artifact.Bytes is <1 or >TakeoutMetadataDocument.MaximumArtifactBytes||!Hash(Artifact.Sha256)||
            string.IsNullOrWhiteSpace(Title)||Title.Length>1024||Title.Contains('/')||Title.Contains('\\')||Title.Contains('\0')||Description is null||
            CapturedUtc is < -62135596800000L or >253402300799999L||(Latitude is null)!=(Longitude is null)||Latitude is {}lat&&(!double.IsFinite(lat)||lat is < -90 or >90)||Longitude is {}lon&&(!double.IsFinite(lon)||lon is < -180 or >180)||
            ImportedNames.Count>100||ImportedNames.Any(name=>name is null||name.Length>200))throw new ProtocolFormatException("Invalid imported photo observations or provenance.");
        try
        {
            var utf8=new UTF8Encoding(false,true);_=utf8.GetByteCount(Title);if(utf8.GetByteCount(Description)>CanonicalJson.MaximumStringBytes)throw new CatalogChunkCapacityException();
            foreach(var name in ImportedNames)_=utf8.GetByteCount(name);
        }catch(EncoderFallbackException error){throw new ProtocolFormatException("Imported metadata contains invalid Unicode.",error);}
    }
    private static bool Hash(string value)=>value is not null&&value.Length==64&&value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    private static string? Coordinate(double? value)=>value?.ToString("R",CultureInfo.InvariantCulture);
    private static string Text(JsonElement root,string name)=>root.GetProperty(name).ValueKind==JsonValueKind.String?root.GetProperty(name).GetString()!:throw new ProtocolFormatException("Imported metadata field must be text: "+name);
    private static Guid Id(JsonElement root,string name)
    {var text=Text(root,name);return Guid.TryParseExact(text,"D",out var id)&&id!=Guid.Empty&&id.ToString("D")==text?id:throw new ProtocolFormatException("Invalid imported metadata identity.");}
    private static long Decimal(JsonElement root,string name)
    {var text=Text(root,name);return long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value.ToString(CultureInfo.InvariantCulture)==text?value:throw new ProtocolFormatException("Invalid imported metadata exact decimal.");}
    private static long? Time(JsonElement root,string name)
    {if(root.GetProperty(name).ValueKind==JsonValueKind.Null)return null;var text=Text(root,name);return long.TryParse(text,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var value)&&value.ToString(CultureInfo.InvariantCulture)==text?value:throw new ProtocolFormatException("Invalid imported metadata timestamp.");}
    private static double? ReadCoordinate(JsonElement root,string name)
    {if(root.GetProperty(name).ValueKind==JsonValueKind.Null)return null;var text=Text(root,name);return double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&Coordinate(value)==text?value:throw new ProtocolFormatException("Invalid imported metadata coordinate.");}
}
