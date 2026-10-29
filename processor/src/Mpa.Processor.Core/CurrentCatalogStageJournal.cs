using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record CurrentCatalogStageDescriptor(Guid Id,long Bytes,string Sha256,long Assets,int Leaves);
/// <summary>Immutable captured identity plus historical preparation/sealing state.
/// A persisted claim is never fresh authority, an active catalog or an original-write grant.</summary>
public sealed record CurrentCatalogStageClaim(Guid Id,Guid PreparedAttempt,string ConnectionCode,string RootSha256,
    long PreparedVersion,long AccountRevision,string? SharedDriveId,string Phase,CurrentCatalogStageDescriptor? Descriptor);

/// <summary>Borrow the serialized main database. A claim commits before file creation.
/// Fresh verification may resume the SAME identity only for the exact original Root
/// bytes/scope; its original nonce/access revision are retained rather than relabeled.</summary>
public sealed class CurrentCatalogStageJournal(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    public const int MaximumStages=2;
    private const string Prefix="current_catalog_stage_v1:";
    private readonly ProcessorStateDatabase _database=database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private static readonly JsonSerializerOptions Json=new() {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    public CurrentCatalogStageClaim Begin(LocalCurrentControlAttempt attempt,Guid id,CancellationToken token=default)
    {
        if(id==Guid.Empty) throw new ArgumentException("A fixed private stage identity is required.",nameof(id));
        using var transaction=_database.Connection.BeginTransaction();var pin=new LocalCurrentControlObservations(_database,_budget).AssertCapturedIn(attempt,transaction,token);
        var claims=ReadAll(transaction);var old=claims.SingleOrDefault(value=>value.Id==id);
        if(old is not null) {AssertScope(old,attempt,pin);if(old.Phase=="retiring") throw new CurrentControlObservationConflictException();transaction.Commit();return old;}
        if(claims.Count==MaximumStages) throw new CatalogPreparationCapacityException();
        var claim=new CurrentCatalogStageClaim(id,attempt.Id,pin.Encode(),attempt.ContentSha256,attempt.Version!.Value,attempt.AccountRevision,attempt.SharedDriveId,"preparing",null);
        Save(claim,transaction);transaction.Commit();return claim;
    }
    public IReadOnlyList<CurrentCatalogStageClaim> ReadAll()
    {using var transaction=_database.Connection.BeginTransaction();var result=ReadAll(transaction);transaction.Commit();return result;}
    internal CurrentCatalogStageClaim AssertSealedIn(CurrentCatalogStageClaim expected,LocalCurrentControlAttempt fenced,
        LibraryConnectionDescriptor pin,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(expected);var actual=ReadAll(transaction).SingleOrDefault(value=>value.Id==expected.Id);
        if(fenced.Phase!="fenced" || actual!=expected || expected.Phase!="sealed" || expected.Descriptor is null) throw new CurrentControlObservationConflictException();
        AssertScope(expected,fenced,pin);return expected;
    }
    internal IReadOnlyList<CurrentCatalogStageClaim> ReadAllIn(SqliteTransaction transaction)=>ReadAll(transaction);
    public void AssertForCapture(CurrentCatalogStageClaim expected,LocalCurrentControlAttempt attempt,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);using var transaction=_database.Connection.BeginTransaction();
        var pin=new LocalCurrentControlObservations(_database,_budget).AssertCapturedIn(attempt,transaction,token);
        var actual=ReadAll(transaction).SingleOrDefault(value=>value.Id==expected.Id);
        if(actual is null || actual!=expected || actual.Phase=="retiring") throw new CurrentControlObservationConflictException();
        AssertScope(actual,attempt,pin);transaction.Commit();
    }
    public CurrentCatalogStageClaim RecordSealed(CurrentCatalogStageClaim expected,LocalCurrentControlAttempt attempt,CurrentCatalogStageDescriptor descriptor,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);ValidateDescriptor(descriptor);using var transaction=_database.Connection.BeginTransaction();
        var pin=new LocalCurrentControlObservations(_database,_budget).AssertCapturedIn(attempt,transaction,token);
        var actual=ReadAll(transaction).SingleOrDefault(value=>value.Id==expected.Id);
        if(actual is null || actual!=expected || actual.Phase=="retiring" || descriptor.Id!=actual.Id || actual.Descriptor is not null && actual.Descriptor!=descriptor)
            throw new CurrentControlObservationConflictException();
        AssertScope(actual,attempt,pin);var next=actual with {Phase="sealed",Descriptor=descriptor};Save(next,transaction);transaction.Commit();return next;
    }
    public CurrentCatalogStageClaim BeginRetirement(CurrentCatalogStageClaim expected)
    {
        ArgumentNullException.ThrowIfNull(expected);using var transaction=_database.Connection.BeginTransaction();
        var actual=ReadAll(transaction).SingleOrDefault(value=>value.Id==expected.Id);
        if(actual is null || actual!=expected) throw new CurrentControlObservationConflictException();
        // Future activation must retain the active claim. Unknown active-pointer data
        // cannot be interpreted as permission to remove a generated file.
        new CurrentCatalogActivationRepository(_database,_budget).AssertNotActiveIn(expected.Id,transaction);
        var next=actual with {Phase="retiring"};Save(next,transaction);transaction.Commit();return next;
    }
    public void CompleteRetirement(CurrentCatalogStageClaim expected,CurrentCatalogStageRetirement? retirement=null)
    {
        ArgumentNullException.ThrowIfNull(expected);using var transaction=_database.Connection.BeginTransaction();
        if(expected.Phase!="retiring" || ReadAll(transaction).SingleOrDefault(value=>value.Id==expected.Id)!=expected) throw new CurrentControlObservationConflictException();
        new CurrentCatalogActivationRepository(_database,_budget).AssertNotActiveIn(expected.Id,transaction);
        if(retirement is null) CurrentCatalogStageArea.AssertRetired(_database.StateDirectory,_database.InstallationId,expected.Id);
        else retirement.AssertFor(_database.StateDirectory,_database.InstallationId,expected);
        using var delete=Command(transaction,"DELETE FROM processor_meta WHERE key=$body OR key=$hash",("$body",Key(expected.Id)),("$hash",Key(expected.Id)+":sha256"));
        if(delete.ExecuteNonQuery()!=2) throw new InvalidOperationException("Retired staging claim lost its complete pair.");transaction.Commit();
    }
    internal static byte[] Identity(CurrentCatalogStageClaim claim)=>Encoding.UTF8.GetBytes(Encode(claim with {Phase="preparing",Descriptor=null}));
    internal static void ValidateDescriptor(CurrentCatalogStageDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(value.Id==Guid.Empty || value.Bytes is <4096 or >CatalogGenerationCaptureRepository.MaximumSnapshotBytes || !Digest(value.Sha256) || value.Assets<0 ||
            value.Leaves is <0 or >WorkspaceObservationRepository.MaximumDocuments || value.Leaves>value.Assets || value.Assets>value.Leaves*(long)DriveCatalogFacts.MaximumAssets || (value.Assets==0)!=(value.Leaves==0))
            throw new ArgumentException("A complete bounded private stage descriptor is required.",nameof(value));
    }
    private static void AssertScope(CurrentCatalogStageClaim claim,LocalCurrentControlAttempt attempt,LibraryConnectionDescriptor pin)
    {
        if(claim.ConnectionCode!=pin.Encode() || claim.RootSha256!=attempt.ContentSha256 || claim.PreparedVersion>attempt.Version!.Value ||
            claim.AccountRevision>attempt.AccountRevision || claim.SharedDriveId!=attempt.SharedDriveId) throw new CurrentControlObservationConflictException();
    }
    private System.Collections.ObjectModel.ReadOnlyCollection<CurrentCatalogStageClaim> ReadAll(SqliteTransaction transaction)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        using(var command=Command(transaction,"SELECT key,length(CAST(value AS BLOB)),value FROM processor_meta WHERE key GLOB $prefix ORDER BY key LIMIT 5",("$prefix",Prefix+"*")))
        using(var reader=command.ExecuteReader()) while(reader.Read())
        {
            if(values.Count==MaximumStages*2 || reader.GetInt64(1) is <1 or >8192) throw new InvalidDataException("Current stage claims exceed their bounded complete inventory.");
            values.Add(reader.GetString(0),reader.GetString(2));
        }
        var result=new List<CurrentCatalogStageClaim>();
        foreach(var pair in values.Where(pair=>!pair.Key.EndsWith(":sha256",StringComparison.Ordinal)))
        {
            var suffix=pair.Key[Prefix.Length..];
            if(!Guid.TryParseExact(suffix,"N",out var id) || id==Guid.Empty || id.ToString("N")!=suffix || !values.TryGetValue(pair.Key+":sha256",out var hash) || Hash(Encoding.UTF8.GetBytes(pair.Value))!=hash)
                throw new InvalidDataException("A staging claim lost its identity or paired digest.");
            CurrentCatalogStageClaim claim;
            try
            {
                claim=JsonSerializer.Deserialize<CurrentCatalogStageClaim>(pair.Value,Json) ?? throw new InvalidDataException("Missing stage claim.");
                if(LibraryConnectionDescriptor.Decode(claim.ConnectionCode).Encode()!=claim.ConnectionCode) throw new InvalidDataException("A stage claim changed its canonical pin.");
                if(claim.Descriptor is not null) ValidateDescriptor(claim.Descriptor);
            }
            catch(Exception error) when(error is JsonException or ArgumentException or ProtocolFormatException)
            {throw new InvalidDataException("A staging claim cannot be decoded safely.",error);}
            if(claim.Id!=id || claim.PreparedAttempt==Guid.Empty || claim.PreparedVersion<1 || claim.AccountRevision<1 || !Digest(claim.RootSha256) ||
                claim.SharedDriveId is not null && !Opaque(claim.SharedDriveId) || claim.Phase is not ("preparing" or "sealed" or "retiring") || claim.Phase=="preparing" && claim.Descriptor is not null ||
                claim.Phase=="sealed" && claim.Descriptor is null || claim.Descriptor is not null && claim.Descriptor.Id!=id || Encode(claim)!=pair.Value)
                throw new InvalidDataException("A staging claim changed its captured identity or phase.");
            result.Add(claim);
        }
        if(result.Count*2!=values.Count) throw new InvalidDataException("Staging history lost a complete body/hash pair.");return result.AsReadOnly();
    }
    private void Save(CurrentCatalogStageClaim value,SqliteTransaction transaction)
    {
        var text=Encode(value);using var reservation=_budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        Put(Key(value.Id),text,transaction);Put(Key(value.Id)+":sha256",Hash(Encoding.UTF8.GetBytes(text)),transaction);
    }
    private void Put(string key,string value,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",key),("$value",value));
        if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("A stage claim did not persist exactly one row.");
    }
    private SqliteCommand Command(SqliteTransaction transaction,string sql,params (string Key,object Value)[] values)
    {var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;foreach(var pair in values) command.Parameters.AddWithValue(pair.Key,pair.Value);return command;}
    private static string Key(Guid id)=>Prefix+id.ToString("N");
    private static string Encode(CurrentCatalogStageClaim value)=>Encoding.UTF8.GetString(new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(value,Json)));
    private static string Hash(ReadOnlySpan<byte> value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    private static bool Opaque(string value)=>value is {Length:>0 and <=256} && value!="root" && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool Digest(string? value)=>value is {Length:64} && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
