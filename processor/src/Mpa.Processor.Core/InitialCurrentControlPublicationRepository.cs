using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical exact initial-current receipt, never fresh cloud authority,
/// canonical activation or permission to overwrite the same file. Later observations
/// must retain at least this actual version and exactly the original signed bytes.</summary>
public sealed record InitialCurrentControlPublicationReceipt(Guid SlotId,string ConnectionSha256,string EnvelopeSha256,
    long RootSequence,long DriveVersion,long AccountRevision);

public sealed class InitialCurrentControlPublicationRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database=database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private const string Key="initial_current_publication_v1";

    public InitialCurrentControlPublicationReceipt? Read()
    {
        using var transaction=_database.Connection.BeginTransaction();var result=ReadIn(transaction);transaction.Commit();return result;
    }

    public InitialCurrentControlPublicationReceipt RecordObserved(LibraryCurrentControlSlot expected,string envelopeSha256,long version,long accountRevision)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (version<1 || accountRevision<1) throw new ArgumentOutOfRangeException(nameof(version));
        using var transaction=_database.Connection.BeginTransaction();
        var slot=new LibraryCurrentControlSlotRepository(_database,_budget).ReadIn(transaction)
            ?? throw new InvalidDataException("Initial-current publication lost its allocated identity.");
        if (slot!=expected || slot.FileId is null) throw new InvalidOperationException("Initial-current publication substituted its retained slot.");
        var control=ReadSigned(slot,transaction);
        var local=new LibraryAuthorityRepository(_database,_budget).ReadLocalPublicationAuthority(transaction);
        if (local.AccountRevision!=accountRevision || envelopeSha256!=control.EnvelopeSha256) throw new StaleLibraryConfigurationException();
        var old=ReadIn(transaction);
        if (old is not null)
        {
            if (version<old.DriveVersion || accountRevision<old.AccountRevision) throw new InvalidDataException("Initial-current observation rewound its retained receipt.");
            transaction.Commit();return old;
        }
        var receipt=new InitialCurrentControlPublicationReceipt(slot.Id,control.ConnectionSha256,control.EnvelopeSha256,control.Sequence,version,accountRevision);
        var body=Encode(receipt);var hash=Hash(body);
        using var reservation=_budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(Key,body,transaction);Insert(Key+":sha256",hash,transaction);Insert(Key+":claim",hash,transaction);
        transaction.Commit();return receipt;
    }

    internal InitialCurrentControlPublicationReceipt? ReadIn(SqliteTransaction transaction)
    {
        var body=ReadValue(Key,2048,transaction);var hash=ReadValue(Key+":sha256",64,transaction);var claim=ReadValue(Key+":claim",64,transaction);
        if (body is null && hash is null && claim is null) return null;
        if (body is null || hash is null || claim!=hash || Hash(body)!=hash) throw new InvalidDataException("Initial-current publication receipt is missing or corrupt.");
        try
        {
            var receipt=JsonSerializer.Deserialize<InitialCurrentControlPublicationReceipt>(body) ?? throw new InvalidDataException("Missing initial-current receipt.");
            var slot=new LibraryCurrentControlSlotRepository(_database,_budget).ReadIn(transaction) ?? throw new InvalidDataException("Initial-current receipt lost its slot.");
            var control=ReadSigned(slot,transaction);
            if (Encode(receipt)!=body || receipt.SlotId!=slot.Id || receipt.ConnectionSha256!=control.ConnectionSha256 ||
                receipt.EnvelopeSha256!=control.EnvelopeSha256 || receipt.RootSequence!=control.Sequence || receipt.DriveVersion<1 ||
                slot.AllocationAccountRevision is not { } allocated || receipt.AccountRevision<allocated)
                throw new InvalidDataException("Initial-current receipt changed its original identity or signed scope.");
            return receipt;
        }
        catch(Exception error) when(error is JsonException or ArgumentException or ProtocolFormatException)
        { throw new InvalidDataException("Initial-current receipt cannot be decoded safely.",error); }
    }

    private LibraryCurrentControl ReadSigned(LibraryCurrentControlSlot slot,SqliteTransaction transaction)
    {
        var intent=new ProtocolSigningJournal(_database,_budget).Read(slot.Id,transaction)
            ?? throw new InvalidDataException("Initial-current publication lost its fixed signing intent.");
        if (intent.Kind!=LibraryCurrentControl.Kind || intent.Envelope is null) throw new InvalidDataException("Initial current bytes have not been sealed.");
        var control=LibraryCurrentControl.Read(intent.Envelope.Value.Span,slot.ConnectionDescriptor());
        var completion=new LibraryAuthorityRepository(_database,_budget).InitialPublicationCompletion(transaction)
            ?? throw new InvalidDataException("Initial-current receipt lost its original policies.");
        if (control.Catalog is not null || control.Publishers.Reference.FileId!=completion.Publisher.FileId ||
            control.Publishers.Reference.Bytes!=completion.Publisher.Bytes || control.Publishers.Reference.Sha256!=completion.Publisher.Sha256 ||
            control.Sources.Reference.FileId!=completion.Sources.FileId || control.Sources.Reference.Bytes!=completion.Sources.Bytes ||
            control.Sources.Reference.Sha256!=completion.Sources.Sha256)
            throw new InvalidDataException("Initial-current receipt cannot select another policy or a later catalog.");
        var journal=new ProtocolSigningJournal(_database,_budget);
        var publisher=journal.Read(completion.Plan.PublisherIntentId,transaction);
        var sources=journal.Read(completion.Plan.SourceIntentId,transaction);
        if (publisher?.Envelope is null || sources?.Envelope is null) throw new InvalidDataException("Initial-current receipt lost its sealed policies.");
        _=control.ReadPublishers(publisher.Envelope.Value.Span,slot.ConnectionDescriptor());
        _=control.ReadSources(sources.Envelope.Value.Span,slot.ConnectionDescriptor());
        return control;
    }

    private static string Encode(InitialCurrentControlPublicationReceipt value)=>Encoding.UTF8.GetString(new CanonicalJson(2048).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string Hash(string value)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string? ReadValue(string key,int maximum,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"SELECT length(value),value FROM processor_meta WHERE key=$key",("$key",key));using var reader=command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt64(0) is <1 || reader.GetInt64(0)>maximum) throw new InvalidDataException("Initial-current receipt exceeds its bound.");
        return reader.GetString(1);
    }
    private void Insert(string key,string value,SqliteTransaction transaction)
    {
        using var command=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value)",("$key",key),("$value",value));
        if (command.ExecuteNonQuery()!=1) throw new InvalidOperationException("Initial-current receipt did not persist exactly one row.");
    }
    private SqliteCommand Command(SqliteTransaction transaction,string sql,params (string Key,object Value)[] values)
    {
        var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;
        foreach(var pair in values) command.Parameters.AddWithValue(pair.Key,pair.Value);return command;
    }
}
