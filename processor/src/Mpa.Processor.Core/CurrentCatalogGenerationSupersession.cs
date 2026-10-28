using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical disposition of exact materialized work that is currently
/// unselected after a human source change. Never a publisher head, fresh authority,
/// original-file permission or permission to upload obsolete facts.</summary>
public sealed record CurrentCatalogGenerationSupersession(WorkspacePublicationScope Scope,Guid SnapshotId,Guid CommitIntentId,
    long Sequence,CatalogObjectReference OriginalCommit,CatalogObjectReference? SelectedAnchor,long HumanConfigurationRevision,
    string HumanConfigurationSha256,CurrentWorkspaceInventoryCheckpoint Inventory,int WithdrawnRootPlans,
    string RootRecoverySha256,string OriginalWorkSha256)
{
    public string Format {get;init;}="mpa-current-generation-supersession";
    public int Major {get;init;}=1;
}

public sealed partial class CatalogGenerationCaptureRepository
{
    private const string SupersessionPrefix="current_catalog_generation_supersession_v1:";

    public CurrentCatalogGenerationSupersession? Supersession(Guid generationId,CancellationToken token=default)
    {
        Id(generationId);using var transaction=_database.Connection.BeginTransaction();var capture=Read(generationId,transaction);
        var result=capture is null ? null : ReadCurrentSupersession(capture,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    /// <summary>The current inventory owner calls this after its actual final fence.
    /// It rechecks the live same-transaction gate, records only historical metadata
    /// and preserves every original scope, outbox identity, counter and payload.</summary>
    public CurrentCatalogGenerationSupersession SupersedeMaterializedCurrentGeneration(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();token.ThrowIfCancellationRequested();
        var capture=RequiredCapture(generationId,transaction);var old=ReadCurrentSupersession(capture,transaction,token);
        if(old is not null) {token.ThrowIfCancellationRequested();transaction.Commit();return old;}
        var actual=InspectMaterializedPredecessorIn(generationId,inventory,signing,fenced,publications,transaction,token);
        if(ReadArchivedPredecessorHumanSources(actual.HumanConfigurationRevision,transaction)!=actual.HumanConfigurationSha256)
            throw new InvalidDataException("Human source bindings changed without their original archived revision.");
        var inputs=SelectionInputsIn(capture,transaction);
        var recovery=new CurrentControlTransitionJournal(_database,_budget).ReadOwnedCatalogRootRecoveryIn(inputs,transaction,token);
        if(recovery.WithdrawnPlans!=actual.WithdrawnRootPlans) throw new InvalidDataException("Supersession lost a captured Root withdrawal.");
        var value=new CurrentCatalogGenerationSupersession(capture.Scope,capture.SnapshotId,capture.CommitIntentId,actual.Sequence,
            actual.OriginalCommit,actual.SelectedAnchor,actual.HumanConfigurationRevision,actual.HumanConfigurationSha256,inventory,
            recovery.WithdrawnPlans,recovery.Sha256,OriginalSupersededWorkSha256(inputs,transaction,token));
        var text=EncodeSupersession(value);var digest=SupersessionHash(inputs,text);
        using var storage=_budget.Reserve(checked(Encoding.UTF8.GetByteCount(text)*3L+65536));storage.RecheckBeforeWrite(storage.Bytes);
        var key=SupersessionPrefix+generationId.ToString("D");
        foreach(var row in new[]{(Key:key,Value:text),(Key:key+":sha256",Value:digest)})
        {
            using var insert=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value)",("$key",row.Key),("$value",row.Value));
            if(insert.ExecuteNonQuery()!=1) throw new InvalidOperationException("Supersession lost its exact disposition owner.");
        }
        ChangePhase(capture,"complete",transaction);
        _=ReadCurrentSupersession(capture with {Phase="complete"},transaction,token) ?? throw new InvalidDataException("Supersession lost its atomic record.");
        token.ThrowIfCancellationRequested();storage.RecheckBeforeWrite(storage.Bytes);transaction.Commit();return value;
    }

    public CatalogGenerationCapture BeginSupersededSnapshotRetirement(Guid generationId)
    {
        Id(generationId);using var transaction=_database.Connection.BeginTransaction();var capture=RequiredCapture(generationId,transaction);
        if(capture.Phase is not ("complete" or "snapshot-retiring") || ReadCurrentSupersession(capture,transaction,CancellationToken.None) is null)
            throw new CatalogGenerationCaptureConflictException();
        using var storage=_budget.Reserve(65536);storage.RecheckBeforeWrite(storage.Bytes);
        if(capture.Phase=="complete") ChangePhase(capture,"snapshot-retiring",transaction);
        transaction.Commit();return capture with {Phase="snapshot-retiring"};
    }

    public CatalogGenerationCapture ConfirmSupersededSnapshotRetired(CatalogSnapshotRetirement actual)
    {
        ArgumentNullException.ThrowIfNull(actual);using var transaction=_database.Connection.BeginTransaction();
        var capture=RequiredCapture(actual.Capture.Scope.GenerationId,transaction);actual.AssertOwned(capture);
        if(capture.Phase!="snapshot-retiring" || ReadCurrentSupersession(capture,transaction,CancellationToken.None) is null)
            throw new CatalogGenerationCaptureConflictException();
        using var storage=_budget.Reserve(65536);storage.RecheckBeforeWrite(storage.Bytes);
        ChangePhase(capture,"retired",transaction);transaction.Commit();actual.Confirm();return capture with {Phase="retired"};
    }

    private CurrentCatalogGenerationSupersession? ReadCurrentSupersession(CatalogGenerationCapture capture,SqliteTransaction? transaction,CancellationToken token)
    {
        AssertExclusiveCurrentDisposition(capture,transaction);
        var value=ReadOriginalCurrentSupersession(capture,transaction,token);
        if(value is null) return null;
        ArgumentNullException.ThrowIfNull(transaction);
        var inputs=SelectionInputsIn(capture,transaction);
        new OwnedCatalogLineageReader(this,capture.Scope,transaction,token).RequirePath(inputs.Publication.Basis.Parent,
            inputs.Publication.Basis.ParentSequence,value.SelectedAnchor);
        return value;
    }

    // Does not recursively load earlier capture dispositions. Only the iterative
    // lineage owner may consume this original binding without the wrapper above.
    private CurrentCatalogGenerationSupersession? ReadOriginalCurrentSupersession(CatalogGenerationCapture capture,SqliteTransaction? transaction,CancellationToken token)
    {
        var key=SupersessionPrefix+capture.Scope.GenerationId.ToString("D");string? text=null;string? digest=null;
        using(var query=Command(transaction,"SELECT key,length(CAST(value AS BLOB)),value FROM processor_meta WHERE key=$key OR key=$hash",("$key",key),("$hash",key+":sha256")))
        using(var reader=query.ExecuteReader())
            while(reader.Read())
            {
                if(reader.GetInt64(1) is <1 or >49152) throw new InvalidDataException("Supersession exceeds its bounded record.");
                if(reader.GetString(0)==key) text=reader.GetString(2);else digest=reader.GetString(2);
            }
        if(text is null && digest is null) return null;
        if(text is null || digest is null || !Digest(digest)) throw new InvalidDataException("Supersession lost its complete integrity record.");
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            var value=JsonSerializer.Deserialize<CurrentCatalogGenerationSupersession>(text,CurrentCompletionJson)
                ?? throw new InvalidDataException("Supersession is missing.");
            var inputs=SelectionInputsIn(capture,transaction);var scope=capture.Scope;
            if(value.Format!="mpa-current-generation-supersession" || value.Major!=1 || value.Scope!=scope || value.SnapshotId!=capture.SnapshotId ||
                value.CommitIntentId!=capture.CommitIntentId || capture.Phase is not ("complete" or "snapshot-retiring" or "retired") ||
                value.Sequence!=inputs.Intent.Sequence || value.Sequence!=checked(inputs.Publication.Basis.ParentSequence+1) ||
                value.OriginalCommit!=new CatalogObjectReference(scope.AccountId,inputs.Work.FileId!,scope.FolderId,inputs.Work.Payload.Length,inputs.Work.Sha256) ||
                value.SelectedAnchor==value.OriginalCommit ||
                value.HumanConfigurationRevision<=scope.ConfigurationRevision || !Digest(value.HumanConfigurationSha256) ||
                value.HumanConfigurationSha256!=ReadArchivedPredecessorHumanSources(value.HumanConfigurationRevision,transaction) ||
                value.Inventory is null || value.Inventory.Context is null || value.Inventory.Context.LibraryId!=scope.LibraryId || value.Inventory.Context.AccountId!=scope.AccountId ||
                value.Inventory.Context.FolderId!=scope.FolderId || value.Inventory.Context.AccountRevision<scope.AccountRevision ||
                EncodeSupersession(value)!=text || SupersessionHash(inputs,text)!=digest ||
                value.OriginalWorkSha256!=OriginalSupersededWorkSha256(inputs,transaction,token))
                throw new InvalidDataException("Supersession changed its exact native work, human configuration or original scope.");
            using(var verification=Command(transaction,"SELECT 1 FROM catalog_generation_verification WHERE id=$id",("$id",scope.GenerationId.ToString("D"))))
                if(verification.ExecuteScalar() is not null) throw new InvalidDataException("Superseded work cannot contain a selected completion receipt.");
            if(new CurrentCatalogGenerationSelectionRepository(_database,_budget).ReadForCaptureIn(inputs,transaction,token) is not null)
                throw new InvalidDataException("Actually selected work cannot be relabelled as unselected.");
            new WorkspaceObservationRepository(_database,_budget).AssertHistoricalCurrentInventoryIn(value.Inventory,transaction,token);
            var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
            var intent=new ProtocolSigningJournal(_database,_budget).Read(value.Inventory.RootIntentId,transaction)
                ?? throw new InvalidDataException("Supersession lost its observed Root.");
            var root=LibraryCurrentControl.Read(intent.Envelope!.Value.Span,basis.Pin);
            if(root.Catalog!=value.SelectedAnchor) throw new InvalidDataException("Supersession substituted its actual selected anchor.");
            var journal=new ProtocolSigningJournal(_database,_budget);
            var sources=root.ReadSources(journal.RetainedControllerReference(basis.Pin,root.Sources,"library-source-configuration",transaction).Span,basis.Pin);
            if(sources.Revision>value.HumanConfigurationRevision)
                throw new InvalidDataException("Supersession predates its actually observed Root source policy.");
            var publishers=root.ReadPublishers(journal.RetainedControllerReference(basis.Pin,root.Publishers,"library-publisher-authorization",transaction).Span,basis.Pin);
            if(publishers.Publisher is not {} actor || actor.ActorId!=scope.ActorId || actor.Epoch!=scope.ActorEpoch)
                throw new InvalidDataException("Supersession substituted its original publisher.");
            var verified=new SignedProtocolDocuments([CatalogPublicationReader.Capability,DriveCatalogFacts.Capability]).VerifyAndRead(inputs.Intent.Envelope!.Value.Span,actor);
            _=new CatalogPublicationReader(basis.Pin.Workspace).ReadCommit(verified);
            var recovery=new CurrentControlTransitionJournal(_database,_budget).ReadOwnedCatalogRootRecoveryIn(inputs,transaction,token);
            if(recovery.WithdrawnPlans!=value.WithdrawnRootPlans || recovery.Sha256!=value.RootRecoverySha256)
                throw new InvalidDataException("Supersession lost its exact original withdrawn Root plans.");
            token.ThrowIfCancellationRequested();return value;
        }
        catch(Exception error) when(error is JsonException or ArgumentException or ProtocolFormatException or OverflowException)
        {throw new InvalidDataException("Supersession cannot be decoded safely.",error);}
    }

