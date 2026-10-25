using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed class CurrentControlObservationConflictException() : InvalidOperationException("The current-file observation or its captured access changed. Preserve pending work and capture afresh.");

/// <summary>Durable pending observation, never fresh authority, activation or write
/// admission. Every new nonce retires earlier captures before any network request.</summary>
public sealed record LocalCurrentControlAttempt(Guid Id,long Revision,Guid SlotId,string ConnectionSha256,long AccountRevision,string Phase,
    long? RootSequence,long? Version,long MaxVersion,string? SharedDriveId,int? DeclaredBytes,string? EntityTag,string? ParentsSha256,
    long ContentVersion,string ContentSha256);

/// <summary>Actual transport metadata, supplied only by the joined semantic owner.
/// A caller record alone cannot prove Google scope, native possession or authority.</summary>
public sealed record LocalCurrentControlMetadata(long Version,int Bytes,string EntityTag,string ParentsSha256,string? SharedDriveId);

/// <summary>Producer-specific observed high-water, borrowing the serialized database.
/// Exact Root envelopes already live in its immutable signing journal; retain bounded
/// references rather than duplicate bodies or adopt counters from received documents.
/// Initial publication provides a historical floor, never current-cloud freshness.</summary>
public sealed class LocalCurrentControlObservations(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database=database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private const string StateKey="local_current_observation_v1";
    private const int MaximumBytes=262144;
    private const int MaximumHeads=LocalCurrentControlHistoryPages.MaximumHeads;
    private static readonly JsonSerializerOptions RecordJson=new() {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    public LocalCurrentControlAttempt Begin(long accountRevision,CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();var state=Load(transaction,token);AssertAccount(state.Slot,accountRevision,transaction);
        if (accountRevision<state.Receipt.AccountRevision) throw new StaleDriveConnectionException();
        var old=state.Saved?.Attempt;var heads=state.Saved?.Heads ?? [state.Slot.Id];
        var receipt=state.Receipt;
        var attempt=new LocalCurrentControlAttempt(Guid.NewGuid(),checked((old?.Revision ?? 0)+1),state.Slot.Id,state.Connection.DocumentSha256,
            accountRevision,"open",null,null,old?.MaxVersion ?? receipt.DriveVersion,
            old is null ? state.InitialDriveId : old.SharedDriveId,null,null,null,
            old?.ContentVersion ?? receipt.DriveVersion,old?.ContentSha256 ?? receipt.EnvelopeSha256);
        Save(Saved.Create(attempt,heads),state,transaction);transaction.Commit();return attempt;
    }

    public LocalCurrentControlAttempt? Current(CancellationToken token=default)
    { using var transaction=_database.Connection.BeginTransaction();var result=Load(transaction,token).Saved?.Attempt;transaction.Commit();return result; }

    public LocalCurrentControlAttempt RememberMetadata(LocalCurrentControlAttempt expected,LocalCurrentControlMetadata actual,CancellationToken token=default)
    {
        Validate(actual);using var transaction=_database.Connection.BeginTransaction();var state=Require(expected,"open",transaction,token);
        if (actual.Version<expected.MaxVersion || actual.SharedDriveId!=expected.SharedDriveId) throw new CurrentControlObservationConflictException();
        var next=expected with {Phase="reading",Version=actual.Version,MaxVersion=actual.Version,DeclaredBytes=actual.Bytes,
            EntityTag=actual.EntityTag,ParentsSha256=actual.ParentsSha256};
        Save(Saved.Create(next,state.Saved!.Heads),state,transaction);transaction.Commit();return next;
    }

    public LocalCurrentControlAttempt RememberBytes(LocalCurrentControlAttempt expected,ReadOnlySpan<byte> bytes,CancellationToken token=default)
    {
        if (bytes.Length is <1 or >MaximumBytes) throw new ArgumentException("A bounded actual current-file body is required.",nameof(bytes));
        var owned=bytes.ToArray();var hash=Hash(owned);using var transaction=_database.Connection.BeginTransaction();var state=Require(expected,"reading",transaction,token);
        if (owned.Length!=expected.DeclaredBytes || expected.ContentVersion==expected.Version && expected.ContentSha256!=hash)
            throw new CurrentControlObservationConflictException();
        var next=expected with {Phase="read",ContentVersion=expected.Version!.Value,ContentSha256=hash};
        Save(Saved.Create(next,state.Saved!.Heads),state,transaction);transaction.Commit();return next;
    }

    public (LocalCurrentControlAttempt Attempt,LibraryCurrentControl Head) Observe(LocalCurrentControlAttempt expected,ReadOnlySpan<byte> bytes,CancellationToken token=default)
    {
        if (bytes.Length is <1 or >MaximumBytes) throw new ArgumentException("A bounded actual current envelope is required.",nameof(bytes));
        var owned=bytes.ToArray();using var transaction=_database.Connection.BeginTransaction();var state=Require(expected,"read",transaction,token);
        if (Hash(owned)!=expected.ContentSha256 || owned.Length!=expected.DeclaredBytes) throw new CurrentControlObservationConflictException();
        var head=LibraryCurrentControl.Read(owned,state.Connection);head.AssertSuccessorOf(state.Head);
        var journal=new ProtocolSigningJournal(_database,_budget);var pin=new LibraryAuthorityRepository(_database,_budget).Controller(transaction)
            ?? throw new InvalidDataException("Observed current work lost its independent Root pin.");
        var retained=journal.AssertRetainedCloudEnvelope(pin.ActorId,pin.Epoch,owned,transaction);
        if (retained.Kind!=LibraryCurrentControl.Kind) throw new ProtocolSigningConflictException();
        var heads=state.Saved!.Heads;
        if (head.Sequence>state.Head.Sequence)
        {
            if (heads.Length==MaximumHeads) throw new InvalidDataException("Observed current history exceeds its retained bound.");
            heads=[..heads,retained.Id];
        }
        else if (retained.Id!=heads[^1]) throw new ProtocolSigningConflictException();
        var next=expected with {Phase="captured",RootSequence=head.Sequence};
        Save(Saved.Create(next,heads),state,transaction);transaction.Commit();return(next,head);
    }

    public void AssertCaptured(LocalCurrentControlAttempt expected,CancellationToken token=default)
    { using var transaction=_database.Connection.BeginTransaction();_=Require(expected,"captured",transaction,token);transaction.Commit(); }

    public void AssertFenced(LocalCurrentControlAttempt expected,CancellationToken token=default)
    { using var transaction=_database.Connection.BeginTransaction();_=Require(expected,"fenced",transaction,token);transaction.Commit(); }

    /// <summary>Checks actual metadata before the final body. Changed metadata is
    /// durably blocked before false is returned; callers then throw outside this commit.</summary>
    public bool CheckFenceMetadata(LocalCurrentControlAttempt expected,LocalCurrentControlMetadata actual,CancellationToken token=default)
    {
        Validate(actual);using var transaction=_database.Connection.BeginTransaction();var state=Require(expected,"captured",transaction,token);
        var same=Matches(expected,actual);
        if (!same) Save(Saved.Create(expected with {Phase="blocked",MaxVersion=Math.Max(expected.MaxVersion,actual.Version)},state.Saved!.Heads),state,transaction);
        transaction.Commit();return same;
    }

    public LocalCurrentControlAttempt Fence(LocalCurrentControlAttempt expected,LocalCurrentControlMetadata actual,string bodySha256,CancellationToken token=default)
    {
        Validate(actual);using var transaction=_database.Connection.BeginTransaction();var state=Require(expected,"captured",transaction,token);
        var same=Matches(expected,actual) && bodySha256==expected.ContentSha256;
        var next=expected with {Phase=same ? "fenced" : "blocked",MaxVersion=Math.Max(expected.MaxVersion,actual.Version)};
        Save(Saved.Create(next,state.Saved!.Heads),state,transaction);transaction.Commit();
        if (!same) throw new CurrentControlObservationConflictException();return next;
    }

    /// <summary>Retires only this exact nonce after a failed final read, even if its
    /// account disconnected meanwhile. It grants no access and cannot replace new work.</summary>
    public void Invalidate(LocalCurrentControlAttempt expected,CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();var state=Load(transaction,token);
        if (state.Saved?.Attempt==expected && expected.Phase=="captured")
            Save(Saved.Create(expected with {Phase="blocked"},state.Saved.Heads),state,transaction);
        transaction.Commit();
    }

    private Loaded Require(LocalCurrentControlAttempt expected,string phase,SqliteTransaction transaction,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);var state=Load(transaction,token);
        if (expected.Phase!=phase || state.Saved?.Attempt!=expected) throw new CurrentControlObservationConflictException();
        AssertAccount(state.Slot,expected.AccountRevision,transaction);return state;
    }

    private Loaded Load(SqliteTransaction transaction,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var slot=new LibraryCurrentControlSlotRepository(_database,_budget).ReadIn(transaction)
            ?? throw new InvalidOperationException("Reserve and publish initial current control before observing it.");
        var connection=slot.ConnectionDescriptor();
        var receipt=new InitialCurrentControlPublicationRepository(_database,_budget).ReadIn(transaction)
            ?? throw new InvalidOperationException("Verify the first current publication before its ongoing producer observations.");
        var completion=new LibraryAuthorityRepository(_database,_budget).InitialPublicationCompletion(transaction)
            ?? throw new InvalidDataException("Current observations lost their original policy completion.");
        var body=Read(StateKey,MaximumBytes,transaction);var sha=Read(StateKey+":sha256",64,transaction);var claim=Read(StateKey+":claim",64,transaction);
        Saved? saved=null;
        if (body is not null || sha is not null || claim is not null)
        {
            if (body is null || sha is null || Hash(Encoding.UTF8.GetBytes(body))!=sha || claim!=Claim(connection,sha))
                throw new InvalidDataException("Current observations lost their complete integrity record.");
            try { saved=JsonSerializer.Deserialize<Saved>(body,RecordJson) ?? throw new InvalidDataException("Missing current observation state."); }
            catch(JsonException error) { throw new InvalidDataException("Current observation state cannot be decoded.",error); }
            if (saved.Attempt is null || saved.HeadPages is null || saved.Heads[0]!=slot.Id || Encode(saved)!=body)
                throw new InvalidDataException("Observed current history lost its fixed initial identity or canonical state.");
        }
        var journal=new ProtocolSigningJournal(_database,_budget);LibraryCurrentControl? head=null;
        foreach(var id in saved?.Heads ?? [slot.Id])
        {
            token.ThrowIfCancellationRequested();var intent=journal.Read(id,transaction) ?? throw new InvalidDataException("Observed Root work lost its immutable signing history.");
            if (intent.Kind!=LibraryCurrentControl.Kind || intent.Envelope is null) throw new InvalidDataException("Observed Root work changed its signed role.");
            var next=LibraryCurrentControl.Read(intent.Envelope.Value.Span,connection);
            if (head is not null) {next.AssertSuccessorOf(head);if(next.Sequence<=head.Sequence) throw new InvalidDataException("Observed Root history repeated or rewound an identity.");}
            head=next;
        }
        if (saved is not null) Validate(saved.Attempt,slot,connection,receipt,head!);
        return new(slot,connection,receipt,completion.Observation.Context.SharedDriveId,saved,head!);
    }

    private static void Validate(LocalCurrentControlAttempt value,LibraryCurrentControlSlot slot,LibraryConnectionDescriptor pin,
        InitialCurrentControlPublicationReceipt receipt,LibraryCurrentControl head)
    {
        if (value.Id==Guid.Empty || value.Revision<1 || value.SlotId!=slot.Id || value.ConnectionSha256!=pin.DocumentSha256 || value.AccountRevision<receipt.AccountRevision ||
            value.MaxVersion<receipt.DriveVersion || value.ContentVersion<receipt.DriveVersion || value.ContentVersion>value.MaxVersion || !Digest(value.ContentSha256) ||
            value.SharedDriveId is not null && !Opaque(value.SharedDriveId)) throw new InvalidDataException("Current observation changed its captured scope or high-water.");
        var selected=value.RootSequence is not null;
        if (selected && (value.RootSequence!=head.Sequence || value.ContentSha256!=head.EnvelopeSha256)) throw new InvalidDataException("Current observation lost its exact selected Root read.");
        var hasMetadata=value.Version is >0 && value.Version<=value.MaxVersion && value.DeclaredBytes is >=1 and <=MaximumBytes &&
            ValidTag(value.EntityTag) && Digest(value.ParentsSha256);
        var valid=value.Phase switch
        {
            "open"=>!selected && value.Version is null && value.DeclaredBytes is null && value.EntityTag is null && value.ParentsSha256 is null,
            "reading"=>!selected && hasMetadata && value.Version==value.MaxVersion,
            "read"=>!selected && hasMetadata && value.Version==value.MaxVersion && value.ContentVersion==value.Version,
            "captured" or "fenced"=>selected && hasMetadata && value.Version==value.MaxVersion && value.ContentVersion==value.Version,
            "blocked"=>selected && hasMetadata && value.ContentVersion==value.Version,
            _=>false
        };
        if (!valid) throw new InvalidDataException("Current observation phase lost its exact metadata/byte receipt.");
    }

    private static void Validate(LocalCurrentControlMetadata value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Version<1 || value.Bytes is <1 or >MaximumBytes || !ValidTag(value.EntityTag) || !Digest(value.ParentsSha256) ||
            value.SharedDriveId is not null && !Opaque(value.SharedDriveId)) throw new ArgumentException("Bounded actual current metadata is required.",nameof(value));
    }
    private static bool Matches(LocalCurrentControlAttempt value,LocalCurrentControlMetadata actual)=>value.Version==actual.Version && value.DeclaredBytes==actual.Bytes &&
        value.EntityTag==actual.EntityTag && value.ParentsSha256==actual.ParentsSha256 && value.SharedDriveId==actual.SharedDriveId;
    private void AssertAccount(LibraryCurrentControlSlot slot,long revision,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"SELECT revision FROM drive_account WHERE permission_id=$account AND connected=1",("$account",slot.ConnectionDescriptor().Workspace.AccountId));
        if (revision<slot.AllocationAccountRevision || command.ExecuteScalar() is not long actual || actual!=revision) throw new StaleDriveConnectionException();
    }
    private void Save(Saved value,Loaded context,SqliteTransaction transaction)
    {
        var text=Encode(value);var bytes=Encoding.UTF8.GetByteCount(text);var sha=Hash(Encoding.UTF8.GetBytes(text));
        using var reservation=_budget.Reserve(checked(bytes*3L+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
        Put(StateKey,text,transaction);Put(StateKey+":sha256",sha,transaction);Put(StateKey+":claim",Claim(context.Connection,sha),transaction);
    }
    private static string Encode(Saved value)=>Encoding.UTF8.GetString(new CanonicalJson(MaximumBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(value,RecordJson)));
    private static string Claim(LibraryConnectionDescriptor pin,string sha)=>Hash(Encoding.UTF8.GetBytes("mpa-local-current-observation-v1\n"+pin.DocumentSha256+"\n"+sha));
    private static string Hash(ReadOnlySpan<byte> value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    private static bool Digest(string? value)=>value is {Length:64} && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Opaque(string value)=>value is {Length:>0 and <=256} && value!="root" && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool ValidTag(string? value)=>value is {Length:>0 and <=1024} && EntityTagHeaderValue.TryParse(value,out var parsed) &&
        !parsed.IsWeak && parsed.Tag!="*" && parsed.ToString()==value;
    private string? Read(string key,int maximum,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"SELECT length(CAST(value AS BLOB)),value FROM processor_meta WHERE key=$key",("$key",key));using var reader=command.ExecuteReader();
        if (!reader.Read()) return null;
        if(reader.GetInt64(0) is <1 || reader.GetInt64(0)>maximum) throw new InvalidDataException("Current observation exceeds its retained byte bound.");return reader.GetString(1);
    }
    private void Put(string key,string value,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",key),("$value",value));
        if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("Current observation did not persist exactly one state row.");
    }
    private SqliteCommand Command(SqliteTransaction transaction,string sql,params(string Key,object Value)[] values)
    {var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;foreach(var pair in values) command.Parameters.AddWithValue(pair.Key,pair.Value);return command;}
    private sealed record Saved(LocalCurrentControlAttempt Attempt,Guid[][] HeadPages)
    {
        [JsonIgnore] public Guid[] Heads=>new LocalCurrentControlHistoryPages(HeadPages).Heads;
        internal static Saved Create(LocalCurrentControlAttempt attempt,Guid[] heads)=>new(attempt,LocalCurrentControlHistoryPages.FromHeads(heads).Pages);
    }
    private sealed record Loaded(LibraryCurrentControlSlot Slot,LibraryConnectionDescriptor Connection,InitialCurrentControlPublicationReceipt Receipt,
        string? InitialDriveId,Saved? Saved,LibraryCurrentControl Head);
}
