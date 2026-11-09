using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Mpa.Processor.Protocol;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed record TakeoutMetadataIssue(string Key,string Name,string Code,bool Retryable,bool RawAvailable=false);
public sealed record TakeoutMetadataIssuePage(IReadOnlyList<TakeoutMetadataIssue> Issues,string? Next);

/// <summary>Read-only input identity minted by the held native inventory. This is
/// not an original action, result receipt or publication capability.</summary>
public sealed class TakeoutMetadataIssueInput
{
    private readonly TakeoutAssociationCatalog _owner;
    internal TakeoutCompletedInventory Inventory{get;}
    internal TakeoutCapturedEntry Entry{get;}
    internal TakeoutMetadataIssueInput(TakeoutAssociationCatalog owner,TakeoutCompletedInventory completed,TakeoutCapturedEntry entry)
    {_owner=owner;Inventory=completed;Entry=entry;}
    internal void AssertOwner(ProcessorStateDatabase database)
    {
        _owner.AssertCompletedSelection(Inventory);
        if(database.StateDirectory!=Inventory.StateDirectory||database.InstallationId!=Inventory.InstallationId)
            throw new InvalidOperationException("Metadata issue belongs to another native state owner.");
    }
}

public sealed partial class TakeoutAssociationCatalog
{
    public TakeoutMetadataIssueInput CaptureMetadataIssueInput(TakeoutCompletedInventory completed,TakeoutCapturedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);AssertCompletedSelection(completed);Enter();try
        {
            RequireSealed();using var query=Command("SELECT payload,sha256,folder,name,fold_folder,fold_name,kind,key,part_id FROM entry WHERE key=$key",("$key",entry.Key));
            using var row=query.ExecuteReader();
            if(entry.Kind!=TakeoutEntryKind.JsonSidecar||!row.Read()||ReadRow(row)!=entry)throw new InvalidDataException("Metadata issue substituted its captured sidecar.");
            return new(this,completed,entry);
        }finally{Exit();}
    }
}

/// <summary>Durable per-file observations, separate from work/backup receipts.
/// Captured input and first observed raw hash cannot change on retry. No paths,
/// status claims or source hashes are accepted from browser commands.</summary>
public sealed partial class TakeoutMetadataIssueRepository(ProcessorStateDatabase database)
{
    private readonly ProcessorStateDatabase _database=database??throw new ArgumentNullException(nameof(database));
    private readonly CanonicalJson _codec=new(16384);
    private const string Prefix="takeout-metadata-item-v1:";
    private const string RawPrefix = "takeout-metadata-raw-v1:";
    private const int MaximumRawTextBytes = ((TakeoutMetadataDocument.MaximumSourceBytes + 2) / 3) * 4;
    private sealed record Saved(TakeoutCapturedEntry Entry,string Code,string? SourceSha256,bool Resolved);
    public void Record(TakeoutMetadataIssueInput input,string code,string? sourceSha256=null)
    {
        ArgumentNullException.ThrowIfNull(input);input.AssertOwner(_database);ValidateCode(code);ValidateHash(sourceSha256);
        RecordCore(Actual(input.Inventory), input.Entry, code, sourceSha256, null);
    }

    /// <summary>Keep the first source bytes and issue in one transaction. This
    /// evidence creates no worker result or photo association.</summary>
    public void RecordRetainedRaw(TakeoutMetadataIssueInput input, string code, ReadOnlySpan<byte> raw)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.AssertOwner(_database);
        ValidateCode(code);
        if (code == "input-unavailable" || raw.Length != input.Entry.Bytes ||
            raw.Length is < 1 or > TakeoutMetadataDocument.MaximumSourceBytes)
            throw new InvalidDataException("Retain only the exact bounded bytes of a captured metadata issue.");

        var completed = Actual(input.Inventory);
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        var encoded = Convert.ToBase64String(raw);
        using var transaction = _database.Connection.BeginTransaction();
        var previous = ReadRawText(completed.ImportId, input.Entry.Key, transaction);
        if (previous is not null && previous != encoded)
            throw new InvalidDataException("Retained metadata issue bytes differ from their first capture.");

