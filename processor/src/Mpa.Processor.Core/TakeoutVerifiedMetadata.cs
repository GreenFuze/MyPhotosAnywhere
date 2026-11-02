using System.Globalization;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical joined-worker receipt. Read-only local retention evidence,
/// never a fabricated running lease or current publication/provider authority.</summary>
public sealed class TakeoutVerifiedMetadata
{
    internal ProcessorStateDatabase Owner{get;}
    public Guid JobId{get;}
    public TakeoutImportedMetadata Imported{get;}
    public ArtifactDescriptor Source{get;}
    public ArtifactDescriptor Artifact{get;}
    internal string ItemContext{get;}
    internal Guid LeaseId{get;}
    internal int Attempt{get;}
    internal TakeoutVerifiedMetadata(ProcessorStateDatabase owner,Guid job,TakeoutImportedMetadata imported,ArtifactDescriptor source,ArtifactDescriptor artifact,string context,Guid lease,int attempt)
    {Owner=owner;JobId=job;Imported=imported;Source=source;Artifact=artifact;ItemContext=context;LeaseId=lease;Attempt=attempt;}
}

public sealed partial class TakeoutMetadataJobRepository
{
    public TakeoutVerifiedMetadata? ReadCompletedImported(Guid importId,string sidecarKey)
    {
        if(importId==Guid.Empty||!IsHash(sidecarKey))throw new ArgumentException("An exact captured sidecar identity is required.");
        var id=ImportedJobId(importId,sidecarKey);string jobContext,itemContext,receipt;int attempt;
        using(var query=_database.Connection.CreateCommand())
        {
            query.CommandText="""
                SELECT j.kind,j.captured_context,j.context_hash,i.captured_context,i.input_fingerprint,s.recipe_hash,
                       s.state,s.attempt,s.lease_id,s.checkpoint,s.result_receipt,
                       (SELECT count(*) FROM state_outbox o WHERE o.job_id=j.id AND o.item_id=i.id AND o.stage_name=s.name AND o.receipt=s.result_receipt)
                FROM job j LEFT JOIN job_item i ON i.job_id=j.id AND i.id=j.id
                LEFT JOIN item_stage s ON s.job_id=j.id AND s.item_id=i.id AND s.name=$stage WHERE j.id=$id
                """;
            query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$id",id.ToString("D"));using var row=query.ExecuteReader();
            if(!row.Read())return null;
            if(row.GetString(0)!=JobKind||row.IsDBNull(3)||row.IsDBNull(5)||row.GetString(5)!=TakeoutMetadataDocument.RecipeSha256||
                Hash(Encoding.UTF8.GetBytes(row.GetString(1)))!=row.GetString(2)||Hash(Encoding.UTF8.GetBytes(row.GetString(3)))!=row.GetString(4))
                throw new InvalidDataException("Metadata lost its native captured context or recipe integrity.");
            if(row.GetString(6)!="ready")
            {
                if(row.GetString(6) is not("queued" or "running" or "canceling" or "failed" or "unsupported" or "canceled")||!row.IsDBNull(10))
                    throw new InvalidDataException("Metadata has contradictory completion state.");
                return null;
            }
            if(row.GetInt32(7)<1||!row.IsDBNull(8)||!row.IsDBNull(9)||row.IsDBNull(10)||row.GetInt64(11)!=1)
                throw new InvalidDataException("Metadata lost its complete atomic result receipt.");
            jobContext=row.GetString(1);itemContext=row.GetString(3);receipt=row.GetString(10);attempt=row.GetInt32(7);
        }
        var captured=DecodeCaptured(id,id,jobContext,itemContext);
        if(captured.Imported is not{} imported||imported.Original.Capture.Inventory.ImportId!=importId||imported.Sidecar.Key!=sidecarKey)
            throw new InvalidDataException("Metadata completion changed its imported provenance.");
        using var parsed=JsonDocument.Parse(receipt);var value=parsed.RootElement.GetProperty("artifact");var size=value.GetProperty("bytes").GetString();
        if(!long.TryParse(size,NumberStyles.None,CultureInfo.InvariantCulture,out var bytes)||bytes.ToString(CultureInfo.InvariantCulture)!=size)
            throw new InvalidDataException("Metadata receipt has a noncanonical artifact size.");
        var artifact=new ArtifactDescriptor(bytes,value.GetProperty("sha256").GetString()!,value.GetProperty("relativePath").GetString()!);
        var name=artifact.RelativePath;
        if(bytes is <1 or >TakeoutMetadataDocument.MaximumArtifactBytes||name.Length!=43||!name.StartsWith("facts-",StringComparison.Ordinal)||!name.EndsWith(".json",StringComparison.Ordinal)||
            !Guid.TryParseExact(name.AsSpan(6,32),"N",out var lease)||lease==Guid.Empty||name!="facts-"+lease.ToString("N")+".json"||
            !ResultReceipt(captured.MediaId,captured.MediaHash,id,captured.Sidecar,artifact).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(receipt)))
            throw new InvalidDataException("Metadata result receipt changed its assigned identity, source or content.");
        return new(_database,id,imported,captured.Sidecar,artifact,itemContext,lease,attempt);
    }
}
