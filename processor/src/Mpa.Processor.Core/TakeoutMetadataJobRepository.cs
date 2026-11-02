using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Captured local metadata work, not permission to read/write an original
/// or a provider. Only the native repository constructs this exact lease binding.</summary>
public sealed class TakeoutMetadataWork
{
    internal object Owner { get; }
    public StageLease Lease { get; }
    public Guid MediaId { get; }
    public string MediaInputSha256 { get; }
    public ArtifactDescriptor Sidecar { get; }
    public TakeoutImportedMetadata? Imported {get;}
    internal TakeoutMetadataWork(object owner,StageLease lease,Guid mediaId,string mediaInputSha256,ArtifactDescriptor sidecar,TakeoutImportedMetadata? imported=null)
    {Owner=owner;Lease=lease;MediaId=mediaId;MediaInputSha256=mediaInputSha256;Sidecar=sidecar;Imported=imported;}
}

/// <summary>One metadata job per captured sidecar/media binding. Independent media
/// uploads are never dependencies of this optional analysis. All successful stage
/// receipts still use the existing atomic durable outbox; raw bytes stay staged.</summary>
public sealed partial class TakeoutMetadataJobRepository
{
    public const string JobKind="takeout-metadata";
    public const string Stage="metadata";
    private static readonly byte[] JobContext=Encoding.UTF8.GetBytes("{\"format\":\"mpa-takeout-metadata-job-1\"}");
    private readonly ProcessorStateDatabase _database;
    private readonly DurableJobRepository _jobs;
    private readonly CanonicalJson _codec=new(16*1024);
    public TakeoutMetadataJobRepository(ProcessorStateDatabase database)
    {_database=database??throw new ArgumentNullException(nameof(database));_jobs=new(database);}

