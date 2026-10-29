using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>First historical verified selection of this exact owned generation.
/// Distinct from legacy completion, current authority and snapshot retirement.
/// The original scope/signature/allocation survive later stages and access changes.</summary>
public sealed record CurrentCatalogGenerationSelection(Guid GenerationId,WorkspacePublicationScope Scope,Guid SnapshotId,Guid CommitIntentId,
    long Sequence,long Assets,CurrentWorkspaceInventoryCheckpoint Inventory,CurrentCatalogActivation Activation,WorkspaceDocumentObservation Commit)
{
    public string Format {get;init;}="mpa-current-generation-selection";
    public int Major {get;init;}=1;
}

internal sealed record CurrentOwnedCatalogSelection(CapturedCatalogPublication Publication,ProtocolSigningIntent Intent,
    WorkspacePublicationRecord Work,string PlanSha256);

public sealed partial class CatalogGenerationCaptureRepository
{
    internal CurrentOwnedCatalogSelection SelectionInputsIn(Guid generationId,SqliteTransaction transaction)
    {
        return SelectionInputsIn(RequiredCapture(generationId,transaction),transaction);
    }

    // The enclosing capture reader has already validated identity/scope/snapshot
    // and publication. Borrow it while validating a typed disposition so that its
    // phase validator cannot recursively call itself through RequiredCapture.
    internal CurrentOwnedCatalogSelection SelectionInputsIn(CatalogGenerationCapture capture,SqliteTransaction transaction)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Original inputs require their owning transaction.");
        if(capture.Phase is not ("commit-sealed" or "commit-verified" or "complete" or "snapshot-retiring" or "retired"))
            throw new CatalogGenerationCaptureConflictException();
        var publication=ReadPublication(capture,transaction) ?? throw new CatalogGenerationCaptureConflictException();
        var intent=new ProtocolSigningJournal(_database,_budget).Read(capture.CommitIntentId,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
        var work=CommitWork(capture,intent,transaction);
        return new(publication,intent,work,PlanHash(capture,publication.Basis,PublicationEncode(publication.Plan!)));
    }
}

/// <summary>Bounded historical receipt journal. The actual activation owner records
/// it before its Root terminal/pointer in the SAME transaction. Reads are historical
/// even while disconnected; neither receipt nor phase grants fresh access or cleanup.</summary>
public sealed class CurrentCatalogGenerationSelectionRepository(ProcessorStateDatabase database,StorageBudgetManager budget)
{
    private readonly ProcessorStateDatabase _database=database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private const string Prefix="current_catalog_generation_selection_v1:";
    private const int MaximumBytes=49152;
    private static readonly JsonSerializerOptions Json=new() {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    public CurrentCatalogGenerationSelection? Read(Guid generationId,CancellationToken token=default)
    {
        if(generationId==Guid.Empty) throw new ArgumentException("An original generation identity is required.",nameof(generationId));
        using var transaction=_database.Connection.BeginTransaction();var result=ReadIn(generationId,null,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    internal StorageReservation? RecordSelectedIn(CurrentCatalogActivation activation,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,WorkspacePublicationInventory publications,LocalCurrentControlAttempt fenced,
        SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Selection must borrow its actual activation transaction.");
        if(activation.Catalog is not {} selected) return null;
        var ids=new List<Guid>();
        using(var query=Command(transaction,"SELECT id FROM workspace_publication WHERE file_id=$file AND role='catalog-commit' ORDER BY id LIMIT 2",("$file",selected.FileId)))
        using(var reader=query.ExecuteReader()) while(reader.Read()) ids.Add(Guid.ParseExact(reader.GetString(0),"D"));
        if(ids.Count==0) return null; // A selected foreign publisher is not an owned outgoing capture.
        if(ids.Count!=1) throw new WorkspacePublicationConflictException();
        var work=new WorkspacePublicationRepository(_database,_budget).Read(ids[0],transaction) ?? throw new WorkspacePublicationConflictException();
        var inputs=new CatalogGenerationCaptureRepository(_database,_budget).SelectionInputsIn(work.Scope.GenerationId,transaction);
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var known=new WorkspaceObservationRepository(_database,_budget).Known(inventory.Context,transaction).ToDictionary(value=>value.FileId,StringComparer.Ordinal);
        foreach(var item in publications.Objects.Where(value=>value.Scope.GenerationId==work.Scope.GenerationId))
        {
            if(item.Scope!=work.Scope || item.FileId is null || item.VerifiedVersion is null || !known.TryGetValue(item.FileId,out var actual) ||
                actual.Version<item.VerifiedVersion || actual.Bytes!=item.Bytes || actual.Sha256!=item.Sha256 || (actual.MimeType ?? "application/json")!=item.MimeType)
                throw new WorkspaceObservationConflictException();
        }
        foreach(var dependency in new CatalogDependencyReferenceRepository(_database,_budget).ForScope(work.Scope,transaction))
        {
            var original=new WorkspacePublicationRepository(_database,_budget).Read(dependency.OriginalPublicationId,transaction) ?? throw new WorkspacePublicationConflictException();
            if(original.VerifiedVersion is null || !known.TryGetValue(dependency.FileId,out var actual) || actual.Version<original.VerifiedVersion ||
                actual.Bytes!=dependency.Bytes || actual.Sha256!=dependency.Sha256 || (actual.MimeType ?? "application/json")!=dependency.MimeType)
                throw new WorkspaceObservationConflictException();
        }
        if(!known.TryGetValue(selected.FileId,out var commit)) throw new WorkspaceObservationConflictException();
        var capture=inputs.Publication.Capture;
        var value=new CurrentCatalogGenerationSelection(work.Scope.GenerationId,work.Scope,capture.SnapshotId,capture.CommitIntentId,
            inputs.Intent.Sequence,capture.Snapshot!.Assets,inventory,activation,commit);
        ValidateIn(value,inputs,transaction,token);
        var old=ReadIn(value.GenerationId,inputs,transaction,token);
        if(old is not null)
        {
            if(old.Scope!=value.Scope || old.SnapshotId!=value.SnapshotId || old.CommitIntentId!=value.CommitIntentId || old.Sequence!=value.Sequence ||
                old.Assets!=value.Assets || old.Commit.FileId!=value.Commit.FileId || old.Commit.Bytes!=value.Commit.Bytes || old.Commit.Sha256!=value.Commit.Sha256 ||
                old.Commit.Version>value.Commit.Version) throw new CatalogGenerationCaptureConflictException();
            return null; // Keep the first historical actual selection, never relabel it.
        }
        var encoded=Encode(value);StorageReservation? reservation=_budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded)*3L+65536));
        try
        {
            reservation.RecheckBeforeWrite(reservation.Bytes);
            Put(transaction,Key(value.GenerationId),encoded);Put(transaction,Key(value.GenerationId)+":sha256",Hash(inputs.PlanSha256,encoded));
            token.ThrowIfCancellationRequested();var owned=reservation;reservation=null;return owned;
        }
        finally {reservation?.Dispose();}
    }

