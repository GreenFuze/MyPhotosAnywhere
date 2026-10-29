using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical unsigned partial-work disposition. Original pending objects
/// remain immutable; no native counter, selected completion or cloud cleanup exists.</summary>
public sealed record CurrentUnsignedCatalogDisposition(WorkspacePublicationScope Scope,Guid SnapshotId,Guid CommitIntentId,
    string OriginalPhase,CatalogObjectReference? SelectedAnchor,long HumanConfigurationRevision,string HumanConfigurationSha256,
    CurrentWorkspaceInventoryCheckpoint Inventory,string PublicationSha256,string OriginalWorkSha256)
{
    public string Format {get;init;}="mpa-current-unsigned-generation-disposition";
    public int Major {get;init;}=1;
}

public sealed partial class CatalogGenerationCaptureRepository
{
    private const string UnsignedDispositionPrefix="current_unsigned_generation_disposition_v1:";

    public CurrentUnsignedCatalogDisposition? UnsignedDisposition(Guid generationId,CancellationToken token=default)
    {
        Id(generationId);using var transaction=_database.Connection.BeginTransaction();var capture=Read(generationId,transaction);
        var result=capture is null ? null : ReadUnsignedCurrentDisposition(capture,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    public CurrentUnsignedCatalogDisposition DisposeUnsignedCurrentGeneration(Guid generationId,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();token.ThrowIfCancellationRequested();
        var capture=RequiredCapture(generationId,transaction);var old=ReadUnsignedCurrentDisposition(capture,transaction,token);
        if(old is not null) {token.ThrowIfCancellationRequested();transaction.Commit();return old;}
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        if(capture.Phase is not ("publication-planned" or "dependencies-ready")) throw new CatalogGenerationCaptureConflictException();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(fenced,transaction,token);
        var human=new LibrarySourceRepository(_database).Snapshot(transaction);var scope=capture.Scope;
        if(human.Revision<=scope.ConfigurationRevision) throw new StaleLibraryConfigurationException();
        var humanHash=HashPredecessorHumanSources(human);
        if(ReadArchivedPredecessorHumanSources(human.Revision,transaction)!=humanHash)
            throw new InvalidDataException("Authored source bindings changed without their archived revision.");
        var publication=ReadPublication(capture,transaction) ?? throw new CatalogGenerationCaptureConflictException();
        if(new OwnedCatalogLineageReader(this,scope,transaction,token).Publisher.Sequence!=publication.Basis.ParentSequence)
            throw new SigningHistoryRecoveryRequiredException();
        var head=ReadPublishedHead(transaction);
        if(basis.Head.Catalog is null ? head is not null : head is null || head.Commit!=basis.Head.Catalog)
            throw new CatalogGenerationCaptureConflictException();
        var value=new CurrentUnsignedCatalogDisposition(scope,capture.SnapshotId,capture.CommitIntentId,capture.Phase,basis.Head.Catalog,
            human.Revision,humanHash,inventory,UnsignedPublicationHash(publication),UnsignedOriginalWorkHash(capture,transaction,token));
        AssertUnsignedRootContext(capture,publication,value,transaction,token);
        var text=EncodeUnsignedDisposition(value);var digest=UnsignedDispositionHash(capture,text);
        using var storage=_budget.Reserve(checked(Encoding.UTF8.GetByteCount(text)*3L+65536));storage.RecheckBeforeWrite(storage.Bytes);
        var key=UnsignedDispositionPrefix+generationId.ToString("D");
        foreach(var pair in new[]{(Key:key,Value:text),(Key:key+":sha256",Value:digest)})
        {
            using var insert=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value)",("$key",pair.Key),("$value",pair.Value));
            if(insert.ExecuteNonQuery()!=1) throw new InvalidOperationException("Unsigned disposition lost its exact original owner.");
        }
        ChangePhase(capture,"complete",transaction);
        _=ReadUnsignedCurrentDisposition(capture with {Phase="complete"},transaction,token)
            ?? throw new InvalidDataException("Unsigned disposition lost its atomic phase.");
        token.ThrowIfCancellationRequested();storage.RecheckBeforeWrite(storage.Bytes);transaction.Commit();return value;
    }

    public CatalogGenerationCapture BeginUnsignedSnapshotRetirement(Guid generationId)
    {
        Id(generationId);using var transaction=_database.Connection.BeginTransaction();var capture=RequiredCapture(generationId,transaction);
        if(capture.Phase is not ("complete" or "snapshot-retiring") || ReadUnsignedCurrentDisposition(capture,transaction,CancellationToken.None) is null)
            throw new CatalogGenerationCaptureConflictException();
        using var storage=_budget.Reserve(65536);storage.RecheckBeforeWrite(storage.Bytes);
        if(capture.Phase=="complete") ChangePhase(capture,"snapshot-retiring",transaction);
        storage.RecheckBeforeWrite(storage.Bytes);transaction.Commit();return capture with {Phase="snapshot-retiring"};
    }

    public CatalogGenerationCapture ConfirmUnsignedSnapshotRetired(CatalogSnapshotRetirement actual)
    {
        ArgumentNullException.ThrowIfNull(actual);using var transaction=_database.Connection.BeginTransaction();
        var capture=RequiredCapture(actual.Capture.Scope.GenerationId,transaction);actual.AssertOwned(capture);
        if(capture.Phase!="snapshot-retiring" || ReadUnsignedCurrentDisposition(capture,transaction,CancellationToken.None) is null)
            throw new CatalogGenerationCaptureConflictException();
        using var storage=_budget.Reserve(65536);storage.RecheckBeforeWrite(storage.Bytes);
        ChangePhase(capture,"retired",transaction);storage.RecheckBeforeWrite(storage.Bytes);transaction.Commit();actual.Confirm();return capture with {Phase="retired"};
    }

    // Original fields are already validated by the enclosing reader. This explicit
    // variant validates its original pre-terminal phase instead of inventing a plan
    // or signed intent to satisfy selected/materialized completion invariants.
    private CurrentUnsignedCatalogDisposition? ReadUnsignedCurrentDisposition(CatalogGenerationCapture capture,SqliteTransaction? transaction,CancellationToken token)
    {
        AssertExclusiveCurrentDisposition(capture,transaction);
        var key=UnsignedDispositionPrefix+capture.Scope.GenerationId.ToString("D");string? text=null;string? digest=null;
        using(var query=Command(transaction,"SELECT key,length(CAST(value AS BLOB)),value FROM processor_meta WHERE key=$key OR key=$hash",("$key",key),("$hash",key+":sha256")))
        using(var reader=query.ExecuteReader()) while(reader.Read())
        {
            if(reader.GetInt64(1) is <1 or >49152) throw new InvalidDataException("Unsigned disposition exceeds its bound.");
            if(reader.GetString(0)==key) text=reader.GetString(2);else digest=reader.GetString(2);
        }
        if(text is null && digest is null) return null;
        if(text is null || digest is null || !Digest(digest)) throw new InvalidDataException("Unsigned disposition lost its complete integrity binding.");
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            var value=JsonSerializer.Deserialize<CurrentUnsignedCatalogDisposition>(text,CurrentCompletionJson)
                ?? throw new InvalidDataException("Unsigned disposition is missing.");
            if(value.Format!="mpa-current-unsigned-generation-disposition" || value.Major!=1 || value.Scope!=capture.Scope ||
                value.SnapshotId!=capture.SnapshotId || value.CommitIntentId!=capture.CommitIntentId || value.OriginalPhase is not ("publication-planned" or "dependencies-ready") ||
                capture.Phase is not ("complete" or "snapshot-retiring" or "retired") || capture.Snapshot is null || value.Inventory is null ||
                value.HumanConfigurationRevision<=capture.Scope.ConfigurationRevision || !Digest(value.HumanConfigurationSha256) ||
                value.HumanConfigurationSha256!=ReadArchivedPredecessorHumanSources(value.HumanConfigurationRevision,transaction) ||
                EncodeUnsignedDisposition(value)!=text || UnsignedDispositionHash(capture,text)!=digest)
                throw new InvalidDataException("Unsigned disposition changed its original scope, phase or human revision.");
            var publication=ReadPublication(capture with {Phase=value.OriginalPhase},transaction)
                ?? throw new InvalidDataException("Unsigned disposition lost its original publication.");
            if(UnsignedPublicationHash(publication)!=value.PublicationSha256 || UnsignedOriginalWorkHash(capture,transaction,token)!=value.OriginalWorkSha256)
                throw new InvalidDataException("Unsigned disposition changed original immutable publication or pending work.");
            AssertUnsignedRootContext(capture,publication,value,transaction,token);token.ThrowIfCancellationRequested();return value;
        }
        catch(Exception error) when(error is JsonException or ArgumentException or ProtocolFormatException or OverflowException)
        {throw new InvalidDataException("Unsigned disposition cannot be decoded safely.",error);}
    }

