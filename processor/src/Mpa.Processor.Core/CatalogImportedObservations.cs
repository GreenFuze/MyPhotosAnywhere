using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record CatalogImportedObservation(string Key,ImportedPhotoMetadata Metadata);
public sealed record CatalogImportedObservationPage(IReadOnlyList<CatalogImportedObservation> Observations,bool HasMore);

internal static class CatalogImportedObservationCodec
{
    internal static CatalogImportedObservation Read(string key,byte[] bytes,Guid assetId)
    {
        if(bytes.Length is <1 or >ImportedPhotoMetadata.MaximumEncodedBytes)throw new InvalidDataException("Imported observation exceeds its fixed body bound.");
        ImportedPhotoMetadata facts;
        try{facts=ImportedPhotoMetadata.Read(bytes);}
        catch(Exception error)when(error is ProtocolFormatException or ProtocolCompatibilityException)
        {throw new InvalidDataException("Imported observation lost its exact canonical body.",error);}
        var expected=TakeoutImportedPhotoFactsRepository.Prefix+assetId.ToString("D")+":"+facts.JobId.ToString("D")+":"+Convert.ToHexStringLower(SHA256.HashData(bytes));
        if(assetId==Guid.Empty||facts.AssetId!=assetId||key!=expected)throw new InvalidDataException("Imported observation changed its native asset/job/body binding.");
        return new(key,facts);
    }
}

public sealed partial class CatalogFactsSnapshot
{
    /// <summary>All observations for one visible captured asset, including conflicts.
    /// Uses the same held WAL observation as facts; never queries later mutable state.</summary>
    internal CatalogImportedObservationPage ReadImportedObservations(Guid assetId,string? after,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_closed,this);token.ThrowIfCancellationRequested();
        var prefix=TakeoutImportedPhotoFactsRepository.Prefix+assetId.ToString("D")+":";
        using var query=Command("SELECT key,length(CAST(value AS BLOB)),value FROM processor_meta WHERE key>=$start AND key<$end AND key>$after ORDER BY key LIMIT 17");
        query.Parameters.AddWithValue("$start",prefix+"0");query.Parameters.AddWithValue("$end",prefix+"g");query.Parameters.AddWithValue("$after",after??prefix);
        var result=new List<CatalogImportedObservation>(16);var more=false;using var rows=query.ExecuteReader();
        while(rows.Read())
        {
            token.ThrowIfCancellationRequested();if(result.Count==16){more=true;break;}
            if(rows.GetInt64(1) is <1 or >ImportedPhotoMetadata.MaximumEncodedBytes)throw new InvalidDataException("Imported observation exceeds its fixed body bound.");
            result.Add(CatalogImportedObservationCodec.Read(rows.GetString(0),Encoding.UTF8.GetBytes(rows.GetString(2)),assetId));
        }
        return new(result.AsReadOnly(),more);
    }
}

public sealed partial class FrozenCatalogSnapshot
{
    // Null identifies an older immutable pending snapshot. Its selected-facts-only
    // behavior stays intact; never retrofit later observations into that file.
    public long? ImportedObservationCount{get;private set;}=0;

    private void CaptureImportedObservations(CatalogFactsSnapshot source,DriveCatalogAsset asset,SqliteTransaction transaction,CancellationToken token)
    {
        using var insert=_connection!.CreateCommand();insert.Transaction=transaction;
        insert.CommandText="INSERT INTO imported_observation(key,asset_id,payload,sha256) VALUES($key,$asset,$payload,$hash)";
        var key=insert.Parameters.Add("$key",SqliteType.Text);var id=insert.Parameters.Add("$asset",SqliteType.Text);var payload=insert.Parameters.Add("$payload",SqliteType.Blob);var hash=insert.Parameters.Add("$hash",SqliteType.Text);
        string? after=null;long count=0;ImportedPhotoMetadata? first=null;using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while(true)
        {
            var page=source.ReadImportedObservations(asset.Id,after,token);_storage!.RecheckBeforeWrite(_storage.Bytes);
            foreach(var observation in page.Observations)
            {
                token.ThrowIfCancellationRequested();var bytes=observation.Metadata.Encode();key.Value=observation.Key;id.Value=asset.Id.ToString("D");payload.Value=bytes;hash.Value=Hash(bytes);
                if(insert.ExecuteNonQuery()!=1)throw new InvalidDataException("Frozen imported observation was not retained exactly once.");
                count=checked(count+1);ImportedObservationCount=checked(ImportedObservationCount!.Value+1);first=count==1?observation.Metadata:null;
                digest.AppendData(Encoding.UTF8.GetBytes(observation.Key));digest.AppendData("\n"u8);
            }
            if(!page.HasMore)break;if(page.Observations.Count==0)throw new InvalidDataException("Imported observation capture did not advance.");after=page.Observations[^1].Key;
        }
        if(asset.ImportedMetadata is{} selected)
        {
            if(count!=1||first is null||!first.Encode().AsSpan().SequenceEqual(selected.Encode()))throw new InvalidDataException("Frozen selected metadata differs from its native observation.");
        }
        else if(asset.ImportedAttention is{} attention)
        {
            if(count!=attention.Candidates||Convert.ToHexStringLower(digest.GetHashAndReset())!=attention.Sha256)throw new InvalidDataException("Frozen metadata attention differs from its native observation set.");
        }
        else if(count!=0)throw new InvalidDataException("Frozen photo omitted its native imported observations.");
    }

    public CatalogImportedObservationPage ReadImportedObservations(string? after=null,CancellationToken token=default)
    {
        ObjectDisposedException.ThrowIf(_closed,this);token.ThrowIfCancellationRequested();
        if(ImportedObservationCount is null)throw new InvalidOperationException("This older immutable snapshot contains selected metadata only.");
        using var query=_connection!.CreateCommand();query.CommandText="SELECT o.key,o.asset_id,length(o.payload),o.payload,o.sha256 FROM imported_observation o JOIN fact f ON f.id=o.asset_id WHERE o.key>$after ORDER BY o.key LIMIT 17";
        query.Parameters.AddWithValue("$after",after??"");var result=new List<CatalogImportedObservation>(16);var more=false;using var rows=query.ExecuteReader();
        while(rows.Read())
        {
            token.ThrowIfCancellationRequested();if(result.Count==16){more=true;break;}
            if(rows.GetInt64(2) is <1 or >ImportedPhotoMetadata.MaximumEncodedBytes)throw new InvalidDataException("Frozen imported observation exceeds its fixed body bound.");
            var bytes=rows.GetFieldValue<byte[]>(3);var text=rows.GetString(1);
            if(!Guid.TryParseExact(text,"D",out var asset)||asset.ToString("D")!=text||Hash(bytes)!=rows.GetString(4))throw new InvalidDataException("Frozen imported observation identity or digest failed.");
            result.Add(CatalogImportedObservationCodec.Read(rows.GetString(0),bytes,asset));
        }
        return new(result.AsReadOnly(),more);
    }
}
