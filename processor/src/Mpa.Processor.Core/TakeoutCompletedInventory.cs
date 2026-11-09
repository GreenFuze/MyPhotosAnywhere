using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>A native ready receipt captured by the serial state owner. This is
/// historical read evidence only: no running lease, current authority or backup
/// receipt is manufactured when a completed import is reopened.</summary>
public sealed class TakeoutCompletedInventory
{
    public Guid ImportId { get; }
    public TakeoutImportTarget Target { get; }
    public ReadOnlyCollection<TakeoutInputBinding> Parts { get; }
    internal string StateDirectory { get; }
    internal Guid InstallationId { get; }
    internal string CapturedContext { get; }
    internal TakeoutInventoryResult Result { get; }
    internal TakeoutCompletedInventory(ProcessorStateDatabase owner,Guid importId,string context,TakeoutImportTarget target,TakeoutInputBinding[] parts,TakeoutInventoryResult result)
    {StateDirectory=owner.StateDirectory;InstallationId=owner.InstallationId;ImportId=importId;CapturedContext=context;Target=target;Parts=Array.AsReadOnly(parts);Result=result;}
}

public sealed partial class TakeoutImportInventoryRepository
{
    /// <summary>Call on the actual serial state owner. Paused/canceled ready work
    /// remains readable; this snapshot grants no new work or provider admission.</summary>
    public TakeoutCompletedInventory? ReadCompleted(Guid importId)
        =>ReadCompleted(importId,null);
    internal TakeoutCompletedInventory? ReadCompleted(Guid importId,Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        if(importId==Guid.Empty)throw new ArgumentException("A captured import identity is required.",nameof(importId));
        using var query=_database.Connection.CreateCommand();query.Transaction=transaction;query.CommandText="""
            SELECT j.captured_context,j.context_hash,i.captured_context,i.input_fingerprint,s.result_receipt
            FROM job j JOIN job_item i ON i.job_id=j.id AND i.id=j.id
            JOIN item_stage s ON s.job_id=j.id AND s.item_id=i.id
            WHERE j.id=$job AND j.kind=$kind AND s.name=$stage AND s.recipe_hash=$recipe AND s.state='ready' AND s.lease_id IS NULL
            """;
        query.Parameters.AddWithValue("$job",importId.ToString("D"));query.Parameters.AddWithValue("$kind",Kind);query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$recipe",Recipe);
        using var row=query.ExecuteReader();if(!row.Read())return null;
        var context=row.GetString(0);var body=Encoding.UTF8.GetBytes(context);var digest=Hash(body);
        if(digest!=row.GetString(1)||digest!=row.GetString(3)||row.GetString(2)!=Item||row.IsDBNull(4))throw new InvalidDataException("Completed import lost its original captured selection or receipt.");
        using var captured=JsonDocument.Parse(context);var root=captured.RootElement;
        if(root.GetProperty("format").GetString()!="mpa-takeout-import-inventory-1")throw new InvalidDataException("Unknown completed import context.");
        var target=root.GetProperty("target").Deserialize<TakeoutImportTarget>()??throw new InvalidDataException("Missing original destination.");
        var parts=root.GetProperty("parts").Deserialize<TakeoutInputBinding[]>()??throw new InvalidDataException("Missing original selected parts.");
        Validate(target,parts);if(!Encode(target,parts).AsSpan().SequenceEqual(body))throw new InvalidDataException("Completed import changed its exact context schema.");
        var receipt=Encoding.UTF8.GetBytes(row.GetString(4));if(!new CanonicalJson(16384).Encode(receipt).AsSpan().SequenceEqual(receipt))throw new InvalidDataException("Completed inventory receipt is not canonical.");
        using var document=JsonDocument.Parse(receipt);var result=document.RootElement;
        if(result.EnumerateObject().Count()!=5||result.GetProperty("format").GetString()!="mpa-takeout-import-inventory-result-1")throw new InvalidDataException("Unknown completed inventory receipt.");
        var hash=result.GetProperty("indexSha256").GetString();var bytes=Decimal(result,"indexBytes");var entries=Decimal(result,"entries");var media=Decimal(result,"media");
        if(hash is not {Length:64}||hash.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f'))||bytes is <100 or >1024L*1024*1024||entries>64L*TakeoutZipDirectoryBounds.MaximumEntries||media>entries)throw new InvalidDataException("Completed inventory receipt exceeds its original bounds.");
        return new(_database,importId,context,target,parts,new(bytes,hash,entries,media));
    }
    private static long Decimal(JsonElement root,string name)
    {
        var value=root.GetProperty(name).GetString();
        if(!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)||parsed<0||parsed.ToString(CultureInfo.InvariantCulture)!=value)throw new InvalidDataException("Invalid inventory receipt decimal string.");
        return parsed;
    }
}