    private void AssertUnsignedRootContext(CatalogGenerationCapture capture,CapturedCatalogPublication publication,CurrentUnsignedCatalogDisposition value,
        SqliteTransaction transaction,CancellationToken token)
    {
        var scope=capture.Scope;var inventory=value.Inventory;
        new WorkspaceObservationRepository(_database,_budget).AssertHistoricalCurrentInventoryIn(inventory,transaction,token);
        if(inventory.Context.LibraryId!=scope.LibraryId || inventory.Context.AccountId!=scope.AccountId || inventory.Context.FolderId!=scope.FolderId ||
            inventory.Context.AccountRevision<scope.AccountRevision) throw new CatalogGenerationCaptureConflictException();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var journal=new ProtocolSigningJournal(_database,_budget);var rootIntent=journal.Read(inventory.RootIntentId,transaction)
            ?? throw new InvalidDataException("Unsigned disposition lost its observed Root.");
        var root=LibraryCurrentControl.Read(rootIntent.Envelope!.Value.Span,basis.Pin);
        var publishers=root.ReadPublishers(journal.RetainedControllerReference(basis.Pin,root.Publishers,"library-publisher-authorization",transaction).Span,basis.Pin);
        var sources=root.ReadSources(journal.RetainedControllerReference(basis.Pin,root.Sources,"library-source-configuration",transaction).Span,basis.Pin);
        var lineage=new OwnedCatalogLineageReader(this,scope,transaction,token);
        if(root.Catalog!=value.SelectedAnchor || publishers.Publisher is not {} actor || actor.ActorId!=scope.ActorId || actor.Epoch!=scope.ActorEpoch ||
            publishers.PublisherKeyId!=lineage.Publisher.KeyId || sources.Revision>value.HumanConfigurationRevision ||
            lineage.Publisher.Sequence<publication.Basis.ParentSequence)
            throw new InvalidDataException("Unsigned disposition substituted its actual selected anchor or publisher/source policy.");
        lineage.RequirePath(publication.Basis.Parent,publication.Basis.ParentSequence,value.SelectedAnchor);
        new CurrentControlTransitionJournal(_database,_budget).AssertUnsignedCatalogHasNoRootIn(capture,transaction,token);
    }