    internal CurrentCatalogGenerationSelection? ReadForCaptureIn(CurrentOwnedCatalogSelection inputs,SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Historical selection requires its owning transaction.");
        return ReadIn(inputs.Publication.Capture.Scope.GenerationId,inputs,transaction,token);
    }

    internal static string HistoricalSha256(CurrentCatalogGenerationSelection value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(value))));

    private CurrentCatalogGenerationSelection? ReadIn(Guid generationId,CurrentOwnedCatalogSelection? supplied,SqliteTransaction transaction,CancellationToken token)
    {
        var text=Value(transaction,Key(generationId),MaximumBytes);var sha=Value(transaction,Key(generationId)+":sha256",64);
        if(text is null && sha is null) return null;
        if(text is null || sha is null) throw new InvalidDataException("Current selection lost its complete receipt.");
        CurrentCatalogGenerationSelection value;
        try {value=JsonSerializer.Deserialize<CurrentCatalogGenerationSelection>(text,Json) ?? throw new InvalidDataException("Missing current generation selection.");}
        catch(JsonException error) {throw new InvalidDataException("Current generation selection cannot be decoded.",error);}
        if(value.GenerationId!=generationId || value.Format!="mpa-current-generation-selection" || value.Major!=1 || value.Scope is null ||
            value.Inventory is null || value.Activation is null || value.Activation.Index is null || value.Commit is null || Encode(value)!=text)
            throw new InvalidDataException("Current generation selection changed its explicit format or identity.");
        var inputs=supplied ?? new CatalogGenerationCaptureRepository(_database,_budget).SelectionInputsIn(generationId,transaction);
        if(Hash(inputs.PlanSha256,text)!=sha) throw new InvalidDataException("Current selection lost its original plan integrity binding.");
        ValidateIn(value,inputs,transaction,token);return value;
    }

    private void ValidateIn(CurrentCatalogGenerationSelection value,CurrentOwnedCatalogSelection inputs,SqliteTransaction transaction,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var capture=inputs.Publication.Capture;var work=inputs.Work;var inventory=value.Inventory;var activation=value.Activation;
        new WorkspaceObservationRepository(_database,_budget).AssertHistoricalCurrentInventoryIn(inventory,transaction,token);
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var rootIntent=new ProtocolSigningJournal(_database,_budget).Read(inventory.RootIntentId,transaction) ?? throw new InvalidDataException("Current selection lost its native Root.");
        var root=LibraryCurrentControl.Read(rootIntent.Envelope!.Value.Span,basis.Pin);
        var publisherBytes=new ProtocolSigningJournal(_database,_budget).RetainedControllerReference(basis.Pin,root.Publishers,"library-publisher-authorization",transaction);
        var sourceBytes=new ProtocolSigningJournal(_database,_budget).RetainedControllerReference(basis.Pin,root.Sources,"library-source-configuration",transaction);
        var publisher=root.ReadPublishers(publisherBytes.Span,basis.Pin);var sources=root.ReadSources(sourceBytes.Span,basis.Pin);
        var reference=new CatalogObjectReference(work.Scope.AccountId,work.FileId!,work.Scope.FolderId,work.Payload.Length,work.Sha256);
        CurrentCatalogStageJournal.ValidateDescriptor(activation.Index);
        if(value.Scope!=capture.Scope || value.GenerationId!=capture.Scope.GenerationId || value.SnapshotId!=capture.SnapshotId || value.CommitIntentId!=capture.CommitIntentId ||
            value.Sequence!=inputs.Intent.Sequence || value.Assets!=capture.Snapshot!.Assets || inventory.Context.LibraryId!=work.Scope.LibraryId ||
            inventory.Context.AccountId!=work.Scope.AccountId || inventory.Context.FolderId!=work.Scope.FolderId || inventory.Context.AccountRevision<work.Scope.AccountRevision ||
            activation.Format!="mpa-current-catalog-activation" || activation.Major!=1 || activation.ConnectionCode!=basis.Pin.Encode() || activation.StageId!=activation.Index.Id ||
            activation.FencedAttempt!=inventory.RootAttemptId || activation.RootSha256!=inventory.RootSha256 || activation.RootSequence!=inventory.RootSequence ||
            activation.DriveVersion!=inventory.RootVersion || activation.AccountRevision!=inventory.Context.AccountRevision || activation.SharedDriveId!=inventory.Context.SharedDriveId ||
            activation.Catalog!=reference || root.Catalog!=reference || activation.CatalogSequence!=value.Sequence || activation.Commits!=value.Sequence || activation.Index.Assets!=value.Assets ||
            activation.ConfigurationRevision!=work.Scope.ConfigurationRevision || sources.Revision!=work.Scope.ConfigurationRevision ||
            activation.PublisherSequence!=root.Publishers.Sequence || activation.PublisherSha256!=root.Publishers.Reference.Sha256 ||
            activation.SourceSequence!=root.Sources.Sequence || activation.SourceSha256!=root.Sources.Reference.Sha256 ||
            activation.SourceSha256!=inputs.Publication.Basis.SourcePolicySha256 || publisher.Publisher is not {} authorised || authorised.ActorId!=work.Scope.ActorId || authorised.Epoch!=work.Scope.ActorEpoch ||
            activation.PublisherId!=work.Scope.ActorId || activation.PublisherEpoch!=work.Scope.ActorEpoch || activation.PublisherKeyId!=publisher.PublisherKeyId ||
            value.Commit.FileId!=reference.FileId || value.Commit.Version<work.VerifiedVersion || value.Commit.Bytes!=reference.Bytes || value.Commit.Sha256!=reference.Sha256 ||
            (value.Commit.MimeType ?? "application/json")!="application/json") throw new InvalidDataException("Current generation selection changed its original native scope, graph or actual Root binding.");
    }

    private string? Value(SqliteTransaction transaction,string key,int maximum)
    {
        using var query=Command(transaction,"SELECT length(value),value FROM processor_meta WHERE key=$key",("$key",key));using var reader=query.ExecuteReader();
        if(!reader.Read()) return null;if(reader.GetInt64(0) is <1 || reader.GetInt64(0)>maximum) throw new InvalidDataException("Current selection receipt exceeds its bound.");return reader.GetString(1);
    }
    private void Put(SqliteTransaction transaction,string key,string value)
    {using var insert=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value)",("$key",key),("$value",value));if(insert.ExecuteNonQuery()!=1) throw new InvalidOperationException("Current selection lost its exact receipt owner.");}
    private SqliteCommand Command(SqliteTransaction transaction,string sql,params (string Key,object Value)[] values)
    {var command=_database.Connection.CreateCommand();command.Transaction=transaction;command.CommandText=sql;foreach(var value in values) command.Parameters.AddWithValue(value.Key,value.Value);return command;}
    private static string Key(Guid id)=>Prefix+id.ToString("D");
    private static string Encode(CurrentCatalogGenerationSelection value)=>Encoding.UTF8.GetString(new CanonicalJson(MaximumBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(value,Json)));
    private static string Hash(string plan,string encoded)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mpa-current-generation-selection-v1\n"+plan+"\n"+encoded)));
}