    public bool Ensure(Guid jobId,Guid itemId,Guid mediaId,string mediaInputSha256,ArtifactDescriptor sidecar)
        =>EnsureCaptured(jobId,itemId,mediaId,mediaInputSha256,sidecar,JobContext);
    private bool EnsureCaptured(Guid jobId,Guid itemId,Guid mediaId,string mediaInputSha256,ArtifactDescriptor sidecar,byte[] capturedJob)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        if(mediaId==Guid.Empty || !IsHash(mediaInputSha256) || sidecar.Bytes>TakeoutMetadataDocument.MaximumSourceBytes || sidecar.RelativePath!="input.bin")
            throw new ArgumentException("A bounded captured sidecar and explicit media identity are required.");
        var context=_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-takeout-sidecar-input-1",mediaId=mediaId.ToString("D"),mediaInputSha256,bytes=sidecar.Bytes.ToString(CultureInfo.InvariantCulture),
            sha256=sidecar.Sha256,relativePath=sidecar.RelativePath,recipeSha256=TakeoutMetadataDocument.RecipeSha256
        }));
        return _jobs.EnsureJobWithInitialItem(jobId,JobKind,capturedJob,
            new(itemId,Hash(context),[new(Stage,TakeoutMetadataDocument.RecipeSha256,[])],Encoding.UTF8.GetString(context)));
    }

    public TakeoutMetadataWork? ClaimNext()
        =>DecodeClaim(_jobs.ClaimNext(JobKind,Stage));
    public TakeoutMetadataWork? Claim(Guid jobId)
        =>DecodeClaim(_jobs.ClaimNext(JobKind,Stage,jobId));
    private TakeoutMetadataWork? DecodeClaim(StageLease? lease)
    {
        if(lease is null)return null;
        var work=Decode(lease);AssertCurrent(work);return work;
    }

    public void AssertCurrent(TakeoutMetadataWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if(!ReferenceEquals(work.Owner,_database))throw new InvalidOperationException("Metadata work belongs to a different native state owner.");
        var expected=Decode(work.Lease);
        if(expected.MediaId!=work.MediaId || expected.MediaInputSha256!=work.MediaInputSha256 || expected.Sidecar.Bytes!=work.Sidecar.Bytes || expected.Sidecar.Sha256!=work.Sidecar.Sha256 || expected.Sidecar.RelativePath!=work.Sidecar.RelativePath)
            throw new InvalidDataException("Metadata work differs from its original captured sidecar and media identity.");
        var lease=work.Lease;using var query=_database.Connection.CreateCommand();
        query.CommandText="""
            SELECT j.captured_context,j.context_hash,i.captured_context,i.input_fingerprint
            FROM item_stage s JOIN job j ON j.id=s.job_id JOIN job_item i ON i.job_id=s.job_id AND i.id=s.item_id
            WHERE s.job_id=$job AND s.item_id=$item AND s.name=$stage AND s.lease_id=$lease AND s.attempt=$attempt
              AND s.state='running' AND j.kind=$kind AND j.paused=0 AND j.canceled=0 AND s.recipe_hash=$recipe
              AND j.captured_context=$jobContext AND i.captured_context=$itemContext AND s.checkpoint IS $checkpoint
            """;
        query.Parameters.AddWithValue("$job",lease.JobId.ToString("D"));query.Parameters.AddWithValue("$item",lease.ItemId.ToString("D"));
        query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$lease",lease.LeaseId.ToString("D"));query.Parameters.AddWithValue("$attempt",lease.Attempt);
        query.Parameters.AddWithValue("$kind",JobKind);query.Parameters.AddWithValue("$recipe",TakeoutMetadataDocument.RecipeSha256);
        query.Parameters.AddWithValue("$jobContext",lease.CapturedContext);query.Parameters.AddWithValue("$itemContext",lease.CapturedItemContext);
        query.Parameters.AddWithValue("$checkpoint",(object?)lease.Checkpoint??DBNull.Value);
        using var row=query.ExecuteReader();if(!row.Read())throw new StaleStageLeaseException();
        if(Hash(Encoding.UTF8.GetBytes(row.GetString(0)))!=row.GetString(1) || Hash(Encoding.UTF8.GetBytes(row.GetString(2)))!=row.GetString(3))
            throw new InvalidDataException("Durable metadata context no longer matches its original integrity record.");
    }

    internal void CompleteAfterJoinedWorker(TakeoutMetadataWork work,TakeoutMetadataWorkerOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);var (artifact,document)=operation.VerifiedResult(work);
        AssertCurrent(work);
        if(document.SourceSha256!=work.Sidecar.Sha256 || artifact.Bytes>TakeoutMetadataDocument.MaximumArtifactBytes ||
            artifact.RelativePath!="facts-"+work.Lease.LeaseId.ToString("N")+".json")
            throw new InvalidDataException("Metadata result substituted its captured source, output identity or budget.");
        _jobs.Complete(work.Lease,_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-takeout-metadata-result-1",mediaId=work.MediaId.ToString("D"),mediaInputSha256=work.MediaInputSha256,itemId=work.Lease.ItemId.ToString("D"),
            sourceSha256=work.Sidecar.Sha256,recipeSha256=TakeoutMetadataDocument.RecipeSha256,
            artifact=new {relativePath=artifact.RelativePath,bytes=artifact.Bytes.ToString(CultureInfo.InvariantCulture),sha256=artifact.Sha256}
        })));
    }

    internal void FailAfterJoinedWorker(TakeoutMetadataWork work,TakeoutMetadataWorkerOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);var code=operation.VerifiedIssue(work);
        AssertCurrent(work);
        if(code is not("metadata-too-large" or "invalid-metadata" or "unsupported-metadata"))
            throw new InvalidDataException("The installed metadata worker returned an unknown operational failure.");
        _jobs.Fail(work.Lease,code,"Sidecar metadata needs attention; original media and captured input are retained.",retryable:false,unsupported:true);
    }

    internal void ReturnJoinedOperation(TakeoutMetadataWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if(!ReferenceEquals(work.Owner,_database))throw new InvalidOperationException("Metadata work belongs to a different native state owner.");
        _=Decode(work.Lease);_jobs.ReturnJoinedRead(work.Lease);
    }

    private TakeoutMetadataWork Decode(StageLease lease)
    {
        if(lease.Stage!=Stage ||
            !_codec.Encode(Encoding.UTF8.GetBytes(lease.CapturedItemContext)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(lease.CapturedItemContext)))
            throw new InvalidDataException("Metadata lease has an unknown or changed captured contract.");
        using var json=JsonDocument.Parse(lease.CapturedItemContext);var root=json.RootElement;
        if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count()!=7 || root.GetProperty("format").GetString()!="mpa-takeout-sidecar-input-1" ||
            root.GetProperty("recipeSha256").GetString()!=TakeoutMetadataDocument.RecipeSha256)
            throw new InvalidDataException("Metadata lease changed its fixed recipe or input schema.");
        var media=root.GetProperty("mediaId").GetString();var mediaHash=root.GetProperty("mediaInputSha256").GetString();var size=root.GetProperty("bytes").GetString();
        if(!IsHash(mediaHash) || !Guid.TryParseExact(media,"D",out var mediaId) || mediaId==Guid.Empty || mediaId.ToString("D")!=media ||
            !long.TryParse(size,NumberStyles.None,CultureInfo.InvariantCulture,out var bytes) || bytes.ToString(CultureInfo.InvariantCulture)!=size ||
            bytes>TakeoutMetadataDocument.MaximumSourceBytes)
            throw new InvalidDataException("Metadata lease has invalid captured identities or byte bounds.");
        var descriptor=new ArtifactDescriptor(bytes,root.GetProperty("sha256").GetString()!,root.GetProperty("relativePath").GetString()!);
        if(descriptor.RelativePath!="input.bin")throw new InvalidDataException("Metadata work must use its assigned sidecar snapshot.");
        var imported=lease.CapturedContext==Encoding.UTF8.GetString(JobContext)?null:DecodeImported(lease,mediaId,mediaHash!,descriptor);
        return new(_database,lease,mediaId,mediaHash!,descriptor,imported);
    }
    private static bool IsHash(string? value)=>value is {Length:64} && value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
