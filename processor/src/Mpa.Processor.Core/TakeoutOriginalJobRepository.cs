using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Immutable local upload intent, not current original-action authority.
/// File identity survives unknown provider outcomes; offsets are observations only.</summary>
public sealed class TakeoutOriginalWork
{
    internal ProcessorStateDatabase Owner {get;}
    public StageLease Lease {get;}
    public TakeoutOriginalCapture Capture {get;}
    public Guid ApprovalId {get;}
    public string? FileId {get;}
    public long AcknowledgedBytes {get;}
    internal TakeoutOriginalWork(ProcessorStateDatabase owner,StageLease lease,TakeoutOriginalCapture capture,Guid approvalId,string? fileId,long acknowledged)
    {Owner=owner;Lease=lease;Capture=capture;ApprovalId=approvalId;FileId=fileId;AcknowledgedBytes=acknowledged;}
}

/// <summary>Original intent is captured once, before allocation or provider writes.
/// Never deduplicates by content/name or resets a running upload using inventory
/// recovery. The application must reconcile unknown outcomes at the same file ID.</summary>
public sealed partial class TakeoutOriginalJobRepository(ProcessorStateDatabase database)
{
    public const string Kind="takeout-original-upload";
    public const string Stage="upload-original";
    private const string Item="{\"format\":\"mpa-takeout-original-item-1\"}";
    private static readonly string Recipe=Hash("mpa-takeout-original-upload:1"u8);
    private readonly ProcessorStateDatabase _database=database??throw new ArgumentNullException(nameof(database));
    private readonly CanonicalJson _codec=new(16384);

    public Guid Ensure(TakeoutOriginalCapture capture,Guid approvalId)
    {
        ArgumentNullException.ThrowIfNull(capture);if(approvalId==Guid.Empty)throw new ArgumentException("Capture the exact original-action approval identity.",nameof(approvalId));
        AssertInventory(capture.Inventory);var context=Encode(capture,approvalId);var id=JobId(capture.Inventory.ImportId,capture.Entry.Key);
        new DurableJobRepository(_database).EnsureJobWithInitialItem(id,Kind,context,new(id,Hash(context),[new(Stage,Recipe,[])],Item));return id;
    }
    public TakeoutOriginalWork? Claim(Guid jobId)
    {
        var lease=new DurableJobRepository(_database).ClaimNext(Kind,Stage,jobId);if(lease is null)return null;
        var result=Decode(lease);AssertCurrent(result);return result;
    }
    public void AssertCurrent(TakeoutOriginalWork work)
    {AssertLease(work,false);}

