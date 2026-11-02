using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed record TakeoutAssociation(string Status,TakeoutCapturedEntry? Media);
public sealed record TakeoutEntryPage(ReadOnlyCollection<TakeoutCapturedEntry> Entries,bool HasMore);
internal sealed record TakeoutInventoryResult(long Bytes,string Sha256,long Entries,long Media);

/// <summary>Disk-backed multipart inventory and conservative sidecar association.
/// A durable captured job precedes files. Private entry IDs/asset IDs survive
/// replay; duplicate names/content never merge originals. No provider/human write.</summary>
public sealed partial class TakeoutAssociationCatalog : IDisposable
{
    private const string DirectoryName="takeout-associations";
    private const int ApplicationId=0x4d504154;
    private const long MaximumIndexBytes=1024L*1024*1024;
    private static readonly object AccountingGate=new();
    private readonly object _gate=new();
    private readonly TakeoutImportInventoryWork? _work;
    private readonly Guid _importId;
    private readonly ReadOnlyCollection<TakeoutInputBinding> _parts;
    private readonly StorageBudgetManager _budget;
    private readonly string _root;
    private readonly string _directory;
    private readonly string _path;
    private readonly byte[] _identity;
    private readonly HashSet<Guid> _seenParts=[];
    private SqliteConnection? _connection;
    private FileStream? _areaLease;
    private FileStream? _sealedLease;
    private TakeoutInventoryResult? _sealed;
    private int _busy;
    private bool _closed;
    public int ReusedVerifiedZipParts { get; private set; }
    private TakeoutAssociationCatalog(ProcessorStateDatabase database,TakeoutImportInventoryWork work,StorageBudgetManager budget)
        :this(database.StateDirectory,database.InstallationId,work.Lease.JobId,work.Lease.CapturedContext,work.Parts,budget,work) { }
    private TakeoutAssociationCatalog(string stateDirectory,Guid installation,Guid importId,string context,ReadOnlyCollection<TakeoutInputBinding> parts,StorageBudgetManager budget,TakeoutImportInventoryWork? work)
    {
        _work=work;_importId=importId;_parts=parts;_budget=budget;_root=Path.Combine(stateDirectory,DirectoryName);
        _directory=Path.Combine(_root,"import-"+importId.ToString("N"));_path=Path.Combine(_directory,"inventory.sqlite");_identity=TakeoutImportInventoryRepository.Identity(importId,context,installation);
    }
    public static TakeoutAssociationCatalog Open(ProcessorStateDatabase database,TakeoutImportInventoryWork work,StorageBudgetManager budget,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(database);ArgumentNullException.ThrowIfNull(work);ArgumentNullException.ThrowIfNull(budget);
        new TakeoutImportInventoryRepository(database).AssertCurrent(work);var owner=new TakeoutAssociationCatalog(database,work,budget);
        try {owner.OpenOwned(database,token);return owner;}
        catch {owner._connection?.Dispose();owner._sealedLease?.Dispose();owner._areaLease?.Dispose();throw;}
    }
    /// <summary>Reopen only the exact completed native receipt. No source scan,
    /// writable SQLite recovery, ready-job requeue or provider permission.</summary>
    public static async Task<TakeoutAssociationCatalog> OpenCompletedAsync(TakeoutCompletedInventory completed,StorageBudgetManager budget,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(completed);ArgumentNullException.ThrowIfNull(budget);
        var owner=new TakeoutAssociationCatalog(completed.StateDirectory,completed.InstallationId,completed.ImportId,completed.CapturedContext,completed.Parts,budget,null);
        try {await owner.OpenCompletedOwnedAsync(completed,token).ConfigureAwait(false);return owner;}
        catch {owner._connection?.Dispose();owner._sealedLease?.Dispose();owner._areaLease?.Dispose();throw;}
    }
    private async Task OpenCompletedOwnedAsync(TakeoutCompletedInventory completed,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock(AccountingGate)
        {
            Regular(completed.StateDirectory,true);Regular(_root,true);var lockPath=Path.Combine(_root,"owner.lock");Regular(lockPath,false);
            _areaLease=new(lockPath,FileMode.Open,FileAccess.Read,FileShare.None);
            AssertBytes(Path.Combine(_root,"owner.json"),OwnerBytes(completed.InstallationId));_=MeasureInventory(_root,completed.InstallationId);
            Regular(_directory,true);AssertBytes(Path.Combine(_directory,"identity.json"),_identity);Regular(_path,false);
            if(File.Exists(_path+"-journal"))throw new InvalidDataException("A completed inventory cannot adopt unexplained native recovery work.");
            _sealedLease=new(_path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        }
        if(_sealedLease.Length!=completed.Result.Bytes)throw new InvalidDataException("Completed inventory changed its verified native length.");
        var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(_sealedLease,token).ConfigureAwait(false));
        if(hash!=completed.Result.Sha256)throw new InvalidDataException("Completed inventory changed its verified native bytes; preserve the receipt and index.");
        token.ThrowIfCancellationRequested();_connection=Connect(_path,true);VerifyIdentity(_connection);
        using(var query=Command("SELECT count(*),coalesce(sum(kind=0),0) FROM entry"))using(var row=query.ExecuteReader())
        {if(!row.Read()||row.GetInt64(0)!=completed.Result.Entries||row.GetInt64(1)!=completed.Result.Media)throw new InvalidDataException("Completed inventory changed its receipt counts.");}
        using(var query=Command("SELECT count(*),coalesce(sum(entries),0) FROM part WHERE complete=1"))using(var row=query.ExecuteReader())
        {if(!row.Read()||row.GetInt64(0)!=_parts.Count||row.GetInt64(1)!=completed.Result.Entries)throw new InvalidDataException("Completed inventory lost an original selected part.");}
        token.ThrowIfCancellationRequested();_sealed=completed.Result;
    }
    private void OpenOwned(ProcessorStateDatabase database,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var reservation=_budget.Reserve(256*1024);reservation.RecheckBeforeWrite(reservation.Bytes);
        lock(AccountingGate)
        {
            Regular(database.StateDirectory,true);var existed=Directory.Exists(_root);if(!existed)Directory.CreateDirectory(_root);Regular(_root,true);
            if(existed&&!File.Exists(Path.Combine(_root,"owner.json")))throw new InvalidDataException("Preserve the unmarked Takeout index directory; it cannot be adopted.");
            var lockPath=Path.Combine(_root,"owner.lock");if(File.Exists(lockPath))Regular(lockPath,false);_areaLease=new(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(!existed)WriteNew(Path.Combine(_root,"owner.json"),OwnerBytes(database.InstallationId));
            AssertBytes(Path.Combine(_root,"owner.json"),OwnerBytes(database.InstallationId));_=MeasureInventory(_root,database.InstallationId);
            if(!Directory.Exists(_directory))
            {
                if(Directory.EnumerateDirectories(_root).Take(64).Count()>=64)throw new IOException("Retained Takeout index capacity reached; preserve pending inputs before explicit cleanup.");
                Directory.CreateDirectory(_directory);WriteNew(Path.Combine(_directory,"identity.json"),_identity);
            }
            Regular(_directory,true);AssertBytes(Path.Combine(_directory,"identity.json"),_identity);
        }
        if(!File.Exists(_path))
        {
            if(Directory.Exists(_path)||File.Exists(_path+"-journal"))throw new InvalidDataException("Takeout index has unexplained native recovery entries.");
            using(var create=new FileStream(_path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){ }
            _connection=Connect(_path,false);
            using var transaction=_connection.BeginTransaction();using var schema=_connection.CreateCommand();schema.Transaction=transaction;
            schema.CommandText="""
                PRAGMA application_id=0x4d504154; PRAGMA user_version=1;
                CREATE TABLE identity(payload BLOB NOT NULL,sha256 TEXT NOT NULL) STRICT;
                CREATE TABLE part(id TEXT PRIMARY KEY,complete INTEGER NOT NULL CHECK(complete IN(0,1)),entries INTEGER,sha256 TEXT) STRICT;
                CREATE TABLE entry(key TEXT PRIMARY KEY,part_id TEXT NOT NULL REFERENCES part(id),kind INTEGER NOT NULL,
                  folder TEXT NOT NULL,name TEXT NOT NULL,fold_folder TEXT NOT NULL,fold_name TEXT NOT NULL,payload BLOB NOT NULL,sha256 TEXT NOT NULL) STRICT;
                CREATE INDEX takeout_entry_name ON entry(kind,fold_folder,fold_name,key);
                CREATE INDEX takeout_entry_page ON entry(kind,key);
                CREATE INDEX takeout_entry_part ON entry(part_id,key);
                """;schema.ExecuteNonQuery();using(var insert=_connection.CreateCommand())
            {insert.Transaction=transaction;insert.CommandText="INSERT INTO identity VALUES($body,$hash)";insert.Parameters.AddWithValue("$body",_identity);insert.Parameters.AddWithValue("$hash",Hash(_identity));insert.ExecuteNonQuery();}
            foreach(var part in _parts)
            {using var insert=_connection.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT INTO part VALUES($id,0,NULL,NULL)";insert.Parameters.AddWithValue("$id",part.PartId.ToString("D"));insert.ExecuteNonQuery();}
            reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();
        }
        else
        {
            Regular(_path,false);using(var file=File.OpenRead(_path))
            {
                if(file.Length is <100 or >MaximumIndexBytes)throw new InvalidDataException("Retained Takeout index is truncated or exceeds its bound.");Span<byte> header=stackalloc byte[100];file.ReadExactly(header);
                if(!header[..16].SequenceEqual("SQLite format 3\0"u8)||BinaryPrimitives.ReadInt32BigEndian(header[68..72])!=ApplicationId||BinaryPrimitives.ReadInt32BigEndian(header[60..64])!=1)throw new InvalidDataException("Refusing an unknown Takeout native index.");
            }
            using(var immutable=Connect(_path,true))VerifyIdentity(immutable);
            _connection=Connect(_path,false);VerifyIdentity(_connection);
        }
    }
    public void AddPart(TakeoutSelectedInput source,CancellationToken token=default)
    {
        if(_work is null)throw new InvalidOperationException("A completed inventory is read-only; never requeue or change ready work to reopen it.");
        ArgumentNullException.ThrowIfNull(source);Enter();
        try
        {
            token.ThrowIfCancellationRequested();var binding=_parts.SingleOrDefault(p=>p.PartId==source.PartId);
            if(binding is null||binding!=source.Binding)throw new InvalidDataException("Takeout part differs from the exact original selected source.");
            if(_seenParts.Contains(source.PartId))throw new InvalidOperationException("A complete import enumeration repeated a selected part.");
            using(var known=Command("SELECT complete,entries,sha256 FROM part WHERE id=$part",("$part",source.PartId.ToString("D"))))
            using(var row=known.ExecuteReader())
            {
                if(!row.Read())throw new InvalidDataException("Retained Takeout index lost a captured part.");
                if(row.GetInt32(0)==1&&binding.Kind=="zip")
                {
                    var expectedCount=row.GetInt64(1);var expectedHash=row.GetString(2);row.Close();var verified=PartDigest(source.PartId,token);
                    if(verified.Count!=expectedCount||verified.Hash!=expectedHash)throw new InvalidDataException("A retained ZIP inventory changed its original rows.");
                    _seenParts.Add(source.PartId);ReusedVerifiedZipParts++;return;
                }
            }
            var seen=new HashSet<string>(StringComparer.Ordinal);var batch=new List<TakeoutCapturedEntry>(250);
            foreach(var entry in source.Enumerate(token))
            {
                if(!seen.Add(entry.Key))throw new InvalidDataException("One selected part repeated an immutable entry identity.");
                var media=AssetId(_importId,entry.Key);batch.Add(new(entry.PartId,entry.Key,entry.RelativePath,entry.Bytes,entry.Kind,entry.Ordinal,entry.SourceStamp,media));
                if(batch.Count==250){WriteBatch(batch,token);batch.Clear();}
            }
            if(batch.Count!=0)WriteBatch(batch,token);
            var digest=PartDigest(source.PartId,token);
            if(digest.Count!=seen.Count)throw new InvalidDataException("Selected Takeout input lost or substituted retained entries; no original was changed.");
            if(_sealed is null)
            {
                using var reservation=_budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
                using var update=Command("UPDATE part SET complete=1,entries=$count,sha256=$hash WHERE id=$part",("$count",digest.Count),("$hash",digest.Hash),("$part",source.PartId.ToString("D")));
                if(update.ExecuteNonQuery()!=1)throw new InvalidDataException("Selected part completion did not persist.");
            }
            _seenParts.Add(source.PartId);
        }
        finally {Exit();}
    }
    private void WriteBatch(List<TakeoutCapturedEntry> rows,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var encoded=rows.Select(row=>JsonSerializer.SerializeToUtf8Bytes(row)).ToArray();
        using var reservation=_budget.Reserve(checked(256*1024+rows.Count*131072L+encoded.Sum(b=>(long)b.Length)*16));reservation.RecheckBeforeWrite(reservation.Bytes);
        using var transaction=_connection!.BeginTransaction();
        for(var at=0;at<rows.Count;at++)
        {
            token.ThrowIfCancellationRequested();var row=rows[at];var body=encoded[at];var (folder,name)=Names(row.RelativePath);
            using(var query=Command("SELECT payload,sha256 FROM entry WHERE key=$key",("$key",row.Key)))
            {
                query.Transaction=transaction;using var existing=query.ExecuteReader();
                if(existing.Read())
                {if(!existing.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(body)||existing.GetString(1)!=Hash(body))throw new InvalidDataException("Retained Takeout entry changed its exact original snapshot.");continue;}
            }
            if(_sealed is not null)throw new InvalidDataException("A sealed import inventory cannot acquire another original.");
            using var insert=Command("INSERT INTO entry VALUES($key,$part,$kind,$folder,$name,$foldFolder,$foldName,$body,$hash)",("$key",row.Key),("$part",row.PartId.ToString("D")),("$kind",(int)row.Kind),("$folder",folder),("$name",name),("$foldFolder",Fold(folder)),("$foldName",Fold(name)),("$body",body),("$hash",Hash(body)));insert.Transaction=transaction;insert.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested();reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();
        if(new FileInfo(_path).Length>MaximumIndexBytes)throw new InvalidDataException("Takeout index exceeded its bounded native file size.");
    }
    public async Task SealAsync(CancellationToken token=default)
    {
        if(_work is null)throw new InvalidOperationException("A reopened completed inventory cannot be resealed or acquire new input.");
        Enter();
        try
        {
            token.ThrowIfCancellationRequested();if(_seenParts.Count!=_parts.Count)throw new InvalidOperationException("Every exact selected part must complete before matching or ready inventory.");
            VerifyIdentity(_connection!);
            using(var query=Command("SELECT count(*),coalesce(sum(entries),0) FROM part WHERE complete=1"))
            using(var row=query.ExecuteReader())
            {if(!row.Read()||row.GetInt64(0)!=_parts.Count)throw new InvalidDataException("Import inventory is incomplete.");}
            if(_sealed is not null)return;
            long entries,media;using(var query=Command("SELECT count(*),coalesce(sum(kind=0),0) FROM entry"))using(var row=query.ExecuteReader()){row.Read();entries=row.GetInt64(0);media=row.GetInt64(1);}
            _connection!.Dispose();_connection=null;if(File.Exists(_path+"-journal"))throw new IOException("Join native Takeout journal recovery before sealing.");
            _sealedLease=new(_path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
            var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(_sealedLease,token).ConfigureAwait(false));token.ThrowIfCancellationRequested();_connection=Connect(_path,true);VerifyIdentity(_connection);
            _sealed=new(_sealedLease.Length,hash,entries,media);
        }
        finally {Exit();}
    }
    public TakeoutEntryPage ReadPage(TakeoutEntryKind kind,string? after=null,int limit=250,CancellationToken token=default)
    {
        Enter();try
        {
            RequireSealed();if(!Enum.IsDefined(kind)||limit is <1 or >250||after is not null&&!IsKey(after))throw new ArgumentException("Invalid bounded Takeout inventory page.");
            using var query=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE kind=$kind AND key>$after ORDER BY key LIMIT $limit",("$kind",(int)kind),("$after",after??""),("$limit",limit+1));
            using var rows=query.ExecuteReader();var entries=new List<TakeoutCapturedEntry>(limit);var more=false;
            while(rows.Read()){token.ThrowIfCancellationRequested();if(entries.Count==limit){more=true;break;}entries.Add(ReadRow(rows));}
            return new(entries.AsReadOnly(),more);
        }finally {Exit();}
    }
    public TakeoutAssociation Associate(string sidecarKey,string title,CancellationToken token=default)
    {
        Enter();try
        {
            RequireSealed();if(!IsKey(sidecarKey)||string.IsNullOrWhiteSpace(title)||title.Length>1024||title.Contains('/')||title.Contains('\\')||title.Any(char.IsControl))throw new ArgumentException("A bounded exact sidecar and media title are required.");
            TakeoutCapturedEntry sidecar;using(var query=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE key=$key",("$key",sidecarKey)))using(var row=query.ExecuteReader())
            {if(!row.Read())throw new KeyNotFoundException("The captured sidecar is absent.");sidecar=ReadRow(row);}
            if(sidecar.Kind!=TakeoutEntryKind.JsonSidecar)throw new InvalidOperationException("Only an actual JSON sidecar can associate metadata.");
            var (folder,name)=Names(sidecar.RelativePath);var stem=name[..^5];const string suffix=".supplemental-metadata";if(stem.EndsWith(suffix,StringComparison.Ordinal))stem=stem[..^suffix.Length];
            string expected;
            if(stem==title)expected=title;
            else if(DuplicateVariant(stem,title) is { } variant)expected=variant;
            else if(stem.Length>=40&&title.StartsWith(stem,StringComparison.Ordinal))expected=title;
            else return new("name-mismatch",null);
            using var candidates=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE kind=0 AND fold_folder=$folder AND fold_name=$name ORDER BY key LIMIT 2",("$folder",Fold(folder)),("$name",Fold(expected)));
            using var matches=candidates.ExecuteReader();token.ThrowIfCancellationRequested();if(!matches.Read())return new("missing-media",null);var media=ReadRow(matches);if(matches.Read())return new("ambiguous-media",null);
            var mediaName=Names(media.RelativePath).Name;if(mediaName!=expected)return new("name-mismatch",null);return new("matched",media);
        }finally {Exit();}
    }
    internal void AssertCompletedSelection(TakeoutCompletedInventory completed)
    {
        ArgumentNullException.ThrowIfNull(completed);Enter();try
        {
            RequireSealed();
            if(_importId!=completed.ImportId||_sealed!=completed.Result||
                !string.Equals(Path.GetFullPath(completed.StateDirectory),Path.GetDirectoryName(_root),StringComparison.Ordinal)||
                !_parts.SequenceEqual(completed.Parts)||
                !_identity.AsSpan().SequenceEqual(TakeoutImportInventoryRepository.Identity(completed.ImportId,completed.CapturedContext,completed.InstallationId)))
                throw new InvalidDataException("Original input and completed import belong to different native selections.");
        }finally {Exit();}
    }
    public Stream OpenCaptured(TakeoutSelectedInput source,TakeoutCapturedEntry entry,long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(entry);Enter();try
        {
            RequireSealed();if(_parts.SingleOrDefault(p=>p.PartId==source.PartId)!=source.Binding)throw new InvalidDataException("Input reader changed its captured import part.");
            using var query=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE key=$key",("$key",entry.Key));using var row=query.ExecuteReader();
            if(!row.Read()||ReadRow(row)!=entry)throw new InvalidDataException("Input read substituted a captured inventory entry.");return source.OpenRead(source.Restore(entry),maximumBytes);
        }finally {Exit();}
    }
    // The caller must retain this owner across its synchronous native state
    // commit. Returning only the digest would allow Dispose to release the
    // sealed file between verification and the durable ready/outbox transition.
    internal InventoryCommit AcquireVerifiedInventory(TakeoutImportInventoryWork work)
    {
        Enter();
        try
        {
            if(_work is null||!ReferenceEquals(work,_work))throw new InvalidOperationException("Inventory completion lost its exact native index owner.");
            RequireSealed();return new(this,_sealed!);
        }
        catch {Exit();throw;}
    }
    internal sealed class InventoryCommit(TakeoutAssociationCatalog owner,TakeoutInventoryResult result) : IDisposable
    {
        private TakeoutAssociationCatalog? _owner=owner;
        internal TakeoutInventoryResult Result=>Volatile.Read(ref _owner) is not null?result:throw new ObjectDisposedException(nameof(InventoryCommit));
        public void Dispose()=>Interlocked.Exchange(ref _owner,null)?.Exit();
    }
    private (long Count,string Hash) PartDigest(Guid id,CancellationToken token)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long count=0;
        using var query=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE part_id=$part ORDER BY key",("$part",id.ToString("D")));using var rows=query.ExecuteReader();
        while(rows.Read())
        {
            token.ThrowIfCancellationRequested();var row=ReadRow(rows);if(row.PartId!=id)throw new InvalidDataException("Takeout inventory crossed selected part identities.");
            hash.AppendData(Convert.FromHexString(row.Key));hash.AppendData(Convert.FromHexString(rows.GetString(1)));count++;
        }
        return(count,Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
    private TakeoutCapturedEntry ReadRow(SqliteDataReader row)
    {
        var bytes=row.GetFieldValue<byte[]>(0);if(bytes.Length>16384||Hash(bytes)!=row.GetString(1))throw new InvalidDataException("Takeout entry lost its original row digest.");
        var value=JsonSerializer.Deserialize<TakeoutCapturedEntry>(bytes)??throw new InvalidDataException("Missing Takeout entry.");var (folder,name)=Names(value.RelativePath);
        var binding=_parts.SingleOrDefault(p=>p.PartId==value.PartId);
        if(!IsKey(value.Key)||value.MediaId!=AssetId(_importId,value.Key)||binding is null||value.Ordinal<0||value.SourceStamp<0||(binding.Kind=="folder")!=(value.Ordinal==0)||value.Bytes is <0 or >TakeoutSelectedInput.MaximumEntryBytes||!Enum.IsDefined(value.Kind)||row.GetString(2)!=folder||row.GetString(3)!=name||row.GetString(4)!=Fold(folder)||row.GetString(5)!=Fold(name)||row.GetInt32(6)!=(int)value.Kind||row.GetString(7)!=value.Key||row.GetString(8)!=value.PartId.ToString("D"))throw new InvalidDataException("Takeout entry changed its original identity or indexed names.");
        return value;
    }
    private void RequireSealed(){if(_sealed is null||_sealedLease is null||!_sealedLease.CanRead)throw new InvalidOperationException("Complete and seal all selected input parts before matching or reading the inventory.");}
    private void VerifyIdentity(SqliteConnection connection)
    {
        using(var query=connection.CreateCommand())
        {query.CommandText="SELECT payload,sha256 FROM identity LIMIT 2";using var row=query.ExecuteReader();if(!row.Read()||!row.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(_identity)||row.GetString(1)!=Hash(_identity)||row.Read())throw new InvalidDataException("Takeout native index changed its captured job identity.");}
        using var parts=connection.CreateCommand();parts.CommandText="SELECT id FROM part ORDER BY id";using var rows=parts.ExecuteReader();var count=0;
        while(rows.Read()){var value=rows.GetString(0);if(!_parts.Any(p=>p.PartId.ToString("D")==value))throw new InvalidDataException("Takeout inventory acquired an unselected part.");count++;}
        if(count!=_parts.Count)throw new InvalidDataException("Takeout inventory lost a selected part.");
    }
    private SqliteCommand Command(string sql,params(string Name,object Value)[] values)
    {var command=(_connection??throw new InvalidOperationException("Takeout native index is not available.")).CreateCommand();try{command.CommandText=sql;foreach(var pair in values)command.Parameters.AddWithValue(pair.Name,pair.Value);return command;}catch{command.Dispose();throw;}}
    private static SqliteConnection Connect(string path,bool readOnly)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=readOnly?new Uri(path).AbsoluteUri+"?immutable=1":path,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=5}.ToString());
        try {connection.Open();using var command=connection.CreateCommand();command.CommandText=readOnly?"PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA cache_size=-2048;":"PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA page_size=4096; PRAGMA cache_size=-2048; PRAGMA temp_store=FILE; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA max_page_count=262144;";command.ExecuteNonQuery();return connection;}
        catch{connection.Dispose();throw;}
    }
    private static (string Folder,string Name) Names(string path)
    {
        path=TakeoutSelectedInput.LogicalPath(path);const string prefix="Takeout/Google Photos/";if(path.StartsWith(prefix,StringComparison.Ordinal))path=path[prefix.Length..];
        var slash=path.LastIndexOf('/');return slash<0?("",path):(path[..slash],path[(slash+1)..]);
    }
    private static string Fold(string value)=>value.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static string? DuplicateVariant(string stem,string title)
    {
        var dot=title.LastIndexOf('.');if(dot<1)return null;var start=title[..dot];var extension=title[dot..];string number;
        if(stem.StartsWith(title+"(",StringComparison.Ordinal)&&stem.EndsWith(')'))number=stem[(title.Length+1)..^1];
        else if(stem.StartsWith(start+"(",StringComparison.Ordinal)&&stem.EndsWith(")"+extension,StringComparison.Ordinal))number=stem[(start.Length+1)..^(extension.Length+1)];
        else return null;
        return int.TryParse(number,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)&&parsed is >0 and <=999999&&parsed.ToString(CultureInfo.InvariantCulture)==number?start+"("+number+")"+extension:null;
    }
    private static Guid AssetId(Guid import,string key)=>Guid.ParseExact(Hash(Encoding.UTF8.GetBytes(import.ToString("D")+"\n"+key))[..32],"N");
    private static bool IsKey(string? value)=>value is {Length:64}&&value.All(c=>c is >='0' and <='9' or >='a' and <='f');
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private void Enter(){lock(_gate){ObjectDisposedException.ThrowIf(_closed,this);if(_busy!=0)throw new InvalidOperationException("Join the active Takeout native index operation first.");_busy=1;}}
    private void Exit(){lock(_gate)_busy=0;}
    public void Dispose()
    {lock(_gate){if(_closed)return;if(_busy!=0)throw new InvalidOperationException("Join Takeout index IO before releasing its leases.");_connection?.Dispose();_connection=null;_sealedLease?.Dispose();_sealedLease=null;_areaLease?.Dispose();_areaLease=null;_closed=true;}}

    public static long MeasureOwnedBytes(string stateDirectory)
    {
        var root=Path.Combine(Path.GetFullPath(stateDirectory),DirectoryName);
        lock(AccountingGate)
        {
            if(!Directory.Exists(root)){if(File.Exists(root))throw new IOException("Takeout inventory accounting refuses a replaced directory.");return 0;}
            using var marker=JsonDocument.Parse(ReadMarker(Path.Combine(stateDirectory,"mpa-processor-state.json")));var installation=Guid.ParseExact(marker.RootElement.GetProperty("installationId").GetString()!,"D");
            AssertBytes(Path.Combine(root,"owner.json"),OwnerBytes(installation));return MeasureInventory(root,installation);
        }
    }
    // Startup recovery must hold the same real native-area lease as indexing.
    // Never create/adopt an unmarked area merely to invalidate a running attempt.
    internal static FileStream? AcquireInterruptedRecoveryLease(ProcessorStateDatabase database)
    {
        lock(AccountingGate)
        {
            Regular(database.StateDirectory,true);var root=Path.Combine(database.StateDirectory,DirectoryName);
            if(!Directory.Exists(root)){if(File.Exists(root))throw new IOException("Preserve the replaced Takeout recovery area.");return null;}
            Regular(root,true);var path=Path.Combine(root,"owner.lock");Regular(path,false);var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);
            try {AssertBytes(Path.Combine(root,"owner.json"),OwnerBytes(database.InstallationId));_=MeasureInventory(root,database.InstallationId);return lease;}
            catch {lease.Dispose();throw;}
        }
    }
    private static long MeasureInventory(string root,Guid installation)
    {
        Regular(root,true);long total=0;
        foreach(var entry in Entries(root,66))
        {
            var name=Path.GetFileName(entry);if(name is "owner.json" or "owner.lock"){Regular(entry,false);total=checked(total+new FileInfo(entry).Length);continue;}
            if(name.Length!=39||!name.StartsWith("import-",StringComparison.Ordinal)||!Guid.TryParseExact(name[7..],"N",out var id)||id==Guid.Empty||id.ToString("N")!=name[7..])throw new InvalidDataException("Unknown Takeout inventory entry; preserve all files.");
            Regular(entry,true);var identity=Path.Combine(entry,"identity.json");using var document=JsonDocument.Parse(ReadMarker(identity));var header=document.RootElement;
            if(header.GetProperty("format").GetString()!="mpa-takeout-association-index-1"||header.GetProperty("installationId").GetString()!=installation.ToString("D")||header.GetProperty("importId").GetString()!=id.ToString("D")||header.GetProperty("context").ValueKind!=JsonValueKind.String)throw new InvalidDataException("Retained Takeout inventory lost its captured owner.");
            foreach(var file in Entries(entry,3))
            {
                var fileName=Path.GetFileName(file);if(fileName is not("identity.json" or "inventory.sqlite" or "inventory.sqlite-journal"))throw new InvalidDataException("Unknown Takeout index file; preserve all bytes.");
                try {Regular(file,false);var bytes=new FileInfo(file).Length;var maximum=fileName=="identity.json"?512*1024:MaximumIndexBytes+65536;if(bytes>maximum)throw new InvalidDataException("Takeout index file exceeded its bounded size.");total=checked(total+bytes);}
                catch(FileNotFoundException)when(fileName=="inventory.sqlite-journal"){ }
            }
        }
        return total;
    }
    private static byte[] OwnerBytes(Guid id)=>JsonSerializer.SerializeToUtf8Bytes(new {format="mpa-takeout-associations-1",installationId=id.ToString("D")});
    private static string[] Entries(string path,int maximum)
    {var entries=Directory.EnumerateFileSystemEntries(path).Take(maximum+1).ToArray();if(entries.Length>maximum)throw new InvalidDataException("Takeout inventory directory exceeded its bounded entries.");return entries;}
    private static byte[] ReadMarker(string path)
    {Regular(path,false);using var file=File.OpenRead(path);if(file.Length is <1 or >512*1024)throw new InvalidDataException("Takeout inventory marker exceeds its bound.");var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);return bytes;}
    private static void AssertBytes(string path,byte[] bytes){if(!ReadMarker(path).AsSpan().SequenceEqual(bytes))throw new InvalidDataException("Takeout inventory marker changed its original identity.");}
    private static void WriteNew(string path,byte[] bytes){using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(bytes);file.Flush(true);}
    private static void Regular(string path,bool directory){var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0||((attributes&FileAttributes.Directory)!=0)!=directory)throw new IOException("Takeout index refuses links and unexpected filesystem entries.");}
}
