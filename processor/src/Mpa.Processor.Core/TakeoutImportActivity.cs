using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mpa.Processor.Core;

public sealed record TakeoutImportActivity(Guid Id,string InputName,TakeoutImportTarget Target,string State,
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long? Entries,
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long? Media,
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long Verified,bool Requested,string? Issue);

public sealed partial class TakeoutImportInventoryRepository
{
    private const string RequestPrefix="takeout-original-request-v1:";
    private const string IssuePrefix="takeout-import-issue-v1:";
    public IReadOnlyList<Guid> Identities()
    {
        using var query=_database.Connection.CreateCommand();query.CommandText="SELECT id FROM job WHERE kind=$kind ORDER BY rowid LIMIT 65";query.Parameters.AddWithValue("$kind",Kind);
        using var rows=query.ExecuteReader();var ids=new List<Guid>();while(rows.Read()){if(ids.Count==64)throw new InvalidDataException("Retained imports exceed their complete native inventory bound.");ids.Add(Guid.ParseExact(rows.GetString(0),"D"));}return ids;
    }
    public TakeoutImportActivity Activity(Guid importId)
    {
        using var query=_database.Connection.CreateCommand();query.CommandText="SELECT captured_context,context_hash FROM job WHERE id=$id AND kind=$kind";
        query.Parameters.AddWithValue("$id",importId.ToString("D"));query.Parameters.AddWithValue("$kind",Kind);
        string context;using(var row=query.ExecuteReader())
        {if(!row.Read())throw new KeyNotFoundException("The captured import is unavailable.");context=row.GetString(0);if(Hash(Encoding.UTF8.GetBytes(context))!=row.GetString(1))throw new InvalidDataException("Import selection lost its native integrity record.");}
        using var document=JsonDocument.Parse(context);var target=document.RootElement.GetProperty("target").Deserialize<TakeoutImportTarget>()!;
        var parts=document.RootElement.GetProperty("parts").Deserialize<TakeoutInputBinding[]>()!;Validate(target,parts);
        if(!Encode(target,parts).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(context)))throw new InvalidDataException("Import changed its captured selection schema.");
        var summary=new DurableJobRepository(_database).Summary(importId);var completed=ReadCompleted(importId);var requested=completed is not null&&RequestValue(importId) is not null;
        if(requested&&RequestValue(importId)!=RequestBody(completed!))throw new InvalidDataException("Original import request changed its exact reviewed selection.");
        var verified=new TakeoutOriginalJobRepository(_database).VerifiedCount(importId);var issue=ReadMeta(IssuePrefix+importId.ToString("D"));
        if(issue is not null&&issue is not("input-unavailable" or "drive-unavailable" or "permission-changed"))throw new InvalidDataException("Unknown retained import issue.");
        var state=summary.IsCanceled?"canceled":summary.Paused?"paused":issue is not null?"needs-attention":completed is null?"reviewing":!requested?"ready":verified==completed.Result.Media?"originals-verified":"copying";
        return new(importId,parts.Length==1?Path.GetFileName(Path.TrimEndingDirectorySeparator(parts[0].Location)):parts.Length.ToString(CultureInfo.InvariantCulture)+" selected parts",target,state,completed?.Result.Entries,completed?.Result.Media,verified,requested,issue);
    }
    /// <summary>Authenticated explicit Start only. This local human request is durable
    /// before acknowledgement, but does not reserve a signature or grant provider IO.</summary>
    public void RequestOriginals(Guid importId)
    {
        var completed=ReadCompleted(importId)??throw new InvalidOperationException("Wait for the selected input review to finish.");
        var summary=new DurableJobRepository(_database).Summary(importId);if(summary.Paused||summary.IsCanceled)throw new StaleStageLeaseException();
        var expected=RequestBody(completed);var previous=RequestValue(importId);if(previous is not null&&previous!=expected)throw new InvalidDataException("Original request conflicts with its first reviewed input.");
        using var command=_database.Connection.CreateCommand();command.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO NOTHING";
        command.Parameters.AddWithValue("$key",RequestPrefix+importId.ToString("D"));command.Parameters.AddWithValue("$value",expected);command.ExecuteNonQuery();ClearIssue(importId);
    }
    public void RecordIssue(Guid importId,string issue)
    {
        if(issue is not("input-unavailable" or "drive-unavailable" or "permission-changed"))throw new ArgumentException("Unknown operational import issue.",nameof(issue));
        _=Activity(importId);using var command=_database.Connection.CreateCommand();command.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key",IssuePrefix+importId.ToString("D"));command.Parameters.AddWithValue("$value",issue);command.ExecuteNonQuery();
    }
    public void ClearIssue(Guid importId)
    {using var command=_database.Connection.CreateCommand();command.CommandText="DELETE FROM processor_meta WHERE key=$key";command.Parameters.AddWithValue("$key",IssuePrefix+importId.ToString("D"));command.ExecuteNonQuery();}
    private string? RequestValue(Guid id)=>ReadMeta(RequestPrefix+id.ToString("D"));
    private string? ReadMeta(string key)
    {using var query=_database.Connection.CreateCommand();query.CommandText="SELECT value FROM processor_meta WHERE key=$key";query.Parameters.AddWithValue("$key",key);return query.ExecuteScalar() as string;}
    private static string RequestBody(TakeoutCompletedInventory completed)=>Encoding.UTF8.GetString(new Mpa.Processor.Protocol.CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-explicit-takeout-start-1",importId=completed.ImportId.ToString("D"),selectionSha256=Hash(Encoding.UTF8.GetBytes(completed.CapturedContext)),indexSha256=completed.Result.Sha256})));
}

