using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical native copy evidence for metadata association. No running
/// lease, current Drive observation or original-action permission is exposed.</summary>
public sealed class TakeoutVerifiedOriginal
{
    internal ProcessorStateDatabase Owner{get;}
    public Guid JobId{get;}
    public TakeoutOriginalCapture Capture{get;}
    public string FileId{get;}
    public long Version{get;}
    public Guid AssetId{get;}
    internal TakeoutVerifiedOriginal(ProcessorStateDatabase owner,Guid jobId,TakeoutOriginalCapture capture,string fileId,long version)
    {Owner=owner;JobId=jobId;Capture=capture;FileId=fileId;Version=version;AssetId=new DriveAssetIdentity(capture.Inventory.Target.LibraryId).Derive(capture.Inventory.Target.AccountId,fileId);}
}

public sealed partial class TakeoutOriginalJobRepository
{
    public TakeoutVerifiedOriginal? ReadVerified(Guid importId,string entryKey)
    {
        if(importId==Guid.Empty||!IsHash(entryKey))throw new ArgumentException("An exact imported media identity is required.");
        var id=JobId(importId,entryKey);string context,checkpoint,receipt;long version;
        using(var query=_database.Connection.CreateCommand())
        {
            query.CommandText="""
                SELECT j.kind,j.captured_context,j.context_hash,i.captured_context,i.input_fingerprint,
                       s.recipe_hash,s.state,s.attempt,s.lease_id,s.checkpoint,s.result_receipt,
                       (SELECT count(*) FROM state_outbox o WHERE o.job_id=j.id AND o.item_id=i.id AND o.stage_name=s.name AND o.receipt=s.result_receipt)
                FROM job j LEFT JOIN job_item i ON i.job_id=j.id AND i.id=j.id
                LEFT JOIN item_stage s ON s.job_id=j.id AND s.item_id=i.id AND s.name=$stage WHERE j.id=$id
                """;
            query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$id",id.ToString("D"));using var row=query.ExecuteReader();
            if(!row.Read())return null;
            if(row.GetString(0)!=Kind||row.IsDBNull(3)||row.IsDBNull(5)||row.GetString(3)!=Item||row.GetString(5)!=Recipe||
                Hash(Encoding.UTF8.GetBytes(row.GetString(1)))!=row.GetString(2)||row.GetString(2)!=row.GetString(4))
                throw new InvalidDataException("Original copy evidence lost its native context or stage integrity.");
            var status=row.GetString(6);
            if(status!="ready")
            {
                if(status is not("queued" or "running" or "canceling" or "failed" or "unsupported" or "canceled")||!row.IsDBNull(10))
                    throw new InvalidDataException("Unknown or contradictory original copy state.");
                return null;
            }
            if(row.GetInt32(7)<1||!row.IsDBNull(8)||row.IsDBNull(9)||row.IsDBNull(10)||row.GetInt64(11)!=1)
                throw new InvalidDataException("Verified original lost its complete atomic receipt.");
            context=row.GetString(1);checkpoint=row.GetString(9);receipt=row.GetString(10);
        }
        var captured=DecodeCapture(context,checkpoint);
        using(var document=JsonDocument.Parse(receipt))version=Decimal(document.RootElement,"version");
        if(captured.Capture.Inventory.ImportId!=importId||captured.Capture.Entry.Key!=entryKey||captured.FileId is null||version<1||
            captured.Acknowledged!=captured.Capture.Entry.Bytes||!VerifiedReceipt(captured.Capture,captured.Approval,captured.FileId,version).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(receipt)))
            throw new InvalidDataException("Original copy receipt changed its input, target, first identity or independently verified bytes.");
        return new(_database,id,captured.Capture,captured.FileId,version);
    }
}
