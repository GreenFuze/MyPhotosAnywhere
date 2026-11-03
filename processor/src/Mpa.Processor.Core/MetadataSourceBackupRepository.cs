using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical local readback evidence and portable content locator. This
/// record is neither fresh remote availability nor raw-source retirement permission.</summary>
public sealed record MetadataSourceBackupReceipt(Guid JobId,Guid PublicationId,WorkspacePublicationScope Scope,
    CatalogObjectReference Bundle,long VerifiedVersion,ImportedMetadataFingerprint Source,string ReadyContextSha256);

/// <summary>Records only actual ready results and an actually verified native
/// raw-bundle outbox. First bindings are immutable. No provider IO, source delete,
/// fake completion or historical permission is admitted by this repository.</summary>
public sealed class MetadataSourceBackupRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database=database??throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget??throw new ArgumentNullException(nameof(budget));
    private const string Prefix="metadata-source-backup-v1:";
    private static readonly CanonicalJson Codec=new(16384);
    public IReadOnlyList<MetadataSourceBackupReceipt> RecordBundle(Guid publicationId,IReadOnlyList<TakeoutVerifiedMetadata> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if(publicationId==Guid.Empty||results.Count is <1 or >MetadataSourceBundle.MaximumSources||results.Any(result=>result is null)||results.Select(result=>result.JobId).Distinct().Count()!=results.Count)
            throw new ArgumentException("A bounded native ready-result batch and actual upload identity are required.");
        var uploaded=new WorkspacePublicationRepository(_database,_budget).Read(publicationId)??throw new InvalidDataException("Raw metadata backup lost its actual upload outbox.");
        RequireUpload(uploaded);var bundle=MetadataSourceBundle.Read(uploaded.Payload.Span);var receipts=new List<MetadataSourceBackupReceipt>(results.Count);
        foreach(var result in results)
        {
            result.AssertReadyForReplayRetirement(_database);
            if(result.Imported.Original.Capture.Inventory.Target.LibraryId!=uploaded.Scope.LibraryId)throw new InvalidDataException("Raw metadata backup changed its library.");
            RequireSource(result,bundle);
            receipts.Add(Describe(result,uploaded));
        }
        using var reservation=_budget.Reserve(checked(results.Count*32768L+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
        using var transaction=_database.Connection.BeginTransaction();
        foreach(var receipt in receipts)
        {
            var key=Key(receipt.JobId,receipt.Scope.AccountId,receipt.Scope.FolderId);var body=Encoding.UTF8.GetString(Encode(receipt));
            using(var read=_database.Connection.CreateCommand())
            {
                read.Transaction=transaction;read.CommandText="SELECT value FROM processor_meta WHERE key=$key";read.Parameters.AddWithValue("$key",key);
                if(read.ExecuteScalar() is string previous)
                {
                    if(previous!=body)throw new InvalidDataException("The first raw metadata backup locator is immutable.");continue;
                }
            }
            using var insert=_database.Connection.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value)";insert.Parameters.AddWithValue("$key",key);insert.Parameters.AddWithValue("$value",body);insert.ExecuteNonQuery();
        }
        reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();return receipts.AsReadOnly();
    }
    public MetadataSourceBackupReceipt? Read(TakeoutVerifiedMetadata result,CatalogWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(result);ArgumentNullException.ThrowIfNull(workspace);result.AssertReadyForReplayRetirement(_database);
        string raw;
        using(var query=_database.Connection.CreateCommand())
        {
            query.CommandText="SELECT length(value),value FROM processor_meta WHERE key=$key";query.Parameters.AddWithValue("$key",Key(result.JobId,workspace.AccountId,workspace.FolderId));using var row=query.ExecuteReader();
            if(!row.Read())return null;if(row.GetInt64(0) is <1 or >16384)throw new InvalidDataException("Raw metadata locator exceeded its bounded schema.");raw=row.GetString(1);
        }
        try
        {
            using var json=JsonDocument.Parse(raw);var id=json.RootElement.GetProperty("publicationId").GetGuid();
            var upload=new WorkspacePublicationRepository(_database,_budget).Read(id)??throw new InvalidDataException("Raw metadata locator lost its actual readback receipt.");RequireUpload(upload);
            RequireSource(result,MetadataSourceBundle.Read(upload.Payload.Span));var expected=Describe(result,upload);
            if(expected.Scope.AccountId!=workspace.AccountId||expected.Scope.FolderId!=workspace.FolderId||expected.Scope.LibraryId!=result.Imported.Original.Capture.Inventory.Target.LibraryId||
                !Encode(expected).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(raw)))throw new InvalidDataException("Raw metadata locator changed its first native context, workspace or upload evidence.");
            return expected;
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {throw new InvalidDataException("Raw metadata backup locator is invalid.",error);}
    }
    private static void RequireUpload(WorkspacePublicationRecord row)
    {
        if(row.Role!=MetadataSourceBundle.Role||row.MimeType!=MetadataSourceBundle.MimeType||row.FileId is null||row.VerifiedVersion is not >=1)
            throw new InvalidDataException("Raw metadata backup requires a verified native bundle upload, not an allocation or caller reference.");
        var payload=CatalogDependencyPayload.Capture(row.Role,row.MimeType,row.Payload.Span);
        if(payload.IdFor(row.Scope)!=row.Id)throw new InvalidDataException("Raw metadata upload changed its immutable current-generation identity.");
    }
    private static MetadataSourceBackupReceipt Describe(TakeoutVerifiedMetadata result,WorkspacePublicationRecord upload)=>new(result.JobId,upload.Id,upload.Scope,
        new(upload.Scope.AccountId,upload.FileId!,upload.Scope.FolderId,upload.Payload.Length,upload.Sha256),upload.VerifiedVersion!.Value,
        new(result.Source.Bytes,result.Source.Sha256),Hash(Encoding.UTF8.GetBytes(result.ItemContext)));
    private static void RequireSource(TakeoutVerifiedMetadata result,MetadataSourceBundle bundle)
    {
        var raw=bundle.ReadSource(result.Source.Sha256);var document=TakeoutMetadataDocument.ParseSource(raw);var artifact=document.EncodeArtifact();
        if(raw.LongLength!=result.Source.Bytes||document.SourceSha256!=result.Source.Sha256||artifact.LongLength!=result.Artifact.Bytes||Hash(artifact)!=result.Artifact.Sha256)
            throw new InvalidDataException("Raw metadata backup changed its first native source or worker result.");
    }
    private static string Key(Guid job,string account,string folder)
    {
        _=new CatalogWorkspace(account,folder);return Prefix+job.ToString("D")+":"+Hash(Encoding.UTF8.GetBytes(account+"\0"+folder));
    }
    private static byte[] Encode(MetadataSourceBackupReceipt receipt)=>Codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-local-metadata-source-backup-1",jobId=receipt.JobId.ToString("D"),publicationId=receipt.PublicationId.ToString("D"),scope=receipt.Scope,
        bundle=receipt.Bundle,verifiedVersion=receipt.VerifiedVersion.ToString(CultureInfo.InvariantCulture),sourceBytes=receipt.Source.Bytes.ToString(CultureInfo.InvariantCulture),sourceSha256=receipt.Source.Sha256,readyContextSha256=receipt.ReadyContextSha256}));
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
