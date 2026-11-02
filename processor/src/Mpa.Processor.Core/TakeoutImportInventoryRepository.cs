using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Captured destination only, never a current provider write grant.</summary>
public sealed record TakeoutImportTarget(Guid LibraryId,string AccountId,[property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long AccountRevision,string FolderId,[property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long ConfigurationRevision);
public sealed class TakeoutImportInventoryWork
{
    internal ProcessorStateDatabase Owner { get; }
    public StageLease Lease { get; }
    public TakeoutImportTarget Target { get; }
    public ReadOnlyCollection<TakeoutInputBinding> Parts { get; }
    internal TakeoutImportInventoryWork(ProcessorStateDatabase owner,StageLease lease,TakeoutImportTarget target,TakeoutInputBinding[] parts)
    {Owner=owner;Lease=lease;Target=target;Parts=Array.AsReadOnly(parts);}
}

/// <summary>Durably captures one complete import selection before private indexing.
/// Inventory receipts are local evidence, never an original upload/backup receipt.</summary>
public sealed partial class TakeoutImportInventoryRepository(ProcessorStateDatabase database)
{
    internal const string Kind="takeout-import-inventory";
    internal const string Stage="inventory";
    private static readonly string Recipe=Hash("mpa-takeout-import-inventory:1"u8);
    private const string Item="{\"format\":\"mpa-takeout-import-inventory-item-1\"}";
    private readonly ProcessorStateDatabase _database=database??throw new ArgumentNullException(nameof(database));
    public bool Ensure(Guid importId,TakeoutImportTarget target,IReadOnlyList<TakeoutInputBinding> parts)
    {
        ArgumentNullException.ThrowIfNull(target);ArgumentNullException.ThrowIfNull(parts);
        Validate(target,parts);var body=Encode(target,parts);
        return new DurableJobRepository(_database).EnsureJobWithInitialItem(importId,Kind,body,new(importId,Hash(body),[new(Stage,Recipe,[])],Item));
    }
    public TakeoutImportInventoryWork? Claim(Guid importId)
    {
        var lease=new DurableJobRepository(_database).ClaimNext(Kind,Stage,importId);if(lease is null)return null;
        using var json=JsonDocument.Parse(lease.CapturedContext);var root=json.RootElement;
        if(root.GetProperty("format").GetString()!="mpa-takeout-import-inventory-1")throw new InvalidDataException("Unknown import inventory context.");
        var target=root.GetProperty("target").Deserialize<TakeoutImportTarget>()??throw new InvalidDataException("Missing import destination.");
        var parts=root.GetProperty("parts").Deserialize<TakeoutInputBinding[]>()??throw new InvalidDataException("Missing selected input parts.");
        Validate(target,parts);var work=new TakeoutImportInventoryWork(_database,lease,target,parts);AssertCurrent(work);return work;
    }
    public void AssertCurrent(TakeoutImportInventoryWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if(!ReferenceEquals(work.Owner,_database))throw new InvalidOperationException("Import work belongs to a different native state owner.");
        var lease=work.Lease;var context=Encode(work.Target,work.Parts);var hash=Hash(context);
        if(lease.ItemId!=lease.JobId||lease.Stage!=Stage||lease.CapturedItemContext!=Item||lease.CapturedContext!=Encoding.UTF8.GetString(context))throw new InvalidDataException("Import work changed its captured selection.");
        using var query=_database.Connection.CreateCommand();query.CommandText="""
            SELECT count(*) FROM item_stage s JOIN job j ON j.id=s.job_id JOIN job_item i ON i.job_id=s.job_id AND i.id=s.item_id
            WHERE j.id=$job AND j.kind=$kind AND j.paused=0 AND j.canceled=0 AND j.captured_context=$context AND j.context_hash=$hash
            AND i.id=$job AND i.captured_context=$item AND i.input_fingerprint=$hash
            AND s.name=$stage AND s.recipe_hash=$recipe AND s.state='running' AND s.lease_id=$lease AND s.attempt=$attempt AND s.checkpoint IS $checkpoint
            """;
        query.Parameters.AddWithValue("$job",lease.JobId.ToString("D"));query.Parameters.AddWithValue("$kind",Kind);query.Parameters.AddWithValue("$context",lease.CapturedContext);query.Parameters.AddWithValue("$hash",hash);query.Parameters.AddWithValue("$item",Item);
        query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$recipe",Recipe);query.Parameters.AddWithValue("$lease",lease.LeaseId.ToString("D"));query.Parameters.AddWithValue("$attempt",lease.Attempt);query.Parameters.AddWithValue("$checkpoint",(object?)lease.Checkpoint??DBNull.Value);
        if((long)query.ExecuteScalar()! !=1)throw new StaleStageLeaseException();
    }
    public void Complete(TakeoutImportInventoryWork work,TakeoutAssociationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        using var inventory=catalog.AcquireVerifiedInventory(work);
        AssertCurrent(work);var result=inventory.Result;
        new DurableJobRepository(_database).Complete(work.Lease,new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {format="mpa-takeout-import-inventory-result-1",indexSha256=result.Sha256,indexBytes=result.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),entries=result.Entries.ToString(System.Globalization.CultureInfo.InvariantCulture),media=result.Media.ToString(System.Globalization.CultureInfo.InvariantCulture)})));
    }
    internal static byte[] Identity(TakeoutImportInventoryWork work,Guid installation)=>Identity(work.Lease.JobId,work.Lease.CapturedContext,installation);
    internal static byte[] Identity(Guid importId,string context,Guid installation)=>JsonSerializer.SerializeToUtf8Bytes(new {format="mpa-takeout-association-index-1",installationId=installation.ToString("D"),importId=importId.ToString("D"),context});
    private static byte[] Encode(TakeoutImportTarget target,IReadOnlyList<TakeoutInputBinding> parts)=>new CanonicalJson(256*1024).Encode(JsonSerializer.SerializeToUtf8Bytes(new {format="mpa-takeout-import-inventory-1",target,parts}));
    private static void Validate(TakeoutImportTarget target,IReadOnlyList<TakeoutInputBinding> parts)
    {
        if(target.LibraryId==Guid.Empty||target.AccountRevision<1||target.ConfigurationRevision<1||!Opaque(target.AccountId)||!Opaque(target.FolderId)||target.FolderId=="root"||parts.Count is <1 or >64||parts.Select(p=>p.PartId).Distinct().Count()!=parts.Count)throw new ArgumentException("Capture an explicit destination and bounded distinct selected parts.");
        foreach(var part in parts)
        {
            if(part.PartId==Guid.Empty||part.Kind is not("folder" or "zip")||string.IsNullOrEmpty(part.Location)||part.Location.Length>4096||!Path.IsPathFullyQualified(part.Location)||part.Location.Any(char.IsControl))throw new ArgumentException("Invalid captured Takeout input selection.");
            if(part.Kind=="folder"&&(part.Bytes is not null||part.Sha256 is not null)||part.Kind=="zip"&&(part.Bytes is null or <22 or >TakeoutZipDirectoryBounds.MaximumArchiveBytes||part.Sha256 is not {Length:64}||part.Sha256.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f'))))throw new ArgumentException("Takeout snapshot binding changed its kind, length or hash.");
        }
    }
    private static bool Opaque(string? value)=>value is {Length:>0 and <=256}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-');
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