    private string UnsignedOriginalWorkHash(CatalogGenerationCapture capture,SqliteTransaction transaction,CancellationToken token)
    {
        var scope=capture.Scope;var journal=new ProtocolSigningJournal(_database,_budget);
        if(journal.Read(capture.CommitIntentId,transaction) is not null) throw new CatalogGenerationCaptureConflictException();
        using(var query=Command(transaction,"SELECT (SELECT count(*) FROM catalog_generation_verification WHERE id=$id)+(SELECT count(*) FROM processor_meta WHERE key GLOB $selection OR key GLOB $supersession)",
            ("$id",scope.GenerationId.ToString("D")),("$selection","current_catalog_generation_selection_v1:"+scope.GenerationId.ToString("D")+"*"),("$supersession",SupersessionPrefix+scope.GenerationId.ToString("D")+"*")))
            if((long)query.ExecuteScalar()! !=0) throw new CatalogGenerationCaptureConflictException();
        var outbox=new WorkspacePublicationRepository(_database,_budget);var count=0;
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData("mpa-current-unsigned-original-work-v1\0"u8);
        foreach(var id in outbox.Identities(WorkspaceObservationRepository.MaximumDocuments,transaction))
        {
            token.ThrowIfCancellationRequested();var row=outbox.Read(id,transaction) ?? throw new WorkspacePublicationConflictException();
            if(row.Scope.GenerationId!=scope.GenerationId) continue;
            if(row.Scope!=scope || row.Role is not ("facts-index" or "facts-chunk" or "coverage-root" or "coverage-page")) throw new CatalogGenerationCaptureConflictException();
            Append(new WorkspacePublicationExpectation(row.Id,row.Scope,row.Role,row.MimeType,row.Payload.Length,row.Sha256,row.FileId,row.VerifiedVersion));count=checked(count+1);
        }
        foreach(var reference in new CatalogDependencyReferenceRepository(_database,_budget).ForScope(scope,transaction).OrderBy(v=>v.Id.ToString("D"),StringComparer.Ordinal))
        {token.ThrowIfCancellationRequested();Append(reference);count=checked(count+1);}
        if(count==0) throw new CatalogGenerationCaptureConflictException();
        Append(new {scope,capture.SnapshotId,capture.CommitIntentId,Count=count});return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append<T>(T value)
        {
            var bytes=new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(value,CurrentCompletionJson));
            Span<byte> size=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(size,bytes.Length);hash.AppendData(size);hash.AppendData(bytes);
        }
    }
    private static string UnsignedPublicationHash(CapturedCatalogPublication value)=>Hash(new CanonicalJson(32768)
        .Encode(JsonSerializer.SerializeToUtf8Bytes(new {value.Basis,value.Plan},CurrentCompletionJson)));
    private static string EncodeUnsignedDisposition(CurrentUnsignedCatalogDisposition value)=>Encoding.UTF8.GetString(new CanonicalJson(49152)
        .Encode(JsonSerializer.SerializeToUtf8Bytes(value,CurrentCompletionJson)));
    private static string UnsignedDispositionHash(CatalogGenerationCapture capture,string text)=>Hash(new CanonicalJson(65536)
        .Encode(JsonSerializer.SerializeToUtf8Bytes(new {domain="mpa-current-unsigned-disposition-integrity-v1",capture.Scope,capture.SnapshotId,capture.CommitIntentId,text},CurrentCompletionJson)));
}
