using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record CurrentCatalogStagePage(ReadOnlyCollection<DriveCatalogAsset> Assets,bool HasMore);
/// <summary>Owns one claimed generated SQLite index. Complete leaves commit atomically;
/// identical replay verifies every retained row. Sealing closes writable native handles
/// before hashing and grants no activation. Dispose preserves claimed recovery files.</summary>
public sealed partial class CurrentCatalogStageIndex : IDisposable
{
    private const int ApplicationId=0x4d504143;
    private int _formatVersion=2;
    public bool SupportsAccountPaging=>_formatVersion==2;
    private readonly string _path;
    private readonly CurrentCatalogStageClaim _claim;
    private readonly StorageBudgetManager _budget;
    private readonly DriveCatalogFacts _codec;
    private readonly HashSet<string> _seen=new(StringComparer.Ordinal);
    private readonly Action _release;
    private SqliteConnection? _connection;
    private FileStream? _readLease;
    private CurrentCatalogStageDescriptor? _sealed;
    private long _seenAssets;
    private bool _closed;
    private CurrentCatalogStageIndex(string path,CurrentCatalogStageClaim claim,StorageBudgetManager budget,Action release)
    {_path=path;_claim=claim;_budget=budget;_release=release;_codec=new(LibraryConnectionDescriptor.Decode(claim.ConnectionCode).LibraryId);}
    internal static CurrentCatalogStageIndex Open(string path,CurrentCatalogStageClaim claim,StorageBudgetManager budget,Action release,CancellationToken token)
    {
        var owner=new CurrentCatalogStageIndex(path,claim,budget,release);
        try
        {
            token.ThrowIfCancellationRequested();CurrentCatalogStageArea.Regular(Path.GetDirectoryName(path)!,true);
            var existed=File.Exists(path);
            if(!existed)
            {
                if(claim.Phase!="preparing" || Directory.Exists(path) || File.Exists(path+"-journal")) throw new InvalidDataException("A claimed current stage cannot be recreated over missing or unknown work.");
                using var reservation=budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
                using(var create=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { }
                owner._connection=Connect(path,false);
                using var setup=owner._connection.CreateCommand();setup.CommandText="""
                    PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA page_size=4096;
                    PRAGMA cache_size=-2048; PRAGMA temp_store=FILE; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;
                    PRAGMA max_page_count=131072; PRAGMA application_id=1297105219; PRAGMA user_version=2;
                    CREATE TABLE identity(payload BLOB NOT NULL,sha256 TEXT NOT NULL) STRICT;
                    CREATE TABLE leaf(prefix TEXT PRIMARY KEY,sha256 TEXT NOT NULL,assets INTEGER NOT NULL CHECK(assets>0)) STRICT;
                    CREATE TABLE fact(id TEXT PRIMARY KEY,prefix TEXT NOT NULL REFERENCES leaf(prefix),payload BLOB NOT NULL,sha256 TEXT NOT NULL,
                      date_unknown INTEGER NOT NULL CHECK(date_unknown IN(0,1)),date_sort INTEGER NOT NULL,account_id TEXT NOT NULL) STRICT;
                    CREATE INDEX current_stage_date ON fact(date_unknown,date_sort,id);
                    CREATE INDEX current_stage_account_date ON fact(account_id,date_unknown,date_sort,id);
                    """;setup.ExecuteNonQuery();var identity=CurrentCatalogStageJournal.Identity(claim);
                using var insert=owner._connection.CreateCommand();insert.CommandText="INSERT INTO identity VALUES($body,$hash)";insert.Parameters.AddWithValue("$body",identity);insert.Parameters.AddWithValue("$hash",Hash(identity));
                if(insert.ExecuteNonQuery()!=1) throw new InvalidOperationException("Current-stage ownership header did not persist.");
            }
            else
            {
                owner._formatVersion=VerifyIdentity(path,claim); // Immutable read first; never recover or upgrade an unknown file.
                if(claim.Descriptor is not null)
                {
                    owner._readLease=new(path,FileMode.Open,FileAccess.Read,FileShare.Read);VerifyDescriptor(owner._readLease,claim.Descriptor,token);owner._sealed=claim.Descriptor;
                }
                owner._connection=Connect(path,claim.Descriptor is not null);
            }
            VerifyHeader(owner._connection,claim);token.ThrowIfCancellationRequested();return owner;
        }
        catch {owner._connection?.Dispose();owner._readLease?.Dispose();throw;}
    }
    public void Add(CatalogFactsLeaf leaf,CancellationToken token=default)
    {
        RequireOpen();ArgumentNullException.ThrowIfNull(leaf);token.ThrowIfCancellationRequested();
        if(_seen.Contains(leaf.Prefix)) throw new InvalidDataException("One complete staging enumeration repeated an identity partition.");
        if(_seen.Count==WorkspaceObservationRepository.MaximumDocuments) throw new CatalogTraversalLimitException();
        var pin=LibraryConnectionDescriptor.Decode(_claim.ConnectionCode);var reference=leaf.Reference;
        ArgumentNullException.ThrowIfNull(reference);ArgumentNullException.ThrowIfNull(reference.Encoded);
        var encoded=reference.Encoded;
        if(!Opaque(encoded.FileId) || encoded.FileId==pin.Workspace.FolderId || encoded.Bytes is <1 or >CatalogChunkDecoder.MaximumCompressedBytes ||
            encoded.Sha256 is not {Length:64} || !encoded.Sha256.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("A staged partition requires a bounded exact immutable reference.");
        var bytes=_codec.Encode(leaf.Prefix,leaf.Facts.Assets);
        if(leaf.Prefix!=leaf.Facts.Prefix || reference.Encoded.AccountId!=pin.Workspace.AccountId || reference.Encoded.WorkspaceId!=pin.Workspace.FolderId ||
            bytes.LongLength!=reference.ExpandedBytes || Hash(bytes)!=reference.ExpandedSha256) throw new InvalidDataException("Staging facts lost their exact verified immutable projection.");
        // Decode the owned canonical copy; caller-owned collections cannot mutate
        // the facts after the digest check and before the native transaction.
        var facts=_codec.Read(bytes,leaf.Prefix);var leafHash=LeafHash(leaf);long? existing=null;string? existingHash=null;
        using(var query=_connection!.CreateCommand())
        {query.CommandText="SELECT assets,sha256 FROM leaf WHERE prefix=$prefix";query.Parameters.AddWithValue("$prefix",leaf.Prefix);using var row=query.ExecuteReader();if(row.Read()){existing=row.GetInt64(0);existingHash=row.GetString(1);}}
        if(existing is not null)
        {
            if(existing!=facts.Assets.Count || existingHash!=leafHash) throw new InvalidDataException("A retained staging partition changed its exact reference or contents.");
            foreach(var asset in facts.Assets) {token.ThrowIfCancellationRequested();VerifyRow(asset,leaf.Prefix);}
            using var count=_connection.CreateCommand();count.CommandText="SELECT count(*) FROM fact WHERE prefix=$prefix";count.Parameters.AddWithValue("$prefix",leaf.Prefix);
            if((long)count.ExecuteScalar()! != facts.Assets.Count) throw new InvalidDataException("A retained partition has missing or additional rows.");
        }
        else
        {
            if(_sealed is not null) throw new InvalidDataException("A sealed current stage cannot accept another partition.");
            using var reservation=_budget.Reserve(checked(bytes.LongLength*6+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
            using var transaction=_connection.BeginTransaction();using(var insertLeaf=_connection.CreateCommand())
            {
                insertLeaf.Transaction=transaction;insertLeaf.CommandText="INSERT INTO leaf VALUES($prefix,$hash,$count)";insertLeaf.Parameters.AddWithValue("$prefix",leaf.Prefix);insertLeaf.Parameters.AddWithValue("$hash",leafHash);insertLeaf.Parameters.AddWithValue("$count",facts.Assets.Count);
                if(insertLeaf.ExecuteNonQuery()!=1) throw new InvalidOperationException("Staging partition did not persist exactly once.");
            }
            using var insert=_connection.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT INTO fact(id,prefix,payload,sha256,date_unknown,date_sort"+(_formatVersion==2?",account_id":"")+") VALUES($id,$prefix,$body,$hash,$unknown,$sort"+(_formatVersion==2?",$account":"")+")";
            var id=insert.Parameters.Add("$id",SqliteType.Text);var prefix=insert.Parameters.AddWithValue("$prefix",leaf.Prefix);var body=insert.Parameters.Add("$body",SqliteType.Blob);var hash=insert.Parameters.Add("$hash",SqliteType.Text);var unknown=insert.Parameters.Add("$unknown",SqliteType.Integer);var sort=insert.Parameters.Add("$sort",SqliteType.Integer);
            var account=_formatVersion==2?insert.Parameters.Add("$account",SqliteType.Text):null;insert.Prepare();
            foreach(var asset in facts.Assets)
            {
                token.ThrowIfCancellationRequested();var payload=_codec.Encode(leaf.Prefix,[asset]);id.Value=asset.Id.ToString("D");prefix.Value=leaf.Prefix;body.Value=payload;hash.Value=Hash(payload);unknown.Value=asset.EffectiveUtc is null;sort.Value=asset.EffectiveUtc is null ? 0L : -asset.EffectiveUtc.Value;if(account is not null) account.Value=asset.AccountId;
                if(insert.ExecuteNonQuery()!=1) throw new InvalidOperationException("Staging asset did not persist exactly once.");
            }
            token.ThrowIfCancellationRequested();reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();
            if(new FileInfo(_path).Length>CatalogGenerationCaptureRepository.MaximumSnapshotBytes) throw new InvalidDataException("Current stage exceeded its existing snapshot bound.");
        }
        token.ThrowIfCancellationRequested();_seen.Add(leaf.Prefix);_seenAssets=checked(_seenAssets+facts.Assets.Count);
    }
    public CurrentCatalogStageDescriptor Seal(long expectedAssets,CancellationToken token=default)
    {
        RequireOpen();token.ThrowIfCancellationRequested();
        if(expectedAssets<0 || expectedAssets!=_seenAssets) throw new InvalidDataException("Complete graph and staged asset counts disagree.");
        using(var count=_connection!.CreateCommand())
        {
            count.CommandText="SELECT (SELECT count(*) FROM fact),(SELECT count(*) FROM leaf),coalesce((SELECT sum(assets) FROM leaf),0)";using var row=count.ExecuteReader();
            if(!row.Read() || row.GetInt64(0)!=expectedAssets || row.GetInt64(1)!=_seen.Count || row.GetInt64(2)!=expectedAssets) throw new InvalidDataException("Staging did not visit every retained partition and row.");
        }
        if(_sealed is not null) return _sealed;
        _connection.Dispose();_connection=null;
        if(File.Exists(_path+"-journal")) throw new InvalidDataException("The current stage still has an unconfirmed transaction journal.");
        CurrentCatalogStageArea.Regular(_path,false);
        using(var flush=new FileStream(_path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) flush.Flush(true);
        _readLease=new(_path,FileMode.Open,FileAccess.Read,FileShare.Read);token.ThrowIfCancellationRequested();
        var descriptor=new CurrentCatalogStageDescriptor(_claim.Id,_readLease.Length,Hash(_readLease,token),expectedAssets,_seen.Count);CurrentCatalogStageJournal.ValidateDescriptor(descriptor);
        token.ThrowIfCancellationRequested();_connection=Connect(_path,true);VerifyHeader(_connection,_claim);_sealed=descriptor;return descriptor;
    }
    public CurrentCatalogStagePage ReadPage(CatalogCursor? after=null,int limit=60,CancellationToken token=default)=>
        ReadOrderedPage(after is null?new(StageReadKind.First):new(StageReadKind.After,Cursor:after),limit,token);
    private void VerifyRow(DriveCatalogAsset asset,string prefix)
    {
        var expected=_codec.Encode(prefix,[asset]);using var query=_connection!.CreateCommand();query.CommandText="SELECT prefix,length(payload),payload,sha256,date_unknown,date_sort"+(_formatVersion==2?",account_id":"")+" FROM fact WHERE id=$id";query.Parameters.AddWithValue("$id",asset.Id.ToString("D"));using var row=query.ExecuteReader();
        if(!row.Read() || row.GetString(0)!=prefix || row.GetInt64(1)!=expected.Length || !row.GetFieldValue<byte[]>(2).AsSpan().SequenceEqual(expected) || row.GetString(3)!=Hash(expected) || row.GetBoolean(4)!=(asset.EffectiveUtc is null) || row.GetInt64(5)!=(asset.EffectiveUtc is null ? 0L : -asset.EffectiveUtc.Value) || _formatVersion==2 && row.GetString(6)!=asset.AccountId) throw new InvalidDataException("A retained staging row changed or disappeared.");
    }
    internal static void VerifyForRemoval(string path,CurrentCatalogStageClaim claim,CancellationToken token)
    {token.ThrowIfCancellationRequested();VerifyIdentity(path,claim);if(claim.Descriptor is not null){using var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);VerifyDescriptor(lease,claim.Descriptor,token);}}
    private static int VerifyIdentity(string path,CurrentCatalogStageClaim claim)
    {
        int version;CurrentCatalogStageArea.Regular(path,false);using(var file=File.OpenRead(path))
        {
            if(file.Length is <100 or >CatalogGenerationCaptureRepository.MaximumSnapshotBytes) throw new InvalidDataException("A retained current-stage file exceeds its exact bounded ownership header.");
            Span<byte> header=stackalloc byte[100];file.ReadExactly(header);
            version=BinaryPrimitives.ReadInt32BigEndian(header[60..64]);
            if(!header[..16].SequenceEqual("SQLite format 3\0"u8) || BinaryPrimitives.ReadInt32BigEndian(header[68..72])!=ApplicationId || version is not (1 or 2))
                throw new InvalidDataException("Refusing to open an unknown or truncated claimed current-stage file.");
        }
        // Identity is committed once and never modified by leaf transactions. An
        // immutable read can inspect it BEFORE any native hot-journal recovery.
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=new Uri(path).AbsoluteUri+"?immutable=1",Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());connection.Open();VerifyHeader(connection,claim);return version;
    }
    private static void VerifyHeader(SqliteConnection connection,CurrentCatalogStageClaim claim)
    {
        using var query=connection.CreateCommand();query.CommandText="SELECT length(payload),payload,sha256 FROM identity LIMIT 2";using var row=query.ExecuteReader();var expected=CurrentCatalogStageJournal.Identity(claim);
        if(!row.Read() || row.GetInt64(0)!=expected.Length || !row.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(expected) || row.GetString(2)!=Hash(expected) || row.Read()) throw new InvalidDataException("The current-stage file lost its exact captured ownership header.");
    }
    private static SqliteConnection Connect(string path,bool readOnly)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Mode=readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=5}.ToString());
        try {connection.Open();using var command=connection.CreateCommand();command.CommandText="PRAGMA trusted_schema=OFF;PRAGMA foreign_keys=ON;PRAGMA cache_size=-2048;PRAGMA temp_store=FILE;"+(readOnly ? "PRAGMA query_only=ON;" : "PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;PRAGMA max_page_count=131072;");command.ExecuteNonQuery();return connection;}
        catch {connection.Dispose();throw;}
    }
    private static void VerifyDescriptor(FileStream file,CurrentCatalogStageDescriptor descriptor,CancellationToken token)
    {CurrentCatalogStageJournal.ValidateDescriptor(descriptor);token.ThrowIfCancellationRequested();if(file.Length!=descriptor.Bytes || Hash(file,token)!=descriptor.Sha256) throw new InvalidDataException("A sealed current-stage file changed or lost bytes.");token.ThrowIfCancellationRequested();}
    private static bool Opaque(string value)=>value is {Length:>0 and <=256} && value!="root" && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static string LeafHash(CatalogFactsLeaf leaf)=>Hash(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(leaf.Reference)));
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(FileStream file,CancellationToken token)
    {
        file.Position=0;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int count;
            while((count=file.Read(buffer,0,buffer.Length))>0) {token.ThrowIfCancellationRequested();hash.AppendData(buffer,0,count);}
            token.ThrowIfCancellationRequested();return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally {ArrayPool<byte>.Shared.Return(buffer);}
    }
    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_closed,this);
        if(_connection is null) throw new InvalidOperationException("The current-stage native operation did not complete; join and reopen its same captured identity.");
    }
    public void Dispose()
    {
        if(_closed) return;_connection?.Dispose();_connection=null;_readLease?.Dispose();_readLease=null;_release();_closed=true;
    }
}
