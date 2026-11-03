using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Retains only observations derived from actual ready metadata, never
/// rewrites a Drive row or original receipt. Each immutable body is hash-bound to
/// its indexed asset/job key. A frozen catalog reads these under its own snapshot.</summary>
public sealed class TakeoutImportedPhotoFactsRepository(ProcessorStateDatabase database)
{
    internal const string Prefix="takeout-photo-observation-v1:";
    private readonly ProcessorStateDatabase _database=database??throw new ArgumentNullException(nameof(database));
    /// <summary>Versioned local admission hint only. Old finished progress must
    /// still replay retained ready evidence once after this profile is introduced.
    /// This marker creates no fact, job, backup receipt or publication authority.</summary>
    public bool HasObservedImport(Guid importId)
    {
        var expected=ImportMarker(importId);using var query=_database.Connection.CreateCommand();query.CommandText="SELECT value FROM processor_meta WHERE key=$key";query.Parameters.AddWithValue("$key",ImportMarkerKey(importId));
        if(query.ExecuteScalar() is not string value)return false;
        if(value!=expected)throw new InvalidDataException("Imported observation pass changed its captured index or selection.");return true;
    }
    public void CompleteObservationPass(Guid importId)
    {
        var expected=ImportMarker(importId);if(HasObservedImport(importId))return;
        using var insert=_database.Connection.CreateCommand();insert.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value)";insert.Parameters.AddWithValue("$key",ImportMarkerKey(importId));insert.Parameters.AddWithValue("$value",expected);
        if(insert.ExecuteNonQuery()!=1)throw new InvalidOperationException("Imported observation pass did not retain its admission hint.");
    }
    private string ImportMarker(Guid importId)
    {
        var completed=new TakeoutImportInventoryRepository(_database).ReadCompleted(importId)??throw new InvalidOperationException("Observe only a completed native inventory.");
        return Encoding.UTF8.GetString(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {format="mpa-local-imported-observation-pass",major=1,importId=importId.ToString("D"),indexSha256=completed.Result.Sha256,selectionSha256=Hash(Encoding.UTF8.GetBytes(completed.CapturedContext))})));
    }
    private static string ImportMarkerKey(Guid importId)=>"takeout-photo-import-observed-v1:"+importId.ToString("D");
    public bool Retain(TakeoutVerifiedMetadata result,TakeoutMetadataDocument verified)
    {
        ArgumentNullException.ThrowIfNull(result);ArgumentNullException.ThrowIfNull(verified);
        if(!ReferenceEquals(result.Owner,_database))throw new InvalidOperationException("Imported photo observations belong to another native state owner.");
        var facts=result.DescribeImportedFacts(verified);var bytes=facts.Encode();var body=Encoding.UTF8.GetString(bytes);var key=Prefix+facts.AssetId.ToString("D")+":"+facts.JobId.ToString("D")+":";
        var actualKey=key+Hash(bytes);
        using var transaction=_database.Connection.BeginTransaction();
        using(var query=_database.Connection.CreateCommand())
        {
            query.Transaction=transaction;query.CommandText="SELECT key,value FROM processor_meta WHERE key>=$start AND key<$end ORDER BY key LIMIT 2";
            query.Parameters.AddWithValue("$start",key+"0");query.Parameters.AddWithValue("$end",key+"g");using var rows=query.ExecuteReader();
            if(rows.Read())
            {
                if(rows.GetString(0)!=actualKey||rows.GetString(1)!=body||rows.Read())throw new InvalidDataException("Imported photo observations changed their first retained body or job binding.");
                rows.Dispose();transaction.Commit();return false;
            }
        }
        using(var insert=_database.Connection.CreateCommand())
        {
            insert.Transaction=transaction;insert.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$body)";insert.Parameters.AddWithValue("$key",actualKey);insert.Parameters.AddWithValue("$body",body);
            if(insert.ExecuteNonQuery()!=1)throw new InvalidOperationException("Imported photo observations did not retain their exact body.");
        }
        // This is the local catalog observation counter, not a Drive file version.
        // Including analysis changes in the same snapshot counter invalidates the
        // existing captured-input scheduler without inventing provider revisions.
        using(var revision=_database.Connection.CreateCommand())
        {
            revision.Transaction=transaction;revision.CommandText="UPDATE discovery_revision SET revision=revision+1 WHERE id=1 AND revision<9223372036854775807";
            if(revision.ExecuteNonQuery()!=1)throw new InvalidDataException("Imported observations lost their bounded local catalog revision.");
        }
        transaction.Commit();return true;
    }
    internal static DriveCatalogAsset Apply(SqliteConnection connection,SqliteTransaction transaction,Guid library,DriveCatalogAsset asset,CancellationToken token)
    {
        var prefix=Prefix+asset.Id.ToString("D")+":";ImportedPhotoMetadata? first=null;long count=0;
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);using var query=connection.CreateCommand();query.Transaction=transaction;
        query.CommandText="SELECT key,value FROM processor_meta WHERE key>=$start AND key<$end ORDER BY key";query.Parameters.AddWithValue("$start",prefix+"0");query.Parameters.AddWithValue("$end",prefix+"g");
        using var rows=query.ExecuteReader();while(rows.Read())
        {
            token.ThrowIfCancellationRequested();var key=rows.GetString(0);var raw=rows.GetString(1);if(raw.Length>ImportedPhotoMetadata.MaximumEncodedBytes)throw new InvalidDataException("Retained imported observations exceeded their fixed body bound.");
            var bytes=Encoding.UTF8.GetBytes(raw);ImportedPhotoMetadata facts;
            try{facts=ImportedPhotoMetadata.Read(bytes);}
            catch(Exception error)when(error is ProtocolFormatException or ProtocolCompatibilityException)
            {throw new InvalidDataException("Retained imported photo observations changed their canonical body.",error);}
            if(facts.AssetId!=asset.Id||key!=prefix+facts.JobId.ToString("D")+":"+Hash(bytes))throw new InvalidDataException("Imported photo observations lost their indexed body or canonical asset binding.");
            count=checked(count+1);if(count==1)first=facts;else first=null;
            digest.AppendData(Encoding.UTF8.GetBytes(key));digest.AppendData("\n"u8);
        }
        if(count==0)return asset;var sha=Convert.ToHexStringLower(digest.GetHashAndReset());
        if(count>1)return asset with{ImportedAttention=new(count,sha,"conflicting-metadata")};
        if(first!.OriginalVersion!=asset.DriveVersion)return asset with{ImportedAttention=new(1,sha,"original-version-changed")};
        var candidate=asset with{ImportedMetadata=first};
        try{_=new DriveCatalogFacts(library).Encode(asset.Id.ToString("N")[..2],[candidate]);return candidate;}
        catch(CatalogChunkCapacityException){return asset with{ImportedAttention=new(1,sha,"metadata-too-large")};}
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