        RecordCore(completed, input.Entry, code, hash, transaction);
        if (previous is null)
        {
            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO processor_meta(key,value) VALUES($key,$value)";
            insert.Parameters.AddWithValue("$key", RawKey(completed.ImportId, input.Entry.Key));
            insert.Parameters.AddWithValue("$value", encoded);
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidDataException("Metadata issue bytes were not retained.");
        }
        transaction.Commit();
    }

    public byte[]? ReadRetainedRaw(Guid importId, string key)
    {
        if (importId == Guid.Empty || !Hash(key))
            throw new ArgumentException("Choose a captured import and sidecar.");
        var completed = new TakeoutImportInventoryRepository(_database).ReadCompleted(importId);
        if (completed is null) return null;

        var encoded = ReadRawText(importId, key, null);
        if (encoded is null) return null;
        var issue = Read(completed, key);
        if (issue is null || issue.SourceSha256 is null)
            throw new InvalidDataException("Retained metadata bytes lost their original issue.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error)
        { throw new InvalidDataException("Retained metadata bytes are malformed.", error); }
        if (bytes.Length is < 1 or > TakeoutMetadataDocument.MaximumSourceBytes || bytes.Length != issue.Entry.Bytes ||
            Convert.ToBase64String(bytes) != encoded || Convert.ToHexStringLower(SHA256.HashData(bytes)) != issue.SourceSha256)
            throw new InvalidDataException("Retained metadata bytes failed their original length or digest.");
        return bytes;
    }

    private string? ReadRawText(Guid importId, string key, SqliteTransaction? transaction)
    {
        using var query = _database.Connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT length(CAST(value AS BLOB)),value FROM processor_meta WHERE key=$key";
        query.Parameters.AddWithValue("$key", RawKey(importId, key));
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetInt64(0) is < 1 or > MaximumRawTextBytes)
            throw new InvalidDataException("Retained metadata bytes exceed their fixed bound.");
        return row.GetString(1);
    }

    private void RecordCore(TakeoutCompletedInventory completed, TakeoutCapturedEntry entry, string code, string? sourceSha256, SqliteTransaction? transaction)
    {
        var old = Read(completed, entry.Key, transaction);
        if(old is not null)
        {
            if(old.Entry!=entry||old.SourceSha256 is not null&&sourceSha256 is not null&&old.SourceSha256!=sourceSha256)
                throw new InvalidDataException("Metadata retry changed its first captured input or raw source.");
            if(old.Resolved)throw new InvalidOperationException("A completed metadata issue cannot be reopened as unfinished work.");
            if(old.Code!="input-unavailable"&&old.Code!=code)throw new InvalidDataException("A retained metadata issue changed its original classification.");
            sourceSha256??=old.SourceSha256;
        }
        Write(completed,new(entry,code,sourceSha256,false),transaction);
    }
    public TakeoutMetadataIssue? Find(TakeoutCompletedInventory completed,string key)
    {
        var actual=Actual(completed);var saved=Read(actual,key);return saved is null||saved.Resolved?null:View(saved);
    }
    public void Resolve(TakeoutVerifiedMetadata result)
    {
        ArgumentNullException.ThrowIfNull(result);if(!ReferenceEquals(result.Owner,_database))throw new InvalidOperationException("Metadata result belongs to another native state owner.");
        var completed=Actual(result.Imported.Original.Capture.Inventory);var old=Read(completed,result.Imported.Sidecar.Key);if(old is null||old.Resolved)return;
        if(old.Entry!=result.Imported.Sidecar||old.SourceSha256 is not null&&old.SourceSha256!=result.Source.Sha256)
            throw new InvalidDataException("Metadata completion changed the issue's original input or source.");
        Write(completed,old with{SourceSha256=result.Source.Sha256,Resolved=true});
    }
    public TakeoutMetadataIssuePage ReadPage(Guid importId,string? after=null,int limit=60)
    {
        if(importId==Guid.Empty||limit is <1 or >60||after is not null&&!Hash(after))throw new ArgumentException("Choose a captured import and bounded issue cursor.");
        var completed=new TakeoutImportInventoryRepository(_database).ReadCompleted(importId)??throw new InvalidOperationException("Wait for this import's input review.");
        var prefix=Prefix+importId.ToString("D")+":";var values=new List<(string Key,string Raw,bool RawAvailable)>();
        using(var query=_database.Connection.CreateCommand())
        {
            query.CommandText = """
                SELECT issue.key,issue.value,EXISTS(
                  SELECT 1 FROM processor_meta raw WHERE raw.key=$raw || substr(issue.key,$suffix))
                FROM processor_meta issue
                WHERE issue.key>=$start AND issue.key<$end AND issue.key>$after
                ORDER BY issue.key LIMIT $limit
                """;
            query.Parameters.AddWithValue("$raw", RawKey(importId, ""));
            query.Parameters.AddWithValue("$suffix", prefix.Length + 1);
            query.Parameters.AddWithValue("$start",prefix+"0");query.Parameters.AddWithValue("$end",prefix+"g");query.Parameters.AddWithValue("$after",prefix+(after??""));query.Parameters.AddWithValue("$limit",limit+1);
            using var rows=query.ExecuteReader();while(rows.Read())values.Add((rows.GetString(0)[prefix.Length..],rows.GetString(1),rows.GetBoolean(2)));
        }
        var more=values.Count>limit;if(more)values.RemoveAt(values.Count-1);
        var issues = values.Select(value => (Saved: Decode(completed,value.Key,value.Raw), value.RawAvailable))
            .Where(value => !value.Saved.Resolved).Select(value => View(value.Saved) with { RawAvailable = value.RawAvailable }).ToArray();return new(issues,more?values[^1].Key:null);
    }
    private TakeoutCompletedInventory Actual(TakeoutCompletedInventory completed)
    {
        var actual=new TakeoutImportInventoryRepository(_database).ReadCompleted(completed.ImportId)??throw new InvalidOperationException("Metadata issue lost its native import inventory.");
        if(actual.InstallationId!=completed.InstallationId||actual.StateDirectory!=completed.StateDirectory||actual.CapturedContext!=completed.CapturedContext||actual.Result!=completed.Result)
            throw new InvalidDataException("Metadata issue changed its original captured import.");return actual;
    }
    private Saved? Read(TakeoutCompletedInventory completed,string key,SqliteTransaction? transaction=null)
    {
        if(!Hash(key))throw new ArgumentException("An exact captured sidecar key is required.",nameof(key));
        using var query=_database.Connection.CreateCommand();query.Transaction=transaction;query.CommandText="SELECT value FROM processor_meta WHERE key=$key";query.Parameters.AddWithValue("$key",RowKey(completed.ImportId,key));
        return query.ExecuteScalar() is string raw?Decode(completed,key,raw):null;
    }
    private Saved Decode(TakeoutCompletedInventory completed,string key,string raw)
    {
        using var document=JsonDocument.Parse(raw);var root=document.RootElement;var value=root.GetProperty("entry");
        var entry=new TakeoutCapturedEntry(value.GetProperty("partId").GetGuid(),value.GetProperty("key").GetString()!,value.GetProperty("relativePath").GetString()!,Decimal(value,"bytes"),TakeoutEntryKind.JsonSidecar,value.GetProperty("ordinal").GetInt32(),Decimal(value,"sourceStamp"),value.GetProperty("mediaId").GetGuid());
        var saved=new Saved(entry,root.GetProperty("code").GetString()!,root.GetProperty("sourceSha256").GetString(),root.GetProperty("resolved").GetBoolean());
        ValidateCode(saved.Code);ValidateHash(saved.SourceSha256);_=TakeoutSelectedInput.LogicalPath(entry.RelativePath);
        if(entry.Key!=key||!Hash(key)||entry.Kind!=TakeoutEntryKind.JsonSidecar||entry.PartId==Guid.Empty||entry.MediaId==Guid.Empty||entry.Bytes is <0 or >TakeoutSelectedInput.MaximumEntryBytes||entry.Ordinal<0||completed.Parts.All(part=>part.PartId!=entry.PartId)||
            !Encode(completed,saved).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(raw)))throw new InvalidDataException("Metadata issue changed its captured identity, import or closed schema.");
        return saved;
    }
    private void Write(TakeoutCompletedInventory completed,Saved saved,SqliteTransaction? transaction=null)
    {
        var body=Encoding.UTF8.GetString(Encode(completed,saved));using var update=_database.Connection.CreateCommand();update.Transaction=transaction;update.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        update.Parameters.AddWithValue("$key",RowKey(completed.ImportId,saved.Entry.Key));update.Parameters.AddWithValue("$value",body);update.ExecuteNonQuery();
    }
    private byte[] Encode(TakeoutCompletedInventory completed,Saved saved)
    {
        var entry=JsonSerializer.SerializeToElement(new{partId=saved.Entry.PartId.ToString("D"),key=saved.Entry.Key,relativePath=saved.Entry.RelativePath,bytes=saved.Entry.Bytes.ToString(CultureInfo.InvariantCulture),ordinal=saved.Entry.Ordinal,sourceStamp=saved.Entry.SourceStamp.ToString(CultureInfo.InvariantCulture),mediaId=saved.Entry.MediaId.ToString("D")});
        var entryHash=Convert.ToHexStringLower(SHA256.HashData(_codec.Encode(Encoding.UTF8.GetBytes(entry.GetRawText()))));
        return _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {format="mpa-local-takeout-metadata-issue-1",importId=completed.ImportId.ToString("D"),indexSha256=completed.Result.Sha256,selectionSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(completed.CapturedContext))),entry,entrySha256=entryHash,sourceSha256=saved.SourceSha256,code=saved.Code,retryable=saved.Code=="input-unavailable",resolved=saved.Resolved}));
    }
    private static long Decimal(JsonElement root,string name)
    {
        var text=root.GetProperty(name).GetString();if(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value<0||value.ToString(CultureInfo.InvariantCulture)!=text)
            throw new InvalidDataException("Metadata issue changed an exact captured decimal.");return value;
    }
    private static TakeoutMetadataIssue View(Saved saved)=>new(saved.Entry.Key,saved.Entry.RelativePath,saved.Code,saved.Code=="input-unavailable");
    private static string RawKey(Guid importId, string key) => RawPrefix + importId.ToString("D") + ":" + key;
    private static string RowKey(Guid importId,string key)=>Prefix+importId.ToString("D")+":"+key;
    private static void ValidateCode(string code)
    {if(code is not("invalid-metadata" or "unsupported-metadata" or "metadata-too-large" or "name-mismatch" or "missing-media" or "ambiguous-media" or "input-unavailable"))throw new InvalidDataException("Unknown captured metadata issue.");}
    private static void ValidateHash(string? value){if(value is not null&&!Hash(value))throw new InvalidDataException("Metadata issue has an invalid source hash.");}
    private static bool Hash(string value)=>value.Length==64&&value.All(c=>c is >='0' and <='9' or >='a' and <='f');
}
