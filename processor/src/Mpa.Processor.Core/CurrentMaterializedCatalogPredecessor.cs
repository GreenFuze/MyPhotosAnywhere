using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Read-only inspection of exact owned, materialized work under an actual
/// current inventory/fence. Not a published head, durable disposition, new parent
/// admission, cleanup permission or permission to upload obsolete source facts.</summary>
public sealed class CurrentMaterializedCatalogPredecessor
{
    public WorkspacePublicationScope OriginalScope {get;}
    public Guid SnapshotId {get;}
    public Guid CommitIntentId {get;}
    public CatalogObjectReference OriginalCommit {get;}
    public CatalogObjectReference? SelectedAnchor {get;}
    public long Sequence {get;}
    public long HumanConfigurationRevision {get;}
    public string HumanConfigurationSha256 {get;}
    public int WithdrawnRootPlans {get;}
    public string ClaimSha256 {get;}
    internal CurrentMaterializedCatalogPredecessor(CatalogGenerationCapture capture,CatalogObjectReference original,
        CatalogObjectReference? anchor,long sequence,long humanRevision,string humanSha256,int roots,string claim)
    {OriginalScope=capture.Scope;SnapshotId=capture.SnapshotId;CommitIntentId=capture.CommitIntentId;OriginalCommit=original;
        SelectedAnchor=anchor;Sequence=sequence;HumanConfigurationRevision=humanRevision;HumanConfigurationSha256=humanSha256;WithdrawnRootPlans=roots;ClaimSha256=claim;}
}

public sealed partial class CatalogGenerationCaptureRepository
{
    private static readonly JsonSerializerOptions PredecessorInspectionJson=new() {NumberHandling=JsonNumberHandling.WriteAsString};