    /// <summary>Preserve a known canonical allocation even if pause/cancel closed
    /// action admission while the response arrived. This performs local bounded
    /// bookkeeping only and grants no original/provider operation.</summary>
    public TakeoutOriginalWork RetainAllocatedFileId(TakeoutOriginalWork work,string fileId)
    {
        if(!Opaque(fileId)||fileId=="root")throw new ArgumentException("An exact generated Drive file ID is required.",nameof(fileId));
        AssertLease(work,true);
        if(work.FileId is {} previous)
        {if(previous!=fileId)throw new InvalidDataException("The first assigned original file identity is immutable.");return work;}
        var checkpoint=Checkpoint(fileId,0);using var update=_database.Connection.CreateCommand();
        update.CommandText="""
            UPDATE item_stage SET checkpoint=$new WHERE job_id=$job AND item_id=$job AND name=$stage
            AND lease_id=$lease AND attempt=$attempt AND state IN('running','canceling') AND checkpoint IS $old
            """;
        update.Parameters.AddWithValue("$new",checkpoint);update.Parameters.AddWithValue("$job",work.Lease.JobId.ToString("D"));update.Parameters.AddWithValue("$stage",Stage);
        update.Parameters.AddWithValue("$lease",work.Lease.LeaseId.ToString("D"));update.Parameters.AddWithValue("$attempt",work.Lease.Attempt);update.Parameters.AddWithValue("$old",(object?)work.Lease.Checkpoint??DBNull.Value);
        if(update.ExecuteNonQuery()!=1)throw new StaleStageLeaseException();return Decode(work.Lease with{Checkpoint=checkpoint});
    }
    public TakeoutOriginalWork ObserveAcknowledgedBytes(TakeoutOriginalWork work,long acknowledged)
    {
        AssertCurrent(work);
        if(work.FileId is null||acknowledged<0||acknowledged>work.Capture.Entry.Bytes)throw new InvalidDataException("Original offset observation is outside its captured object.");
        var checkpoint=Checkpoint(work.FileId,acknowledged);
        new DurableJobRepository(_database).SaveCheckpoint(work.Lease,Encoding.UTF8.GetBytes(checkpoint));return Decode(work.Lease with{Checkpoint=checkpoint});
    }
    /// <summary>The actual application transfer owner calls this only after local
    /// replay, independent remote content verification and current original-action
    /// admission in this same serial state operation. Offsets alone never complete.</summary>
    public void ConfirmVerified(TakeoutOriginalWork work,string accountId,string folderId,string fileId,long bytes,long version,string independentlyVerifiedSha256)
    {
        AssertCurrent(work);var capture=work.Capture;
        if(accountId!=capture.Inventory.Target.AccountId||folderId!=capture.Inventory.Target.FolderId||fileId!=work.FileId||
            bytes!=capture.Entry.Bytes||version<1||independentlyVerifiedSha256!=capture.Sha256||work.AcknowledgedBytes!=bytes)
            throw new InvalidDataException("Verified original receipt differs from its captured source, destination or assigned identity.");
        new DurableJobRepository(_database).Complete(work.Lease,VerifiedReceipt(capture,work.ApprovalId,fileId,version));
    }
    private byte[] VerifiedReceipt(TakeoutOriginalCapture capture,Guid approval,string fileId,long version)=>_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-takeout-original-result-1",importId=capture.Inventory.ImportId.ToString("D"),mediaId=capture.Entry.MediaId.ToString("D"),
            approvalId=approval.ToString("D"),accountId=capture.Inventory.Target.AccountId,folderId=capture.Inventory.Target.FolderId,fileId,
            bytes=capture.Entry.Bytes.ToString(CultureInfo.InvariantCulture),version=version.ToString(CultureInfo.InvariantCulture),sha256=capture.Sha256
        }));

    private void AssertLease(TakeoutOriginalWork work,bool retirementBookkeeping)
    {
        ArgumentNullException.ThrowIfNull(work);
        if(!ReferenceEquals(work.Owner,_database))throw new InvalidOperationException("Original work belongs to another native state owner.");
        var context=Encode(work.Capture,work.ApprovalId);var hash=Hash(context);var lease=work.Lease;
        if(lease.JobId!=JobId(work.Capture.Inventory.ImportId,work.Capture.Entry.Key)||lease.ItemId!=lease.JobId||lease.Stage!=Stage||lease.CapturedItemContext!=Item||
            lease.CapturedContext!=Encoding.UTF8.GetString(context)||lease.Checkpoint!=(work.FileId is null?null:Checkpoint(work.FileId,work.AcknowledgedBytes)))
            throw new InvalidDataException("Original work differs from its first captured input or identity.");
        using var query=_database.Connection.CreateCommand();query.CommandText="""
            SELECT count(*) FROM item_stage s JOIN job j ON j.id=s.job_id JOIN job_item i ON i.job_id=s.job_id AND i.id=s.item_id
            WHERE j.id=$job AND j.kind=$kind AND j.captured_context=$context AND j.context_hash=$hash
            AND i.id=$job AND i.captured_context=$item AND i.input_fingerprint=$hash
            AND s.name=$stage AND s.recipe_hash=$recipe AND s.lease_id=$lease AND s.attempt=$attempt AND s.checkpoint IS $checkpoint
            AND ((s.state='running' AND j.paused=0 AND j.canceled=0) OR ($retirement=1 AND s.state IN('running','canceling')))
            """;
        query.Parameters.AddWithValue("$job",lease.JobId.ToString("D"));query.Parameters.AddWithValue("$kind",Kind);query.Parameters.AddWithValue("$context",lease.CapturedContext);query.Parameters.AddWithValue("$hash",hash);
        query.Parameters.AddWithValue("$item",Item);query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$recipe",Recipe);query.Parameters.AddWithValue("$lease",lease.LeaseId.ToString("D"));
        query.Parameters.AddWithValue("$attempt",lease.Attempt);query.Parameters.AddWithValue("$checkpoint",(object?)lease.Checkpoint??DBNull.Value);query.Parameters.AddWithValue("$retirement",retirementBookkeeping?1:0);
        if((long)query.ExecuteScalar()! !=1)throw new StaleStageLeaseException();
    }
    private TakeoutOriginalWork Decode(StageLease lease,Microsoft.Data.Sqlite.SqliteTransaction? transaction=null)
    {
        var value=DecodeCapture(lease.CapturedContext,lease.Checkpoint,transaction);
        return new(_database,lease,value.Capture,value.Approval,value.FileId,value.Acknowledged);
    }
    private (TakeoutOriginalCapture Capture,Guid Approval,string? FileId,long Acknowledged) DecodeCapture(string context,string? savedCheckpoint,Microsoft.Data.Sqlite.SqliteTransaction? transaction=null)
    {
        using var document=JsonDocument.Parse(context);var root=document.RootElement;
        var inventory=new TakeoutImportInventoryRepository(_database).ReadCompleted(root.GetProperty("importId").GetGuid(),transaction)??throw new InvalidDataException("Original upload lost its completed selected-input receipt.");
        var entry=new TakeoutCapturedEntry(root.GetProperty("partId").GetGuid(),root.GetProperty("entryKey").GetString()!,root.GetProperty("relativePath").GetString()!,
            Decimal(root,"bytes"),TakeoutEntryKind.Media,root.GetProperty("ordinal").GetInt32(),Decimal(root,"sourceStamp"),root.GetProperty("mediaId").GetGuid());
        var capture=new TakeoutOriginalCapture(inventory,entry,root.GetProperty("sha256").GetString()!);var approval=root.GetProperty("approvalId").GetGuid();
        if(!Encode(capture,approval).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(context)))throw new InvalidDataException("Original upload context lost its exact captured schema.");
        string? fileId=null;long acknowledged=0;
        if(savedCheckpoint is {} checkpoint)
        {
            using var progress=JsonDocument.Parse(checkpoint);fileId=progress.RootElement.GetProperty("fileId").GetString();acknowledged=Decimal(progress.RootElement,"acknowledgedBytes");
            if(!Opaque(fileId)||fileId=="root"||acknowledged>entry.Bytes||Checkpoint(fileId!,acknowledged)!=checkpoint)throw new InvalidDataException("Original progress changed its assigned identity or bounds.");
        }
        return(capture,approval,fileId,acknowledged);
    }
    private void AssertInventory(TakeoutCompletedInventory inventory)
    {
        if(inventory.InstallationId!=_database.InstallationId||inventory.StateDirectory!=_database.StateDirectory)throw new InvalidOperationException("Original capture belongs to another native installation.");
        var actual=new TakeoutImportInventoryRepository(_database).ReadCompleted(inventory.ImportId);
        if(actual is null||actual.CapturedContext!=inventory.CapturedContext||actual.Result!=inventory.Result)throw new InvalidDataException("Original capture lost its exact completed inventory.");
    }
    private byte[] Encode(TakeoutOriginalCapture capture,Guid approval)
    {
        var entry=capture.Entry;var inventory=capture.Inventory;
        if(approval==Guid.Empty||entry.MediaId==Guid.Empty||entry.Kind!=TakeoutEntryKind.Media||entry.Bytes is <1 or >TakeoutSelectedInput.MaximumEntryBytes||
            entry.Ordinal<0||entry.SourceStamp<0||!IsHash(entry.Key)||!IsHash(capture.Sha256)||inventory.Parts.All(part=>part.PartId!=entry.PartId))throw new InvalidDataException("Invalid original input capture.");
        _=TakeoutSelectedInput.LogicalPath(entry.RelativePath);
        return _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-takeout-original-upload-1",importId=inventory.ImportId.ToString("D"),indexSha256=inventory.Result.Sha256,selectionSha256=Hash(Encoding.UTF8.GetBytes(inventory.CapturedContext)),
            target=inventory.Target,partId=entry.PartId.ToString("D"),entryKey=entry.Key,relativePath=entry.RelativePath,mediaId=entry.MediaId.ToString("D"),
            bytes=entry.Bytes.ToString(CultureInfo.InvariantCulture),ordinal=entry.Ordinal,sourceStamp=entry.SourceStamp.ToString(CultureInfo.InvariantCulture),
            sha256=capture.Sha256,name=capture.Name,mimeType=capture.MimeType,approvalId=approval.ToString("D"),recipeSha256=Recipe
        }));
    }
    private string Checkpoint(string fileId,long acknowledged)=>Encoding.UTF8.GetString(_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-takeout-original-progress-1",fileId,acknowledgedBytes=acknowledged.ToString(CultureInfo.InvariantCulture)})));
    private static Guid JobId(Guid importId,string key)
    {var bytes=SHA256.HashData(Encoding.UTF8.GetBytes("mpa-takeout-original-upload:1\n"+importId.ToString("D")+"\n"+key));return new(bytes.AsSpan(0,16));}
    private static long Decimal(JsonElement root,string key)
    {var text=root.GetProperty(key).GetString();if(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var number)||number<0||number.ToString(CultureInfo.InvariantCulture)!=text)throw new InvalidDataException("Invalid original decimal string.");return number;}
    private static bool IsHash(string value)=>value is {Length:64}&&value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    private static bool Opaque(string? value)=>value is{Length:>=1 and <=256}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
