using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed class TakeoutMetadataSelectionException(string message):InvalidOperationException(message);

/// <summary>Explicit local selection for an unresolved native sidecar. Contains
/// historical copy evidence only; it grants no provider or publication action.</summary>
public sealed class TakeoutManualMetadataAssociation
{
    internal TakeoutCapturedEntry Sidecar{get;}
    public string SourceSha256{get;}
    public TakeoutVerifiedOriginal Original{get;}
    internal string IssueCode{get;}
    internal TakeoutManualMetadataAssociation(TakeoutCapturedEntry sidecar,string sourceSha256,TakeoutVerifiedOriginal original,string issueCode)
    {Sidecar=sidecar;SourceSha256=sourceSha256;Original=original;IssueCode=issueCode;}
}

public sealed partial class TakeoutMetadataIssueRepository
{
    private const string AssociationPrefix="takeout-manual-metadata-v1:";
    /// <summary>Durable user intent binds the first raw hash and a verified copy
    /// from this same import. Repeated identical requests are idempotent; changing
    /// a captured binding requires a new explicit repair workflow, never retry.</summary>
    public void SelectOriginal(Guid importId,string sidecarKey,string originalKey)
    {
        if(importId==Guid.Empty||!Hash(sidecarKey)||!Hash(originalKey))throw new ArgumentException("Choose exact captured sidecar and original identities.");
        var completed=new TakeoutImportInventoryRepository(_database).ReadCompleted(importId)??throw new TakeoutMetadataSelectionException("Wait for the captured input review.");
        var saved=Read(completed,sidecarKey)??throw new TakeoutMetadataSelectionException("Choose an unresolved metadata association issue.");
        var original=new TakeoutOriginalJobRepository(_database).ReadVerified(importId,originalKey)??throw new TakeoutMetadataSelectionException("Choose an independently verified original from this import.");
        var existing=ReadAssociation(completed,sidecarKey);
        if(existing is not null)
        {
            if(existing.Original.Capture.Entry.Key!=originalKey)throw new TakeoutMetadataSelectionException("This metadata file already has a captured original selection.");
            return;
        }
        if(saved.Resolved||!CanSelect(saved.Code)||saved.SourceSha256 is null)throw new TakeoutMetadataSelectionException("Only unmatched supported metadata can receive an original selection.");
        var summary=new DurableJobRepository(_database).Summary(importId);
        if(summary.Paused||summary.IsCanceled)throw new TakeoutMetadataSelectionException("This import is paused or canceled. Its captured work was retained.");
        var selection=new TakeoutManualMetadataAssociation(saved.Entry,saved.SourceSha256,original,saved.Code);
        var body=Encoding.UTF8.GetString(EncodeAssociation(completed,selection));
        using var transaction=_database.Connection.BeginTransaction();
        using(var insert=_database.Connection.CreateCommand())
        {
            insert.Transaction=transaction;insert.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$body)";
            insert.Parameters.AddWithValue("$key",AssociationKey(importId,sidecarKey));insert.Parameters.AddWithValue("$body",body);insert.ExecuteNonQuery();
        }
        // This is local scheduling, never an observation/backup receipt. A pass
        // already in flight also compares its initial selection count at closure.
        new TakeoutImportedPhotoFactsRepository(_database).InvalidateObservationPass(importId,transaction);
        transaction.Commit();
    }
    public long AssociationCount(Guid importId)
    {
        if(importId==Guid.Empty)throw new ArgumentException("Choose a captured import.",nameof(importId));
        using var query=_database.Connection.CreateCommand();query.CommandText="SELECT count(*) FROM processor_meta WHERE key>=$start AND key<$end";
        query.Parameters.AddWithValue("$start",AssociationKey(importId,"0"));query.Parameters.AddWithValue("$end",AssociationKey(importId,"g"));return(long)query.ExecuteScalar()!;
    }
    public TakeoutManualMetadataAssociation? ReadAssociation(TakeoutCompletedInventory completed,string sidecarKey)
    {
        var actual=Actual(completed);if(!Hash(sidecarKey))throw new ArgumentException("Choose an exact captured sidecar.",nameof(sidecarKey));
        string raw;
        using(var query=_database.Connection.CreateCommand())
        {
            query.CommandText="SELECT value FROM processor_meta WHERE key=$key";query.Parameters.AddWithValue("$key",AssociationKey(actual.ImportId,sidecarKey));
            if(query.ExecuteScalar() is not string body)return null;raw=body;
        }
        try
        {
            if(raw.Length>16384)throw new InvalidDataException("An explicit metadata selection exceeded its fixed bound.");
            using var json=JsonDocument.Parse(raw);var root=json.RootElement;
            var saved=Read(actual,sidecarKey)??throw new InvalidDataException("An explicit selection lost its captured issue.");
            var issueCode=root.GetProperty("issueCode").GetString()!;
            if(!CanSelect(issueCode)||saved.SourceSha256 is null||saved.Code!=issueCode&&saved.Code!="metadata-too-large")throw new InvalidDataException("An explicit selection changed its original issue classification.");
            var original=new TakeoutOriginalJobRepository(_database).ReadVerified(actual.ImportId,root.GetProperty("originalKey").GetString()!)??throw new InvalidDataException("An explicit selection lost its verified original.");
            if(saved.Code!=issueCode)
            {
                var ready=new TakeoutMetadataJobRepository(_database).ReadCompletedImported(actual.ImportId,sidecarKey);
                if(ready is null||ready.Imported.Original.Capture.Entry!=original.Capture.Entry||ready.Source.Sha256!=saved.SourceSha256)throw new InvalidDataException("A selected metadata capacity issue lost its actual ready evidence.");
            }
            var selection=new TakeoutManualMetadataAssociation(saved.Entry,saved.SourceSha256,original,issueCode);
            if(!EncodeAssociation(actual,selection).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(raw)))throw new InvalidDataException("An explicit selection changed its first input, original or closed contract.");
            return selection;
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException or FormatException)
        {throw new InvalidDataException("An explicit metadata selection is malformed.",error);}
    }
    /// <summary>Only an actual ready result may advance a selected association
    /// issue to portable-record capacity attention. The original choice stays exact.</summary>
    public void ObserveProjectionCapacity(TakeoutVerifiedMetadata result,TakeoutMetadataDocument verified)
    {
        ArgumentNullException.ThrowIfNull(result);if(!ReferenceEquals(result.Owner,_database))throw new InvalidOperationException("Metadata result belongs to another native state owner.");
        var exceedsCapacity=false;
        try{_=result.DescribeImportedFacts(verified).Encode();}catch(CatalogChunkCapacityException){exceedsCapacity=true;}
        if(!exceedsCapacity)throw new InvalidDataException("Only verified metadata exceeding the portable bound may become a capacity issue.");
        var completed=Actual(result.Imported.Original.Capture.Inventory);var saved=Read(completed,result.Imported.Sidecar.Key);if(saved is null)return;
        if(saved.Entry!=result.Imported.Sidecar||saved.SourceSha256 is not null&&saved.SourceSha256!=result.Source.Sha256||saved.Resolved)throw new InvalidDataException("Metadata capacity changed its first captured input.");
        if(CanSelect(saved.Code))
        {
            var selection=ReadAssociation(completed,result.Imported.Sidecar.Key)??throw new InvalidDataException("Unmatched metadata cannot acquire a worker result without an explicit selection.");
            if(selection.Original.Capture.Entry!=result.Imported.Original.Capture.Entry)throw new InvalidDataException("Metadata capacity substituted its selected original.");
        }
        else if(saved.Code is not("input-unavailable" or "metadata-too-large"))throw new InvalidDataException("Metadata capacity changed a permanent classification.");
        Write(completed,saved with{Code="metadata-too-large",SourceSha256=result.Source.Sha256});
    }
    private static bool CanSelect(string code)=>code is "name-mismatch" or "missing-media" or "ambiguous-media";
    private static string AssociationKey(Guid importId,string sidecarKey)=>AssociationPrefix+importId.ToString("D")+":"+sidecarKey;
    private byte[] EncodeAssociation(TakeoutCompletedInventory completed,TakeoutManualMetadataAssociation selection)=>_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {
        format="mpa-local-takeout-manual-association-1",importId=completed.ImportId.ToString("D"),indexSha256=completed.Result.Sha256,
        selectionSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(completed.CapturedContext))),
        sidecarKey=selection.Sidecar.Key,sourceSha256=selection.SourceSha256,issueCode=selection.IssueCode,originalKey=selection.Original.Capture.Entry.Key,
        originalSha256=selection.Original.Capture.Sha256,fileId=selection.Original.FileId,version=selection.Original.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
    }));
}

