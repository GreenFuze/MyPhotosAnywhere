using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>One controller-signed local import command per completed selection.
/// Native private intent, not a metadata publication or current cloud permission.
/// It approves selected entries; the inventory is not a content hash of originals.</summary>
public sealed class TakeoutOriginalApprovalRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    public const string Kind="original-action";
    private const string Prefix="takeout-original-approval-v1:";
    private readonly ProcessorStateDatabase _database=database??throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget??throw new ArgumentNullException(nameof(budget));
    private readonly CanonicalJson _codec=new(16384);

    /// <summary>Only the authenticated explicit import-action owner calls this,
    /// under fresh native controller/source/account admission. No network or signing
    /// occurs here. Reserve body, counter and original-selection binding atomically.</summary>
    public ProtocolSigningIntent CaptureIntent(TakeoutCompletedInventory completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if(completed.StateDirectory!=_database.StateDirectory||completed.InstallationId!=_database.InstallationId)
            throw new InvalidOperationException("Import approval belongs to another native installation.");
        using var transaction=_database.Connection.BeginTransaction();var actual=Completed(completed.ImportId,transaction);
        if(actual.CapturedContext!=completed.CapturedContext||actual.Result!=completed.Result)throw new InvalidDataException("Import selection changed before approval.");
        var pin=Pin(transaction);var id=ApprovalId(actual.ImportId);var journal=new ProtocolSigningJournal(_database,_budget);
        StorageReservation? binding=null;
        try
        {
            using var reserved=journal.ReserveIn(id,pin.ActorId,pin.Epoch,Kind,sequence=>
            {
                var body=Body(actual,pin,id,sequence);var value=Binding(actual.ImportId,Hash(body));var previous=Meta(id,transaction);
                if(previous is not null&&previous!=value)throw new ProtocolSigningConflictException();
                if(previous is null)
                {
                    binding=_budget.Reserve(65536);binding.RecheckBeforeWrite(binding.Bytes);
                    using var insert=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value)");
                    insert.Parameters.AddWithValue("$key",Prefix+id.ToString("D"));insert.Parameters.AddWithValue("$value",value);
                    if(insert.ExecuteNonQuery()!=1)throw new InvalidOperationException("Original approval binding did not insert exactly once.");
                }
                return body;
            },transaction);
            reserved.Recheck();binding?.RecheckBeforeWrite(binding.Bytes);transaction.Commit();return reserved.Value;
        }
        finally {binding?.Dispose();}
    }
    public ProtocolSigningIntent RequireSealed(TakeoutCompletedInventory completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        using var transaction=_database.Connection.BeginTransaction();var intent=RequireOwnedIn(ApprovalId(completed.ImportId),transaction,true);
        AssertUnsignedIn(intent.Body.Span,transaction);var actual=Completed(completed.ImportId,transaction);
        if(actual.StateDirectory!=completed.StateDirectory||actual.InstallationId!=completed.InstallationId||actual.CapturedContext!=completed.CapturedContext||actual.Result!=completed.Result)
            throw new InvalidDataException("Original approval changed its native selected input.");
        transaction.Commit();return intent;
    }
    public ProtocolSigningIntent? ReadCaptured(TakeoutCompletedInventory completed)
    {
        ArgumentNullException.ThrowIfNull(completed);using var transaction=_database.Connection.BeginTransaction();
        var actual=Completed(completed.ImportId,transaction);
        if(actual.StateDirectory!=completed.StateDirectory||actual.InstallationId!=completed.InstallationId||actual.CapturedContext!=completed.CapturedContext||actual.Result!=completed.Result)
            throw new InvalidDataException("Original approval changed its native selected input.");
        var intent=new ProtocolSigningJournal(_database,_budget).Read(ApprovalId(completed.ImportId),transaction);
        if(intent is not null)AssertUnsignedIn(intent.Body.Span,transaction);
        transaction.Commit();return intent;
    }
    /// <summary>Complete signing/history inventories explicitly account for this
    /// private command by its native completed-selection binding and exact signature.
    /// It must never be silently skipped or mislabeled as a published source policy.</summary>
    public void AssertLocalIntentOwned(Guid id)
    {using var transaction=_database.Connection.BeginTransaction();AssertLocalIntentOwnedIn(id,transaction);transaction.Commit();}
    internal void AssertLocalIntentOwnedIn(Guid id,SqliteTransaction transaction)=>_=RequireOwnedIn(id,transaction,true);
    private ProtocolSigningIntent RequireOwnedIn(Guid id,SqliteTransaction transaction,bool sealedRequired)
    {
        _=ReadBinding(id,transaction);var intent=new ProtocolSigningJournal(_database,_budget).Read(id,transaction)??throw new SigningHistoryRecoveryRequiredException();
        if(intent.Kind!=Kind||sealedRequired&&intent.Envelope is null)throw new SigningHistoryRecoveryRequiredException();
        AssertUnsignedIn(intent.Body.Span,transaction);return intent;
    }
    // Called by the shared counter journal before reservation/seal and on every
    // historical read. No signature/counter recursion or policy substitution.
    internal void AssertUnsignedIn(ReadOnlySpan<byte> body,SqliteTransaction transaction)
    {
        using var document=JsonDocument.Parse(body.ToArray());var root=document.RootElement;
        var id=root.GetProperty("payload").GetProperty("approvalId").GetGuid();var binding=ReadBinding(id,transaction);
        var completed=Completed(binding.ImportId,transaction);var pin=Pin(transaction);
        if(ApprovalId(completed.ImportId)!=id||binding.BodySha256!=Hash(body)||
            !long.TryParse(root.GetProperty("sequence").GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out var sequence)||sequence<1||
            !Body(completed,pin,id,sequence).AsSpan().SequenceEqual(body))
            throw new ProtocolSigningConflictException();
    }
    private TakeoutCompletedInventory Completed(Guid importId,SqliteTransaction transaction)
        =>new TakeoutImportInventoryRepository(_database).ReadCompleted(importId,transaction)??throw new InvalidDataException("Original approval lost its completed selected-input receipt.");
    private LibraryControllerPin Pin(SqliteTransaction transaction)=>new LibraryAuthorityRepository(_database,_budget).Controller(transaction)
        ??throw new InvalidOperationException("Enroll the native controller before approving original imports.");
    private byte[] Body(TakeoutCompletedInventory completed,LibraryControllerPin pin,Guid id,long sequence)
    {
        if(completed.Target.LibraryId!=pin.LibraryId)throw new ProtocolSigningConflictException();
        var bytes=_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa",major=1,minor=0,libraryId=pin.LibraryId.ToString("D"),actorId=pin.ActorId.ToString("D"),epoch=pin.Epoch.ToString(CultureInfo.InvariantCulture),
            sequence=sequence.ToString(CultureInfo.InvariantCulture),kind=Kind,requiredCapabilities=Array.Empty<string>(),
            payload=new
            {
                approvalId=id.ToString("D"),action="import",source=new{type="import-manifest",manifestSha256=completed.Result.Sha256,selectionSha256=SelectionHash(completed)},
                destination=new{accountPermissionId=completed.Target.AccountId,folderId=completed.Target.FolderId,workspaceFolderId=pin.FolderId},recipeSha256=(string?)null,tombstoneId=(string?)null
            }
        }));
        using var document=JsonDocument.Parse(bytes);
        _=new ApprovedOriginalActionReader(pin.ActorId).Read(new(pin.LibraryId,pin.ActorId,pin.Epoch,sequence,Kind,0,Array.AsReadOnly(Array.Empty<string>()),document.RootElement.GetProperty("payload").GetRawText()));
        return bytes;
    }
    private string SelectionHash(TakeoutCompletedInventory completed)=>Hash(_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {
        format="mpa-takeout-original-selection-1",importId=completed.ImportId.ToString("D"),indexSha256=completed.Result.Sha256,
        indexBytes=completed.Result.Bytes.ToString(CultureInfo.InvariantCulture),media=completed.Result.Media.ToString(CultureInfo.InvariantCulture),
        entries=completed.Result.Entries.ToString(CultureInfo.InvariantCulture),capturedSelectionSha256=Hash(Encoding.UTF8.GetBytes(completed.CapturedContext)),selection="all-media"
    })));
    private (Guid ImportId,string BodySha256) ReadBinding(Guid id,SqliteTransaction transaction)
    {
        var value=Meta(id,transaction)??throw new SigningHistoryRecoveryRequiredException();using var document=JsonDocument.Parse(value);
        var root=document.RootElement;var import=root.GetProperty("importId").GetGuid();var digest=root.GetProperty("bodySha256").GetString();
        if(import==Guid.Empty||digest is not{Length:64}||digest.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f'))||Binding(import,digest)!=value)
            throw new InvalidDataException("Original approval lost its exact native binding.");
        return(import,digest);
    }
    private string Binding(Guid importId,string bodySha256)=>Encoding.UTF8.GetString(_codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-takeout-original-approval-1",importId=importId.ToString("D"),bodySha256})));
    private string? Meta(Guid id,SqliteTransaction transaction)
    {using var query=Command(transaction,"SELECT value FROM processor_meta WHERE key=$key");query.Parameters.AddWithValue("$key",Prefix+id.ToString("D"));return query.ExecuteScalar() as string;}
    private SqliteCommand Command(SqliteTransaction transaction,string sql)
    {var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;return command;}
    private static Guid ApprovalId(Guid importId)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("mpa-takeout-original-approval:1\n"+importId.ToString("D"))).AsSpan(0,16));
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
