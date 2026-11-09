using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed class TakeoutImportedMetadata
{
    public TakeoutVerifiedOriginal Original{get;}
    public TakeoutCapturedEntry Sidecar{get;}
    internal TakeoutImportedMetadata(TakeoutVerifiedOriginal original,TakeoutCapturedEntry sidecar)
    {Original=original;Sidecar=sidecar;}
}

public sealed partial class TakeoutMetadataJobRepository
{
    public TakeoutMetadataWork? ClaimImported(Guid importId,string sidecarKey)
    {
        if(importId==Guid.Empty||!IsHash(sidecarKey))throw new ArgumentException("An exact captured sidecar identity is required.");
        var work=Claim(ImportedJobId(importId,sidecarKey));
        if(work is not null&&(work.Imported is null||work.Imported.Original.Capture.Inventory.ImportId!=importId||work.Imported.Sidecar.Key!=sidecarKey))
            throw new InvalidDataException("Claimed metadata changed its first imported provenance.");
        return work;
    }
    /// <summary>One optional analysis per exact indexed sidecar and verified copy.
    /// Canonical photo identity and logical input provenance remain separate.
    /// This local admission is not current provider/publication permission.</summary>
    public Guid EnsureImported(TakeoutVerifiedOriginal original,TakeoutMetadataAssociation association,ArtifactDescriptor sidecar)
    {
        ArgumentNullException.ThrowIfNull(original);ArgumentNullException.ThrowIfNull(association);ArgumentNullException.ThrowIfNull(sidecar);
        if(!ReferenceEquals(original.Owner,_database))throw new InvalidOperationException("Imported metadata belongs to a different native state owner.");
        association.AssertSelection(original.Capture.Inventory);
        if(association.Status!="matched"||association.Media!=original.Capture.Entry||association.Sidecar.Kind!=TakeoutEntryKind.JsonSidecar||
            association.Bytes!=association.Sidecar.Bytes||sidecar.Bytes!=association.Bytes||sidecar.Sha256!=association.Sha256)
            throw new InvalidDataException("Metadata must bind its unique native media match and exact raw source.");
        var current=new TakeoutOriginalJobRepository(_database).ReadVerified(original.Capture.Inventory.ImportId,original.Capture.Entry.Key)
            ??throw new InvalidOperationException("Finish independent original verification before associating its metadata.");
        if(current.AssetId!=original.AssetId||current.FileId!=original.FileId||current.Version!=original.Version||current.Capture.Sha256!=original.Capture.Sha256)
            throw new InvalidDataException("Metadata changed its first verified original representation.");
        var summary=_jobs.Summary(original.Capture.Inventory.ImportId);if(summary.Paused||summary.IsCanceled)throw new StaleStageLeaseException();
        var id=ImportedJobId(original.Capture.Inventory.ImportId,association.Sidecar.Key);
        EnsureCaptured(id,id,current.AssetId,current.Capture.Sha256,sidecar,ImportedContext(current,association.Sidecar));return id;
    }
    private byte[] ImportedContext(TakeoutVerifiedOriginal original,TakeoutCapturedEntry sidecar)=>_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {
        format="mpa-takeout-import-metadata-job-1",importId=original.Capture.Inventory.ImportId.ToString("D"),originalEntryKey=original.Capture.Entry.Key,
        fileId=original.FileId,version=original.Version.ToString(CultureInfo.InvariantCulture),logicalMediaId=original.Capture.Entry.MediaId.ToString("D"),
        canonicalAssetId=original.AssetId.ToString("D"),originalSha256=original.Capture.Sha256,
        indexSha256=original.Capture.Inventory.Result.Sha256,selectionSha256=Hash(Encoding.UTF8.GetBytes(original.Capture.Inventory.CapturedContext)),
        sidecar=new{key=sidecar.Key,partId=sidecar.PartId.ToString("D"),relativePath=sidecar.RelativePath,bytes=sidecar.Bytes.ToString(CultureInfo.InvariantCulture),
            ordinal=sidecar.Ordinal,sourceStamp=sidecar.SourceStamp.ToString(CultureInfo.InvariantCulture),mediaId=sidecar.MediaId.ToString("D")}
    }));
    private TakeoutImportedMetadata DecodeImported(Guid jobId,Guid itemId,string jobContext,Guid mediaId,string mediaHash,ArtifactDescriptor descriptor)
    {
        try
        {
            using var document=JsonDocument.Parse(jobContext);var root=document.RootElement;
            if(root.GetProperty("format").GetString()!="mpa-takeout-import-metadata-job-1")throw new InvalidDataException("Unknown captured metadata job contract.");
            var import=root.GetProperty("importId").GetGuid();var entry=root.GetProperty("originalEntryKey").GetString()!;
            var original=new TakeoutOriginalJobRepository(_database).ReadVerified(import,entry)??throw new InvalidDataException("Imported metadata lost its verified copy receipt.");
            var value=root.GetProperty("sidecar");var sidecar=new TakeoutCapturedEntry(value.GetProperty("partId").GetGuid(),value.GetProperty("key").GetString()!,value.GetProperty("relativePath").GetString()!,
                ImportedDecimal(value,"bytes"),TakeoutEntryKind.JsonSidecar,value.GetProperty("ordinal").GetInt32(),ImportedDecimal(value,"sourceStamp"),value.GetProperty("mediaId").GetGuid());
            _=TakeoutSelectedInput.LogicalPath(sidecar.RelativePath);
            if(sidecar.Ordinal<0||sidecar.Bytes!=descriptor.Bytes||sidecar.Kind!=TakeoutEntryKind.JsonSidecar||original.Capture.Inventory.Parts.All(part=>part.PartId!=sidecar.PartId)||
                jobId!=ImportedJobId(import,sidecar.Key)||itemId!=jobId||mediaId!=original.AssetId||mediaHash!=original.Capture.Sha256||
                !ImportedContext(original,sidecar).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(jobContext)))
                throw new InvalidDataException("Imported metadata changed its original, sidecar, provenance or canonical identity.");
            return new(original,sidecar);
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {throw new InvalidDataException("Imported metadata has an invalid captured binding.",error);}
    }
    private static long ImportedDecimal(JsonElement root,string name)
    {
        var text=root.GetProperty(name).GetString();if(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value<0||value.ToString(CultureInfo.InvariantCulture)!=text)
            throw new InvalidDataException("Imported metadata changed a captured decimal.");return value;
    }
    private static Guid ImportedJobId(Guid importId,string sidecarKey)
    {var bytes=SHA256.HashData(Encoding.UTF8.GetBytes("mpa-takeout-import-metadata:1\n"+importId.ToString("D")+"\n"+sidecarKey));return new(bytes.AsSpan(0,16));}
}