public sealed partial class TakeoutAssociationCatalog
{
    public TakeoutMetadataAssociation AssociateMetadata(TakeoutCompletedInventory completed,TakeoutManualMetadataAssociation selection,TakeoutMetadataDocument document)
    {
        ArgumentNullException.ThrowIfNull(selection);ArgumentNullException.ThrowIfNull(document);AssertCompletedSelection(completed);
        if(selection.Original.Capture.Inventory.ImportId!=completed.ImportId||selection.Original.Capture.Inventory.CapturedContext!=completed.CapturedContext||selection.SourceSha256!=document.SourceSha256||selection.Sidecar.Bytes!=document.CopyRawSource().LongLength)
            throw new InvalidDataException("Explicit metadata selection changed its captured import or raw bytes.");
        Enter();try
        {
            RequireSealed();
            foreach(var entry in new[]{selection.Sidecar,selection.Original.Capture.Entry})
            {
                using var query=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE key=$key",("$key",entry.Key));using var row=query.ExecuteReader();
                if(!row.Read()||ReadRow(row)!=entry)throw new InvalidDataException("Explicit metadata selection substituted an indexed input.");
            }
            return new(this,selection.Sidecar,new("matched",selection.Original.Capture.Entry),document);
        }finally{Exit();}
    }
}
