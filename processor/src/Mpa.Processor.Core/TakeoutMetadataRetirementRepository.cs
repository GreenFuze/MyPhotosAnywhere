using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

// A local duplicate-retirement intent, never provider authority. The actual
// verified outbox continues to retain all raw bytes. No missing-file heuristic
// can create this record or reconstruct a source without it.
internal sealed record TakeoutMetadataRetirement(TakeoutVerifiedMetadata Ready,MetadataSourceBackupReceipt Backup,TakeoutMetadataDocument Document,string Body);

internal sealed class TakeoutMetadataRetirementRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private const string Prefix="takeout-metadata-retirement-v1:";
    private const string Active="takeout-metadata-retirement-active-v1";
    private const string Done="done:";
    private static readonly CanonicalJson Codec=new(16384);
    internal TakeoutMetadataRetirement Capture(TakeoutVerifiedMetadata ready,CatalogWorkspace workspace,Guid publicationId)
    {
        ready.AssertReadyForReplayRetirement(database);
        var receipt=new MetadataSourceBackupRepository(database,budget).Read(ready,workspace)
            ??throw new InvalidDataException("Keep the private metadata until its actual raw backup is recorded.");
        if(receipt.PublicationId!=publicationId)throw new InvalidDataException("Metadata cleanup changed its first backup identity.");
        var row=new WorkspacePublicationRepository(database,budget).Read(publicationId)
            ??throw new InvalidDataException("Metadata cleanup lost the durable raw backup bytes.");
        var document=TakeoutMetadataDocument.ParseSource(MetadataSourceBundle.Read(row.Payload.Span).ReadSource(ready.Source.Sha256));
        _=ready.DescribeImportedFacts(document);
        var body=Encoding.UTF8.GetString(Codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa-local-metadata-retirement-1",jobId=ready.JobId,importId=ready.Imported.Original.Capture.Inventory.ImportId,
            sidecarKey=ready.Imported.Sidecar.Key,leaseId=ready.LeaseId,attempt=ready.Attempt,
            contextSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ready.ItemContext))),
            source=ready.Source,artifact=ready.Artifact,backup=receipt
        })));
        return new(ready,receipt,document,body);
    }
    internal TakeoutMetadataRetirement? Read(TakeoutVerifiedMetadata ready)
    {
        var raw=ReadValue(Prefix+ready.JobId.ToString("D"));
        return raw is null?null:Decode(raw,ready);
    }
    internal bool HasIntent(TakeoutVerifiedMetadata ready)=>ReadValue(Prefix+ready.JobId.ToString("D")) is not null;
    internal TakeoutMetadataRetirement? Pending()
    {
        var raw=ReadValue(Active);return raw is null||raw.StartsWith(Done,StringComparison.Ordinal)?null:Decode(raw,null);
    }
    internal TakeoutMetadataRetirement? Last()
    {var raw=ReadValue(Active);return raw is null?null:Decode(raw.StartsWith(Done,StringComparison.Ordinal)?raw[Done.Length..]:raw,null);}
    internal void Prepare(TakeoutMetadataRetirement captured)
    {
        var actual=Capture(captured.Ready,new(captured.Backup.Scope.AccountId,captured.Backup.Scope.FolderId),captured.Backup.PublicationId);
        if(actual.Body!=captured.Body)throw new InvalidDataException("Metadata retirement changed its native capture.");
        // Native ready/original readers own their complete read contract. Finish
        // those checks before opening this short write transaction, then compare
        // the exact retained pointer again inside it.
        var checkedPrevious=ReadValue(Active);
        if(checkedPrevious is not null&&checkedPrevious.StartsWith(Done,StringComparison.Ordinal))_=Decode(checkedPrevious[Done.Length..],null);
        using var reservation=budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        using var transaction=database.Connection.BeginTransaction();
        foreach(var key in new[]{Prefix+captured.Ready.JobId.ToString("D"),Active})
        {
            using var read=database.Connection.CreateCommand();read.Transaction=transaction;read.CommandText="SELECT value FROM processor_meta WHERE key=$key";read.Parameters.AddWithValue("$key",key);
            if(read.ExecuteScalar() is string previous)
            {
                if(key==Active&&previous.StartsWith(Done,StringComparison.Ordinal))
                {
                    if(previous!=checkedPrevious)throw new InvalidDataException("Metadata retirement changed its last joined capture during admission.");
                    using var replace=database.Connection.CreateCommand();replace.Transaction=transaction;replace.CommandText="UPDATE processor_meta SET value=$body WHERE key=$key AND value=$previous";replace.Parameters.AddWithValue("$body",captured.Body);replace.Parameters.AddWithValue("$key",key);replace.Parameters.AddWithValue("$previous",previous);
                    if(replace.ExecuteNonQuery()!=1)throw new InvalidOperationException("Metadata retirement lost its previous joined capture.");continue;
                }
                if(previous!=captured.Body)throw new InvalidDataException("Join and recover the first metadata retirement before another cleanup.");continue;
            }
            using var insert=database.Connection.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value)";insert.Parameters.AddWithValue("$key",key);insert.Parameters.AddWithValue("$value",captured.Body);
            if(insert.ExecuteNonQuery()!=1)throw new InvalidOperationException("Metadata retirement intent did not commit exactly one row.");
        }
        reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();
    }
    internal void Complete(TakeoutMetadataRetirement captured)
    {
        var actual=Last()??throw new InvalidDataException("Metadata retirement lost its active native intent.");
        if(actual.Body!=captured.Body||Read(captured.Ready)?.Body!=captured.Body)throw new InvalidDataException("Metadata retirement changed its immutable intent.");
        using var complete=database.Connection.CreateCommand();complete.CommandText="UPDATE processor_meta SET value=$done WHERE key=$key AND (value=$body OR value=$done)";complete.Parameters.AddWithValue("$key",Active);complete.Parameters.AddWithValue("$body",captured.Body);complete.Parameters.AddWithValue("$done",Done+captured.Body);
        if(complete.ExecuteNonQuery()!=1)throw new InvalidOperationException("Metadata retirement completion lost its exact pending identity.");
    }
    private TakeoutMetadataRetirement Decode(string raw,TakeoutVerifiedMetadata? ready)
    {
        try
        {
            using var json=JsonDocument.Parse(raw);var root=json.RootElement;var backup=root.GetProperty("backup");var scope=backup.GetProperty("Scope");
            ready??=new TakeoutMetadataJobRepository(database).ReadCompletedImported(root.GetProperty("importId").GetGuid(),root.GetProperty("sidecarKey").GetString()!)
                ??throw new InvalidDataException("Metadata retirement lost its actual native ready result.");
            var actual=Capture(ready,new(scope.GetProperty("AccountId").GetString()!,scope.GetProperty("FolderId").GetString()!),backup.GetProperty("PublicationId").GetGuid());
            if(actual.Body!=raw)throw new InvalidDataException("Metadata retirement changed its closed schema, native result or durable raw bytes.");
            return actual;
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {throw new InvalidDataException("Metadata retirement intent is invalid; preserve every private file.",error);}
    }
    private string? ReadValue(string key)
    {
        using var read=database.Connection.CreateCommand();read.CommandText="SELECT length(value),value FROM processor_meta WHERE key=$key";read.Parameters.AddWithValue("$key",key);using var row=read.ExecuteReader();
        if(!row.Read())return null;if(row.GetInt64(0) is <1 or >32768)throw new InvalidDataException("Metadata retirement intent exceeded its bounded schema.");return row.GetString(1);
    }
}