    public CurrentMaterializedCatalogPredecessor InspectMaterializedPredecessor(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();
        var result=InspectMaterializedPredecessorIn(generationId,inventory,signing,fenced,publications,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    // Future durable disposition must reuse this same transaction-borrowed gate,
    // not turn this public read result or its hash into authority after more IO.
    internal CurrentMaterializedCatalogPredecessor InspectMaterializedPredecessorIn(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Predecessor inspection requires its owning transaction.");
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var capture=RequiredCapture(generationId,transaction);
        if(capture.Phase!="commit-sealed") throw new CatalogGenerationCaptureConflictException();
        var inputs=SelectionInputsIn(capture,transaction);var work=inputs.Work;var scope=work.Scope;
        if(new CurrentCatalogGenerationSelectionRepository(_database,_budget).ReadForCaptureIn(inputs,transaction,token) is not null)
            throw new CatalogGenerationCaptureConflictException();
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(fenced,transaction,token);
        var original=new CatalogObjectReference(scope.AccountId,work.FileId!,scope.FolderId,work.Payload.Length,work.Sha256);
        var human=new LibrarySourceRepository(_database).Snapshot(transaction);
        if(human.Revision<=scope.ConfigurationRevision) throw new StaleLibraryConfigurationException();
        if(inventory.Context.LibraryId!=scope.LibraryId || inventory.Context.AccountId!=scope.AccountId || inventory.Context.FolderId!=scope.FolderId ||
            inventory.Context.AccountRevision<scope.AccountRevision || basis.Head.Catalog==original ||
            inputs.Intent.Sequence!=checked(inputs.Publication.Basis.ParentSequence+1))
            throw new CatalogGenerationCaptureConflictException();
        var journal=new ProtocolSigningJournal(_database,_budget);
        var publisher=basis.Head.ReadPublishers(journal.RetainedControllerReference(basis.Pin,basis.Head.Publishers,"library-publisher-authorization",transaction).Span,basis.Pin);
        var sources=basis.Head.ReadSources(journal.RetainedControllerReference(basis.Pin,basis.Head.Sources,"library-source-configuration",transaction).Span,basis.Pin);
        if(publisher.Publisher is not {} actor || actor.ActorId!=scope.ActorId || actor.Epoch!=scope.ActorEpoch || sources.Revision>human.Revision)
            throw new CatalogGenerationCaptureConflictException();
        var verified=new SignedProtocolDocuments([CatalogPublicationReader.Capability,DriveCatalogFacts.Capability])
            .VerifyAndRead(inputs.Intent.Envelope!.Value.Span,actor);
        _=new CatalogPublicationReader(basis.Pin.Workspace).ReadCommit(verified);
        var head=ReadPublishedHead(transaction);
        if(basis.Head.Catalog is {} anchor)
        {
            if(head is null || head.Commit!=anchor || head.GenerationId==generationId)
                throw new CatalogGenerationCaptureConflictException();
        }
        else if(head is not null) throw new CatalogGenerationCaptureConflictException();
        // An actual selected anchor and an owned signing parent are distinct. All
        // intervening native commits must have their original verified disposition
        // and physical retirement; no higher orphan becomes a new head.
        new OwnedCatalogLineageReader(this,scope,transaction,token).RequirePath(inputs.Publication.Basis.Parent,
            inputs.Publication.Basis.ParentSequence,basis.Head.Catalog);
        var roots=0;
        foreach(var root in signing.Roots)
        {
            token.ThrowIfCancellationRequested();if(root.Transition?.GenerationId!=generationId) continue;
            if(root.Transition.Target.CatalogPublicationId!=capture.CommitIntentId || root.Transition.Catalog!=original ||
                root.Disposition!=CurrentRootSigningDisposition.Withdrawn || root.TerminalSha256 is null)
                throw new CurrentControlTransitionInProgressException();
            roots=checked(roots+1);
        }
        var observations=new WorkspaceObservationRepository(_database,_budget).Known(inventory.Context,transaction).ToDictionary(v=>v.FileId,StringComparer.Ordinal);
        var count=0;
        foreach(var item in publications.Objects.Where(v=>v.Scope.GenerationId==generationId))
        {
            token.ThrowIfCancellationRequested();count=checked(count+1);
            if(item.Scope!=scope || item.FileId is null || item.VerifiedVersion is null || !observations.TryGetValue(item.FileId,out var actual) ||
                actual.Version<item.VerifiedVersion || actual.Bytes!=item.Bytes || actual.Sha256!=item.Sha256 || (actual.MimeType ?? "application/json")!=item.MimeType)
                throw new WorkspaceObservationConflictException();
        }
        if(count<1 || !observations.TryGetValue(original.FileId,out var committed) || committed.Sha256!=original.Sha256 || committed.Bytes!=original.Bytes)
            throw new WorkspaceObservationConflictException();
        foreach(var dependency in new CatalogDependencyReferenceRepository(_database,_budget).ForScope(scope,transaction))
        {
            token.ThrowIfCancellationRequested();var retained=new WorkspacePublicationRepository(_database,_budget).Read(dependency.OriginalPublicationId,transaction)
                ?? throw new WorkspacePublicationConflictException();
            if(retained.VerifiedVersion is null || !observations.TryGetValue(dependency.FileId,out var actual) || actual.Version<retained.VerifiedVersion ||
                actual.Bytes!=dependency.Bytes || actual.Sha256!=dependency.Sha256 || (actual.MimeType ?? "application/json")!=dependency.MimeType)
                throw new WorkspaceObservationConflictException();
        }
        var humanHash=HashPredecessorHumanSources(human);
        var claim=Convert.ToHexStringLower(SHA256.HashData(new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain="mpa-materialized-current-predecessor-inspection-v1",capture.Scope,capture.SnapshotId,capture.CommitIntentId,
            original,anchor=basis.Head.Catalog,inputs.Intent.Sequence,inputs.PlanSha256,HumanRevision=human.Revision,HumanSourcesSha256=humanHash,WithdrawnRootPlans=roots,inventory
        },PredecessorInspectionJson))));
        token.ThrowIfCancellationRequested();return new(capture,original,basis.Head.Catalog,inputs.Intent.Sequence,human.Revision,humanHash,roots,claim);
    }

    private static string HashPredecessorHumanSources(LibrarySourceSnapshot human)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData("mpa-current-predecessor-human-sources-v1\0"u8);
        Append(new {human.Revision,Count=human.Sources.Count});
        foreach(var source in human.Sources.OrderBy(v=>v.Id.ToString("D"),StringComparer.Ordinal))
            Append(new {source.Id,source.AccountId,source.FolderId,source.DisplayName,source.IncludeDescendants,source.Enabled,source.Revision});
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append<T>(T value)
        {
            // The existing1024-character display-name limit can serialize as
            //6144 escaped bytes before canonicalization. Frame each source alone;
            // neither the global source/document nor collection budget changes.
            var bytes=new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(value,PredecessorInspectionJson));
            Span<byte> size=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(size,bytes.Length);hash.AppendData(size);hash.AppendData(bytes);
        }
    }
}