public sealed partial class TakeoutOriginalJobRepository
{
    public static Guid Identity(Guid importId,string entryKey)=>JobId(importId,entryKey);
    public bool IsVerified(Guid jobId)
    {
        using var query=_database.Connection.CreateCommand();query.CommandText="SELECT count(*) FROM item_stage s JOIN job j ON j.id=s.job_id WHERE j.id=$id AND j.kind=$kind AND s.name=$stage AND s.state='ready' AND s.result_receipt IS NOT NULL";
        query.Parameters.AddWithValue("$id",jobId.ToString("D"));query.Parameters.AddWithValue("$kind",Kind);query.Parameters.AddWithValue("$stage",Stage);return(long)query.ExecuteScalar()! ==1;
    }
    internal long VerifiedCount(Guid importId)
    {
        using var query=_database.Connection.CreateCommand();query.CommandText="SELECT count(*) FROM item_stage s JOIN job j ON j.id=s.job_id WHERE j.kind=$kind AND s.name=$stage AND s.state='ready' AND s.result_receipt IS NOT NULL AND json_extract(j.captured_context,'$.importId')=$import";
        query.Parameters.AddWithValue("$kind",Kind);query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$import",importId.ToString("D"));return(long)query.ExecuteScalar()!;
    }
    /// <summary>Composition-root startup only, under exclusive native state/index
    /// ownership. Provider outcome stays UNKNOWN: retain every original checkpoint
    /// and first file ID, then actual transport reconciles that ID before any write.</summary>
    public int RecoverInterruptedAfterExclusiveRestart()
    {
        using var area=TakeoutAssociationCatalog.AcquireInterruptedRecoveryLease(_database);using var transaction=_database.Connection.BeginTransaction();
        var leases=new List<StageLease>();using(var query=_database.Connection.CreateCommand())
        {
            query.Transaction=transaction;query.CommandText="""
                SELECT j.id,j.captured_context,j.context_hash,i.captured_context,i.input_fingerprint,s.name,s.recipe_hash,s.lease_id,s.attempt,s.checkpoint,s.result_receipt,i.id
                FROM job j JOIN job_item i ON i.job_id=j.id JOIN item_stage s ON s.job_id=j.id AND s.item_id=i.id
                WHERE j.kind=$kind AND s.state IN('running','canceling') LIMIT 65
                """;query.Parameters.AddWithValue("$kind",Kind);using var rows=query.ExecuteReader();
            while(rows.Read())
            {
                if(leases.Count==64||rows.GetString(0)!=rows.GetString(11)||rows.GetString(5)!=Stage||rows.GetString(6)!=Recipe||rows.IsDBNull(7)||rows.GetInt32(8)<1||!rows.IsDBNull(10)||rows.GetString(3)!=Item||
                    Hash(Encoding.UTF8.GetBytes(rows.GetString(1)))!=rows.GetString(2)||rows.GetString(2)!=rows.GetString(4))throw new InvalidDataException("Unknown interrupted original-upload contract; preserve every pending identity.");
                var id=Guid.ParseExact(rows.GetString(0),"D");leases.Add(new(id,id,Stage,Guid.ParseExact(rows.GetString(7),"D"),rows.GetInt32(8),rows.GetString(1),rows.IsDBNull(9)?null:rows.GetString(9),rows.GetString(3)));
            }
        }
        foreach(var lease in leases){var work=Decode(lease,transaction);if(lease.JobId!=JobId(work.Capture.Inventory.ImportId,work.Capture.Entry.Key))throw new InvalidDataException("Original recovery changed its captured identity.");}
        using var update=_database.Connection.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE item_stage SET state=CASE WHEN(SELECT canceled FROM job WHERE id=job_id)=1 THEN 'canceled' ELSE 'queued' END,lease_id=NULL WHERE state IN('running','canceling') AND job_id IN(SELECT id FROM job WHERE kind=$kind)";
        update.Parameters.AddWithValue("$kind",Kind);var changed=update.ExecuteNonQuery();transaction.Commit();return changed;
    }
}
