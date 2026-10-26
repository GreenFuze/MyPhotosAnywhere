using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical canonical selection. Reading this receipt does not refresh
/// authority, authorize publication or grant permission to change original media.</summary>
public sealed record CurrentCatalogActivation(Guid StageId,string ConnectionCode,string RootSha256,long RootSequence,
    Guid FencedAttempt,long AccountRevision,long DriveVersion,string? SharedDriveId,long ConfigurationRevision,
    long PublisherSequence,string PublisherSha256,long SourceSequence,string SourceSha256,Guid? PublisherId,long? PublisherEpoch,
    string? PublisherKeyId,CatalogObjectReference? Catalog,long CatalogSequence,int Commits,CurrentCatalogStageDescriptor Index)
{
    public string Format {get;init;}="mpa-current-catalog-activation";
    public int Major {get;init;}=1;
}

/// <summary>Borrow the serialized state owner. Only the live reader/stage facade
/// supplies complete graph witnesses; the transaction independently verifies current
/// nonce, Root signatures/history, exact durable byte observations and human sources.
/// Policy selections and the canonical pointer commit together, without network IO.
/// Runtime composition and Root publication completion remain separate.</summary>
public sealed class CurrentCatalogActivationRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    internal const string Key="current_catalog_active_v1";
    private readonly ProcessorStateDatabase _database=database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private static readonly JsonSerializerOptions Json=new() {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    public CurrentCatalogActivation? Read()
    {using var transaction=_database.Connection.BeginTransaction();var result=ReadIn(transaction);transaction.Commit();return result;}

    public CurrentCatalogActivation Activate(LocalCurrentControlAttempt fenced,CurrentCatalogStageClaim stage,ReadOnlyMemory<byte> publisher,
        ReadOnlyMemory<byte> sources,long catalogSequence,int commits,IReadOnlyList<LocalCurrentGraphObservation> observations,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(stage);ArgumentNullException.ThrowIfNull(observations);
        if(observations.Count>WorkspaceObservationRepository.MaximumDocuments) throw new CatalogTraversalLimitException();
        var owned=observations.ToArray();using var transaction=_database.Connection.BeginTransaction();token.ThrowIfCancellationRequested();
        var selection=new LocalCurrentControlObservations(_database,_budget).FencedSelectionIn(fenced,publisher.Span,sources.Span,transaction,token);
        var claim=new CurrentCatalogStageJournal(_database,_budget).AssertSealedIn(stage,fenced,selection.Pin,transaction);var descriptor=claim.Descriptor!;
        var actuals=new LocalCurrentGraphObservations(_database,_budget);var references=new Dictionary<string,CatalogObjectReference>(StringComparer.Ordinal);
        foreach(var expected in owned)
        {
            token.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(expected);
            if(!references.TryAdd(expected.Reference.FileId,expected.Reference) || actuals.RequireCompleteIn(fenced,selection.Pin,expected.Reference,transaction)!=expected)
                throw new CurrentControlObservationConflictException();
        }
        _=actuals.RequireCompleteIn(fenced,selection.Pin,selection.Head.Publishers.Reference,transaction);
        _=actuals.RequireCompleteIn(fenced,selection.Pin,selection.Head.Sources.Reference,transaction);
        foreach(var required in selection.RequiredCatalogs)
            if(!references.TryGetValue(required.FileId,out var observed) || observed!=required) throw new CurrentControlObservationConflictException();
        if(selection.Head.Catalog is null)
        {if(catalogSequence!=0 || commits!=0 || owned.Length!=0 || descriptor.Assets!=0) throw new CurrentControlObservationConflictException();}
        else if(catalogSequence is <1 or >CatalogCommitHistoryTraversal.MaximumCommits || commits!=catalogSequence ||
            !references.TryGetValue(selection.Head.Catalog.FileId,out var head) || head!=selection.Head.Catalog) throw new CurrentControlObservationConflictException();
        var previous=ReadIn(transaction);
        if(previous is not null && (previous.ConnectionCode!=selection.Pin.Encode() || previous.RootSequence>selection.Head.Sequence ||
            previous.RootSequence==selection.Head.Sequence && previous.RootSha256!=selection.Head.EnvelopeSha256 ||
            previous.PublisherId==selection.Publishers.Publisher?.ActorId && previous.PublisherEpoch==selection.Publishers.Publisher?.Epoch && previous.CatalogSequence>catalogSequence))
            throw new CurrentControlObservationConflictException();
        var authority=new LibraryAuthorityRepository(_database,_budget);
        using var acceptedPublisher=authority.SelectPublisherPolicyIn(publisher.Span,transaction);
        using var acceptedSources=authority.SelectSourceConfigurationIn(sources.Span,transaction);
        if(acceptedPublisher.Value.EnvelopeSha256!=selection.Publishers.EnvelopeSha256 || acceptedSources.Value.EnvelopeSha256!=selection.Head.Sources.Reference.Sha256)
            throw new ControllerAuthorityConflictException();
        var revision=authority.AssertHumanSources(acceptedSources.Value.Configuration,transaction);
        var result=new CurrentCatalogActivation(stage.Id,selection.Pin.Encode(),fenced.ContentSha256,selection.Head.Sequence,fenced.Id,fenced.AccountRevision,
            fenced.Version!.Value,fenced.SharedDriveId,revision,selection.Publishers.Sequence,selection.Publishers.EnvelopeSha256,selection.Head.Sources.Sequence,
            selection.Head.Sources.Reference.Sha256,selection.Publishers.Publisher?.ActorId,selection.Publishers.Publisher?.Epoch,selection.Publishers.PublisherKeyId,
            selection.Head.Catalog,catalogSequence,commits,descriptor);
        var encoded=Encode(result);using var reservation=_budget.Reserve(checked(encoded.Length*3L+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
        Put(transaction,Key,encoded);Put(transaction,Key+":sha256",Hash(Encoding.UTF8.GetBytes(encoded)));
        token.ThrowIfCancellationRequested();acceptedPublisher.Recheck();acceptedSources.Recheck();reservation.RecheckBeforeWrite(reservation.Bytes);
        transaction.Commit();return result;
    }

    internal void AssertNotActiveIn(Guid id,SqliteTransaction transaction)
    {if(ReadIn(transaction)?.StageId==id) throw new InvalidOperationException("The active canonical index cannot be retired.");}

    private CurrentCatalogActivation? ReadIn(SqliteTransaction transaction)
    {
        var body=ReadValue(transaction,Key,8192);var hash=ReadValue(transaction,Key+":sha256",64);
        if(body is null && hash is null) return null;
        if(body is null || hash!=Hash(Encoding.UTF8.GetBytes(body))) throw new InvalidDataException("Active canonical selection lost its complete paired integrity record.");
        CurrentCatalogActivation value;
        try {value=JsonSerializer.Deserialize<CurrentCatalogActivation>(body,Json) ?? throw new InvalidDataException("Missing canonical selection.");}
        catch(JsonException error){throw new InvalidDataException("Canonical selection cannot be decoded.",error);}
        try
        {
            if(value.Format!="mpa-current-catalog-activation" || value.Major!=1 || value.Index is null)
                throw new InvalidDataException("Canonical selection has an unsupported format or missing index.");
            CurrentCatalogStageJournal.ValidateDescriptor(value.Index);var pin=LibraryConnectionDescriptor.Decode(value.ConnectionCode);
            if(pin.CurrentFileId is null || pin.Encode()!=value.ConnectionCode || value.StageId!=value.Index.Id || value.FencedAttempt==Guid.Empty ||
                value.RootSequence<1 || value.AccountRevision<1 || value.DriveVersion<1 || value.ConfigurationRevision<1 ||
                value.PublisherSequence<1 || value.SourceSequence<1 || value.PublisherSequence>=value.RootSequence || value.SourceSequence>=value.RootSequence ||
                !Digest(value.RootSha256) || !Digest(value.PublisherSha256) || !Digest(value.SourceSha256) ||
                value.SharedDriveId is not null && !Opaque(value.SharedDriveId) ||
                (value.PublisherId is null)!=(value.PublisherEpoch is null) || (value.PublisherId is null)!=(value.PublisherKeyId is null) ||
                value.PublisherId is not null && (value.PublisherId==Guid.Empty || value.PublisherEpoch<1 || !Digest(value.PublisherKeyId)) ||
                value.CatalogSequence is <0 or >CatalogCommitHistoryTraversal.MaximumCommits || value.Commits!=value.CatalogSequence ||
                value.Catalog is null && (value.CatalogSequence!=0 || value.Index.Assets!=0) || value.Catalog is not null && (value.CatalogSequence==0 || value.PublisherId is null) || Encode(value)!=body)
                throw new InvalidDataException("Canonical selection changed its bounded scope or phase.");
            var snapshot=InitialPolicyReadSnapshot.Capture(_database,_budget,transaction);
            var slot=new LibraryCurrentControlSlotRepository(_database,_budget).ReadIn(transaction,snapshot);
            if(slot is null || slot.ConnectionDescriptor().Encode()!=value.ConnectionCode) throw new InvalidDataException("Canonical selection lost its independently approved current slot.");
            var claim=new CurrentCatalogStageJournal(_database,_budget).ReadAllIn(transaction).SingleOrDefault(claim=>claim.Id==value.StageId);
            if(claim is null || claim.Phase!="sealed" || claim.Descriptor!=value.Index || claim.ConnectionCode!=value.ConnectionCode || claim.RootSha256!=value.RootSha256 ||
                claim.AccountRevision>value.AccountRevision || claim.PreparedVersion>value.DriveVersion || claim.SharedDriveId!=value.SharedDriveId)
                throw new InvalidDataException("Active canonical selection lost its exact sealed preparation.");
            // A receipt must still point at the retained native Root signature. It
            // remains historical even after this independent integrity verification.
            using var root=Command(transaction,"SELECT id FROM signing_intent WHERE actor_id=$actor AND epoch=$epoch AND sequence=$sequence",
                ("$actor",pin.ControllerId.ToString("D")),("$epoch",pin.ControllerEpoch),("$sequence",value.RootSequence));
            var raw=root.ExecuteScalar() as string;if(raw is null || !Guid.TryParseExact(raw,"D",out var id)) throw new InvalidDataException("Canonical selection lost its retained Root intent.");
            var intent=new ProtocolSigningJournal(_database,_budget).Read(id,transaction);
            if(intent?.Envelope is null) throw new InvalidDataException("Canonical selection lost its sealed Root envelope.");
            var head=LibraryCurrentControl.Read(intent.Envelope.Value.Span,pin);
            if(head.EnvelopeSha256!=value.RootSha256 || head.Publishers.Sequence!=value.PublisherSequence || head.Publishers.Reference.Sha256!=value.PublisherSha256 ||
                head.Sources.Sequence!=value.SourceSequence || head.Sources.Reference.Sha256!=value.SourceSha256 || head.Catalog!=value.Catalog)
                throw new InvalidDataException("Canonical selection no longer matches its signed Root.");
            var journal=new ProtocolSigningJournal(_database,_budget);
            var publisher=head.ReadPublishers(journal.RetainedControllerReference(pin,head.Publishers,"library-publisher-authorization",transaction).Span,pin);
            var sources=head.ReadSources(journal.RetainedControllerReference(pin,head.Sources,"library-source-configuration",transaction).Span,pin);
            if(publisher.Publisher?.ActorId!=value.PublisherId || publisher.Publisher?.Epoch!=value.PublisherEpoch || publisher.PublisherKeyId!=value.PublisherKeyId || sources.Revision!=value.ConfigurationRevision)
                throw new InvalidDataException("Canonical selection changed its signed publisher or source identity.");
        }
        catch(Exception error) when(error is ArgumentException or ProtocolFormatException or ProtocolCompatibilityException)
        {throw new InvalidDataException("Canonical selection exceeds its schema or authority bounds.",error);}
        return value;
    }
    private string? ReadValue(SqliteTransaction transaction,string key,int limit)
    {
        using var command=Command(transaction,"SELECT length(CAST(value AS BLOB)),value FROM processor_meta WHERE key=$key",("$key",key));using var row=command.ExecuteReader();
        if(!row.Read()) return null;if(row.GetInt64(0) is <1 || row.GetInt64(0)>limit) throw new InvalidDataException("Canonical selection exceeds its retained byte bound.");return row.GetString(1);
    }
    private void Put(SqliteTransaction transaction,string key,string value)
    {
        using var command=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",key),("$value",value));
        if(command.ExecuteNonQuery()!=1) throw new InvalidOperationException("Canonical selection did not update exactly one paired row.");
    }
    private SqliteCommand Command(SqliteTransaction transaction,string sql,params (string Key,object Value)[] values)
    {var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;foreach(var pair in values) command.Parameters.AddWithValue(pair.Key,pair.Value);return command;}
    private static string Encode(CurrentCatalogActivation value)=>Encoding.UTF8.GetString(new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(value,Json)));
    private static string Hash(ReadOnlySpan<byte> value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    private static bool Digest(string? value)=>value is {Length:64} && value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Opaque(string value)=>value is {Length:>0 and <=256} && value!="root" && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
