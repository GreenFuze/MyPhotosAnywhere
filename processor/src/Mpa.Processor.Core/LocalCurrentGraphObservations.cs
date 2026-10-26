using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record LocalCurrentGraphMetadata(long Version,int Bytes,string MimeType,string? SharedDriveId,string ParentsSha256);
/// <summary>Pending actual metadata and byte high-water. ReferenceSha256 is an
/// expected signed reference; BodySha256 is separately retained actual complete data,
/// including a mismatch. Neither is a graph/activation or original-write grant.</summary>
public sealed record LocalCurrentGraphObservation(Guid AttemptId,string ConnectionSha256,CatalogObjectReference Reference,
    LocalCurrentGraphMetadata Metadata,long? BodyVersion,string? BodySha256)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public long? RejectedPolicyVersion {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? RejectedPolicySha256 {get;init;}
}

/// <summary>Private bounded observations for current-selected graph reads. Borrow one
/// serialized database transaction; no network, media body duplication, signing-counter
/// adoption or legacy inventory completion occurs. Missing/corrupt paired rows fail
/// closed. Every callback requires the still-captured Root nonce and current access.</summary>
public sealed class LocalCurrentGraphObservations(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database=database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private const string Prefix="local_current_graph_v1:object:";
    private const int MaximumRecordBytes=8192;
    private static readonly JsonSerializerOptions RecordJson=new() {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    public LocalCurrentGraphObservation RememberMetadata(LocalCurrentControlAttempt attempt,CatalogObjectReference reference,
        LocalCurrentGraphMetadata metadata,CancellationToken token=default)
    {
        Validate(reference,metadata);using var transaction=_database.Connection.BeginTransaction();
        var pin=Require(attempt,reference,metadata,transaction,token);var key=Key(pin,reference.FileId);var old=Read(key,pin,reference,transaction);
        var context=new WorkspaceObservationContext(pin.LibraryId,pin.Workspace.AccountId,pin.Workspace.FolderId,attempt.AccountRevision,attempt.SharedDriveId);
        var legacy=new WorkspaceObservationRepository(_database,_budget).ReadOne(context,reference.FileId,transaction);
        if(legacy is not null && (metadata.Version<legacy.Version || reference.Bytes!=legacy.Bytes || reference.Sha256!=legacy.Sha256 ||
            (legacy.MimeType ?? "application/json")!=metadata.MimeType)) throw new WorkspaceObservationConflictException();
        if(old is not null && (metadata.Version<old.Metadata.Version || old.Metadata.MimeType!=metadata.MimeType || old.Metadata.SharedDriveId!=metadata.SharedDriveId ||
            metadata.Version==old.Metadata.Version && metadata!=old.Metadata)) throw new WorkspaceObservationConflictException();
        var next=new LocalCurrentGraphObservation(attempt.Id,pin.DocumentSha256,reference,metadata,old?.BodyVersion,old?.BodySha256)
            {RejectedPolicyVersion=old?.RejectedPolicyVersion,RejectedPolicySha256=old?.RejectedPolicySha256};
        if(next!=old) Save(key,next,old is null,transaction);
        transaction.Commit();return next;
    }

    public LocalCurrentGraphObservation RememberBytes(LocalCurrentControlAttempt attempt,LocalCurrentGraphObservation expected,
        string actualSha256,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if(!Digest(actualSha256)) throw new ArgumentException("An actual complete lowercase SHA-256 is required.",nameof(actualSha256));
        using var transaction=_database.Connection.BeginTransaction();var pin=Require(attempt,expected.Reference,expected.Metadata,transaction,token);
        var key=Key(pin,expected.Reference.FileId);var old=Read(key,pin,expected.Reference,transaction);
        if(expected.AttemptId!=attempt.Id || old is null || old!=expected || old.BodyVersion==old.Metadata.Version && old.BodySha256!=actualSha256)
            throw new WorkspaceObservationConflictException();
        var next=old with {BodyVersion=old.Metadata.Version,BodySha256=actualSha256};
        if(next!=old) Save(key,next,false,transaction);
        transaction.Commit();return next;
    }

    public LocalCurrentGraphObservation? Current(LocalCurrentControlAttempt attempt,CatalogObjectReference reference,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(reference);using var transaction=_database.Connection.BeginTransaction();
        var pin=new LocalCurrentControlObservations(_database,_budget).AssertCapturedIn(attempt,transaction,token);
        CheckReference(pin,reference);var result=Read(Key(pin,reference.FileId),pin,reference,transaction);
        if(result is not null && result.Metadata.SharedDriveId!=attempt.SharedDriveId) throw new InvalidDataException("Graph observations lost their retained corpus binding.");
        transaction.Commit();return result;
    }
    /// <summary>Retain complete rejected policy transport bytes separately from an
    /// admitted immutable body. A later exact signed-reference read may recover, but
    /// cannot erase the rejection or lower its observed metadata high-water.</summary>
    public LocalCurrentGraphObservation RememberRejectedPolicyBytes(LocalCurrentControlAttempt attempt,LocalCurrentGraphObservation expected,
        string actualSha256,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if(!Digest(actualSha256) || actualSha256==expected.Reference.Sha256) throw new ArgumentException("A rejected actual policy digest is required.",nameof(actualSha256));
        using var transaction=_database.Connection.BeginTransaction();
        var pin=new LocalCurrentControlObservations(_database,_budget).AssertCapturedPolicyIn(attempt,expected.Reference,transaction,token);
        var key=Key(pin,expected.Reference.FileId);var old=Read(key,pin,expected.Reference,transaction);
        if(old!=expected || expected.AttemptId!=attempt.Id || old!.Metadata.MimeType!="application/json" ||
            old.RejectedPolicyVersion==old.Metadata.Version && old.RejectedPolicySha256!=actualSha256) throw new WorkspaceObservationConflictException();
        var next=old with {RejectedPolicyVersion=old.Metadata.Version,RejectedPolicySha256=actualSha256};
        if(next!=old) Save(key,next,false,transaction);transaction.Commit();return next;
    }

    private LibraryConnectionDescriptor Require(LocalCurrentControlAttempt attempt,CatalogObjectReference reference,LocalCurrentGraphMetadata metadata,
        SqliteTransaction transaction,CancellationToken token)
    {
        Validate(reference,metadata);
        var pin=new LocalCurrentControlObservations(_database,_budget).AssertCapturedIn(attempt,transaction,token);
        CheckReference(pin,reference);
        if(metadata.SharedDriveId!=attempt.SharedDriveId) throw new CurrentControlObservationConflictException();return pin;
    }
    internal LocalCurrentGraphObservation RequireCompleteIn(LocalCurrentControlAttempt fenced,LibraryConnectionDescriptor pin,
        CatalogObjectReference reference,SqliteTransaction transaction)
    {
        CheckReference(pin,reference);var actual=Read(Key(pin,reference.FileId),pin,reference,transaction);
        if(fenced.Phase!="fenced" || actual is null || actual.AttemptId!=fenced.Id || actual.Metadata.SharedDriveId!=fenced.SharedDriveId ||
            actual.BodyVersion!=actual.Metadata.Version || actual.BodySha256!=reference.Sha256) throw new CurrentControlObservationConflictException();
        return actual;
    }
    private static void CheckReference(LibraryConnectionDescriptor pin,CatalogObjectReference reference)
    {
        if(!Opaque(reference.FileId) || reference.Bytes is <1 or >CatalogChunkDecoder.MaximumCompressedBytes || !Digest(reference.Sha256))
            throw new ArgumentException("A bounded concrete immutable reference is required.",nameof(reference));
        if(reference.AccountId!=pin.Workspace.AccountId || reference.WorkspaceId!=pin.Workspace.FolderId ||
            reference.FileId==pin.CurrentFileId || reference.FileId==pin.Workspace.FolderId) throw new CurrentControlObservationConflictException();
    }
    private static void Validate(CatalogObjectReference reference,LocalCurrentGraphMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(reference);ArgumentNullException.ThrowIfNull(metadata);
        var limit=metadata.MimeType switch {"application/json"=>ProtocolDocumentReader.MaximumDocumentBytes,
            "application/gzip" or "application/octet-stream"=>CatalogChunkDecoder.MaximumCompressedBytes,_=>0};
        if(!Opaque(reference.AccountId) || !Opaque(reference.WorkspaceId) || !Opaque(reference.FileId) || reference.Bytes<1 || reference.Bytes>limit ||
            !Digest(reference.Sha256) || metadata.Version<1 || metadata.Bytes!=reference.Bytes || !Digest(metadata.ParentsSha256) ||
            metadata.SharedDriveId is not null && !Opaque(metadata.SharedDriveId)) throw new ArgumentException("A bounded captured immutable reference and actual scope/metadata are required.");
    }
    private LocalCurrentGraphObservation? Read(string key,LibraryConnectionDescriptor pin,CatalogObjectReference reference,SqliteTransaction transaction)
    {
        var body=ReadValue(key,MaximumRecordBytes,transaction);var hash=ReadValue(key+":sha256",64,transaction);
        if(body is null && hash is null) return null;
        if(body is null || Hash(Encoding.UTF8.GetBytes(body))!=hash) throw new InvalidDataException("Current graph observations lost their complete paired integrity record.");
        LocalCurrentGraphObservation value;
        try {value=JsonSerializer.Deserialize<LocalCurrentGraphObservation>(body,RecordJson) ?? throw new InvalidDataException("Missing graph observation.");}
        catch(JsonException error){throw new InvalidDataException("Graph observation cannot be decoded.",error);}
        if(value.Reference is null || value.Metadata is null || value.AttemptId==Guid.Empty || value.ConnectionSha256!=pin.DocumentSha256 ||
            value.Reference.FileId!=reference.FileId || (value.BodyVersion is null)!=(value.BodySha256 is null) ||
            value.BodyVersion is not null && (value.BodyVersion<1 || value.BodyVersion>value.Metadata.Version || !Digest(value.BodySha256)) ||
            (value.RejectedPolicyVersion is null)!=(value.RejectedPolicySha256 is null) || value.RejectedPolicyVersion is not null &&
            (value.RejectedPolicyVersion<1 || value.RejectedPolicyVersion>value.Metadata.Version || !Digest(value.RejectedPolicySha256) || value.RejectedPolicySha256==value.Reference.Sha256) || Encode(value)!=body)
            throw new InvalidDataException("Current graph observations changed their fixed reference, scope or canonical state.");
        try {Validate(value.Reference,value.Metadata);}
        catch(ArgumentException error){throw new InvalidDataException("Retained graph observation metadata exceeds its schema or bounds.",error);}
        if(value.Reference!=reference) throw new WorkspaceObservationConflictException();return value;
    }
    private void Save(string key,LocalCurrentGraphObservation value,bool added,SqliteTransaction transaction)
    {
        if(added)
        {
            using var count=Command(transaction,"SELECT count(*) FROM processor_meta WHERE key GLOB $prefix AND key NOT LIKE '%:sha256'",("$prefix",Prefix+"*"));
            if((long)count.ExecuteScalar()!>=WorkspaceObservationRepository.MaximumDocuments) throw new CatalogTraversalLimitException();
        }
        var text=Encode(value);var bytes=Encoding.UTF8.GetBytes(text);using var reservation=_budget.Reserve(checked(bytes.Length*3L+65536));
        reservation.RecheckBeforeWrite(reservation.Bytes);Put(key,text,transaction);Put(key+":sha256",Hash(bytes),transaction);
    }
    private string? ReadValue(string key,int limit,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"SELECT length(CAST(value AS BLOB)),value FROM processor_meta WHERE key=$key",("$key",key));using var reader=command.ExecuteReader();
        if(!reader.Read()) return null;
        if(reader.GetInt64(0) is <1 || reader.GetInt64(0)>limit) throw new InvalidDataException("A graph observation exceeds its retained UTF8 byte bound.");return reader.GetString(1);
    }
    private void Put(string key,string value,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",key),("$value",value));
        if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("A graph checkpoint did not persist exactly one row.");
    }
    private SqliteCommand Command(SqliteTransaction transaction,string sql,params(string Key,object Value)[] values)
    {var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;foreach(var pair in values) command.Parameters.AddWithValue(pair.Key,pair.Value);return command;}
    private static string Encode(LocalCurrentGraphObservation value)=>Encoding.UTF8.GetString(new CanonicalJson(MaximumRecordBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(value,RecordJson)));
    private static string Key(LibraryConnectionDescriptor pin,string file)=>Prefix+Hash(Encoding.UTF8.GetBytes(pin.DocumentSha256+"\n"+file));
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Digest(string? value)=>value is {Length:64} && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Opaque(string? value)=>value is {Length:>0 and <=256} && value!="root" && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