    private string OriginalSupersededWorkSha256(CurrentOwnedCatalogSelection inputs,SqliteTransaction transaction,CancellationToken token)
    {
        var scope=inputs.Publication.Capture.Scope;var outbox=new WorkspacePublicationRepository(_database,_budget);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData("mpa-superseded-original-work-v1\0"u8);
        var count=0;
        foreach(var id in outbox.Identities(WorkspaceObservationRepository.MaximumDocuments,transaction))
        {
            token.ThrowIfCancellationRequested();var row=outbox.Read(id,transaction) ?? throw new InvalidDataException("Original superseded outbox work disappeared.");
            if(row.Scope.GenerationId!=scope.GenerationId) continue;
            if(row.Scope!=scope || row.FileId is null || row.VerifiedVersion is null) throw new InvalidDataException("Superseded work lost a materialized original object.");
            Append(new WorkspacePublicationExpectation(row.Id,row.Scope,row.Role,row.MimeType,row.Payload.Length,row.Sha256,row.FileId,row.VerifiedVersion));count=checked(count+1);
        }
        foreach(var dependency in new CatalogDependencyReferenceRepository(_database,_budget).ForScope(scope,transaction).OrderBy(v=>v.Id.ToString("D"),StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();var row=outbox.Read(dependency.OriginalPublicationId,transaction) ?? throw new InvalidDataException("Original superseded dependency disappeared.");
            Append(new {dependency,Original=new WorkspacePublicationExpectation(row.Id,row.Scope,row.Role,row.MimeType,row.Payload.Length,row.Sha256,row.FileId,row.VerifiedVersion)});
        }
        if(count<1) throw new InvalidDataException("Supersession lost its materialized outbox.");
        Append(new {scope,Count=count,inputs.PlanSha256});return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append<T>(T value)
        {
            var bytes=new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(value,CurrentCompletionJson));
            Span<byte> size=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(size,bytes.Length);hash.AppendData(size);hash.AppendData(bytes);
        }
    }
    private static string EncodeSupersession(CurrentCatalogGenerationSupersession value)=>Encoding.UTF8.GetString(new CanonicalJson(49152)
        .Encode(JsonSerializer.SerializeToUtf8Bytes(value,CurrentCompletionJson)));
    private static string SupersessionHash(CurrentOwnedCatalogSelection inputs,string text)=>Hash(new CanonicalJson(65536)
        .Encode(JsonSerializer.SerializeToUtf8Bytes(new {domain="mpa-current-generation-supersession-integrity-v1",inputs.PlanSha256,text},CurrentCompletionJson)));
}
