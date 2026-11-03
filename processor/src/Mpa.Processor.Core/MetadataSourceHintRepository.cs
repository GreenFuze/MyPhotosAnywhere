using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record MetadataSourceCandidate(CatalogObjectReference Bundle,long MinimumVersion);

/// <summary>Bounded discovery hints rebuilt from actual observed bundle bytes.
/// These are neither backup receipts nor source association, current availability,
/// provider permission or private-file retirement evidence. Consumers must read
/// and verify the candidate against the actual selected signed photo metadata.</summary>
public sealed class MetadataSourceHintRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private const string Prefix="metadata-source-hint-v1:";
    private const int MaximumRowBytes=32768;
    private static readonly CanonicalJson Codec=new(MaximumRowBytes);
    private readonly ProcessorStateDatabase _database=database??throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget??throw new ArgumentNullException(nameof(budget));
    public void Remember(WorkspaceObservationContext context,WorkspaceDocumentObservation observation,MetadataSourceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(context);ArgumentNullException.ThrowIfNull(observation);ArgumentNullException.ThrowIfNull(bundle);
        var payload=bundle.CopyPayload();
        if(observation.MimeType!=MetadataSourceBundle.MimeType||observation.Bytes!=payload.LongLength||observation.Sha256!=Hash(payload))
            throw new InvalidDataException("Raw-source hints changed their actual observed bundle bytes.");
        using var reservation=_budget.Reserve(2*MaximumRowBytes+65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        using var transaction=_database.Connection.BeginTransaction();var observations=new WorkspaceObservationRepository(_database,_budget);observations.AssertContext(context,transaction);
        if(observations.ReadOne(context,observation.FileId,transaction)!=observation)throw new WorkspaceObservationConflictException();
        var reference=new CatalogObjectReference(context.AccountId,observation.FileId,context.FolderId,observation.Bytes,observation.Sha256);
        var body=Encode(context.LibraryId,reference,bundle.Sources);var key=Scope(context)+observation.FileId;
        var exists=false;
        using(var read=_database.Connection.CreateCommand())
        {
            read.Transaction=transaction;read.CommandText="SELECT length(value),value FROM processor_meta WHERE key=$key";read.Parameters.AddWithValue("$key",key);using var row=read.ExecuteReader();
            if(row.Read())
            {if(row.GetInt64(0)>MaximumRowBytes||row.GetString(1)!=body)throw new InvalidDataException("The first observed raw-source index changed.");exists=true;}
        }
        if(exists){transaction.Commit();return;}
        using var count=_database.Connection.CreateCommand();count.Transaction=transaction;count.CommandText="SELECT count(*) FROM processor_meta WHERE key LIKE $scope";count.Parameters.AddWithValue("$scope",Scope(context)+"%");
        if((long)count.ExecuteScalar()!>=WorkspaceObservationRepository.MaximumDocuments)throw new InvalidDataException("Raw-source hints exceeded the complete workspace inventory bound.");
        using var write=_database.Connection.CreateCommand();write.Transaction=transaction;write.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value)";write.Parameters.AddWithValue("$key",key);write.Parameters.AddWithValue("$value",body);write.ExecuteNonQuery();
        reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();
    }
    public IReadOnlyList<MetadataSourceCandidate> Candidates(WorkspaceObservationContext context,ImportedMetadataFingerprint source,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(context);RequireSource(source);token.ThrowIfCancellationRequested();using var transaction=_database.Connection.BeginTransaction();
        var observations=new WorkspaceObservationRepository(_database,_budget);observations.AssertContext(context,transaction);
        using var query=_database.Connection.CreateCommand();query.Transaction=transaction;query.CommandText="SELECT key,length(value),value FROM processor_meta WHERE key LIKE $scope ORDER BY key LIMIT 4097";query.Parameters.AddWithValue("$scope",Scope(context)+"%");
        using var rows=query.ExecuteReader();var found=new List<MetadataSourceCandidate>();var count=0;
        while(rows.Read())
        {
            token.ThrowIfCancellationRequested();if(++count>WorkspaceObservationRepository.MaximumDocuments||rows.GetInt64(1) is <1 or >MaximumRowBytes)throw new InvalidDataException("Raw-source hints exceeded their bounded inventory schema.");
            var raw=rows.GetString(2);
            try
            {
                using var json=JsonDocument.Parse(raw);var root=json.RootElement;var values=root.GetProperty("sources");
                if(root.GetProperty("format").GetString()!="mpa-observed-raw-source-index-1"||values.GetArrayLength() is <1 or >MetadataSourceBundle.MaximumSources)throw new InvalidDataException("Invalid raw-source discovery index.");
                var reference=new CatalogObjectReference(root.GetProperty("accountId").GetString()!,root.GetProperty("fileId").GetString()!,root.GetProperty("workspaceId").GetString()!,Decimal(root,"bytes"),root.GetProperty("sha256").GetString()!);
                var sources=values.EnumerateArray().Select(row=>new ImportedMetadataFingerprint(Decimal(row,"bytes"),row.GetProperty("sha256").GetString()!)).ToArray();
                foreach(var value in sources)RequireSource(value);
                if(root.GetProperty("libraryId").GetGuid()!=context.LibraryId||reference.AccountId!=context.AccountId||reference.WorkspaceId!=context.FolderId||rows.GetString(0)!=Scope(context)+reference.FileId||
                    sources.Select(value=>value.Sha256).Distinct(StringComparer.Ordinal).Count()!=sources.Length||!sources.SequenceEqual(sources.OrderBy(value=>value.Sha256,StringComparer.Ordinal))||Encode(context.LibraryId,reference,sources)!=raw)
                    throw new InvalidDataException("Raw-source discovery changed its closed schema or workspace identity.");
                var observed=observations.ReadOne(context,reference.FileId,transaction)??throw new InvalidDataException("Raw-source discovery lost its native observation.");
                if(observed.MimeType!=MetadataSourceBundle.MimeType||observed.Bytes!=reference.Bytes||observed.Sha256!=reference.Sha256||reference.Bytes>MetadataSourceBundle.MaximumBytes)
                    throw new InvalidDataException("Raw-source discovery changed its observed bundle identity.");
                if(sources.Contains(source))found.Add(new(reference,observed.Version));
            }
            catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
            {throw new InvalidDataException("Raw-source discovery index is invalid.",error);}
        }
        token.ThrowIfCancellationRequested();transaction.Commit();return found.AsReadOnly();
    }
    private static string Scope(WorkspaceObservationContext context)=>Prefix+Hash(Encoding.UTF8.GetBytes(context.LibraryId.ToString("D")+"\0"+context.AccountId+"\0"+context.FolderId))+":";
    private static string Encode(Guid library,CatalogObjectReference reference,IEnumerable<ImportedMetadataFingerprint> sources)=>Encoding.UTF8.GetString(Codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-observed-raw-source-index-1",libraryId=library.ToString("D"),accountId=reference.AccountId,workspaceId=reference.WorkspaceId,fileId=reference.FileId,bytes=reference.Bytes.ToString(CultureInfo.InvariantCulture),sha256=reference.Sha256,
        sources=sources.Select(source=>new{bytes=source.Bytes.ToString(CultureInfo.InvariantCulture),sha256=source.Sha256}).ToArray()})));
    private static long Decimal(JsonElement root,string field)
    {var text=root.GetProperty(field).GetString();return long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value.ToString(CultureInfo.InvariantCulture)==text?value:throw new InvalidDataException("Raw-source index has a noncanonical size.");}
    private static void RequireSource(ImportedMetadataFingerprint source)
    {if(source is null||source.Bytes is <1 or >TakeoutMetadataDocument.MaximumSourceBytes||source.Sha256 is not{Length:64}||source.Sha256.Any(c=>c is not(>='a' and <='f') and not(>='0' and <='9')))throw new ArgumentException("A bounded exact raw source fingerprint is required.",nameof(source));}
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
