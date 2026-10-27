using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Read-only original reserved/sealed header description. Its hash is not
/// authority. A future local repair must recheck original inputs in its own transaction;
/// a future upload still needs a NEW actual complete inventory and final Root fence.</summary>
public sealed class CurrentCatalogHeaderInspection
{
    public WorkspacePublicationScope OriginalScope {get;}
    public Guid SnapshotId {get;}
    public Guid CommitIntentId {get;}
    public long Sequence {get;}
    public string BodySha256 {get;}
    public string? EnvelopeSha256 {get;}
    public string? OriginalFileId {get;}
    public long? VerifiedVersion {get;}
    public CatalogObjectReference? SelectedAnchor {get;}
    public long HumanConfigurationRevision {get;}
    public string HumanConfigurationSha256 {get;}
    public string ClaimSha256 {get;}
    internal CurrentCatalogHeaderInspection(CatalogGenerationCapture capture,ProtocolSigningIntent intent,WorkspacePublicationRecord? header,
        CatalogObjectReference? anchor,long humanRevision,string humanSha,string claim)
    {
        OriginalScope=capture.Scope;SnapshotId=capture.SnapshotId;CommitIntentId=capture.CommitIntentId;Sequence=intent.Sequence;
        BodySha256=intent.BodySha256;EnvelopeSha256=intent.EnvelopeSha256;OriginalFileId=header?.FileId;VerifiedVersion=header?.VerifiedVersion;
        SelectedAnchor=anchor;HumanConfigurationRevision=humanRevision;HumanConfigurationSha256=humanSha;ClaimSha256=claim;
    }
}

public sealed partial class CatalogGenerationCaptureRepository
{
    public CurrentCatalogHeaderInspection InspectCurrentCatalogHeader(Guid generationId,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(fenced);
        using var transaction=_database.Connection.BeginTransaction();
        var inputs=ReadCurrentHeaderInputsIn(generationId,signing,fenced,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return inputs.Inspection;
    }

    // No complete workspace checkpoint is required merely to inspect/repair LOCAL
    // missing pending-header bookkeeping. Such a checkpoint can be unavailable after
    // sealing but before outbox insertion. This helper grants no cloud access.
    private CurrentHeaderRecoveryInputs ReadCurrentHeaderInputsIn(Guid generationId,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Header inspection requires its original transaction.");
        var observations=new LocalCurrentControlObservations(_database,_budget);
        var basis=observations.TransitionBasisIn(fenced,transaction,token);
        if(fenced.Phase!="fenced") throw new CurrentControlObservationConflictException();
        _=new CurrentControlTransitionJournal(_database,_budget).RequireSigningInventoryIn(signing,fenced,transaction,token);
        var capture=RequiredCapture(generationId,transaction);var scope=capture.Scope;
        if(capture.Phase is not ("dependencies-ready" or "commit-sealed")) throw new CatalogGenerationCaptureConflictException();
        var publication=ReadPublication(capture,transaction) ?? throw new CatalogGenerationCaptureConflictException();
        var plan=publication.Plan ?? throw new CatalogGenerationCaptureConflictException();
        var journal=new ProtocolSigningJournal(_database,_budget);
        var intent=journal.Read(capture.CommitIntentId,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
        AssertIntent(capture,plan,intent);
        if(intent.Sequence!=checked(publication.Basis.ParentSequence+1) || intent.LibraryId!=scope.LibraryId ||
            intent.ActorId!=scope.ActorId || intent.Epoch!=scope.ActorEpoch || intent.Kind!="catalog-commit")
            throw new CatalogGenerationCaptureConflictException();
        var human=new LibrarySourceRepository(_database).Snapshot(transaction);
        if(human.Revision<=scope.ConfigurationRevision) throw new StaleLibraryConfigurationException();
        var humanSha=HashPredecessorHumanSources(human);
        if(humanSha!=ReadArchivedPredecessorHumanSources(human.Revision,transaction))
            throw new InvalidDataException("Header recovery source bindings changed without their archived revision.");
        if(scope.LibraryId!=basis.Pin.LibraryId || scope.AccountId!=basis.Pin.Workspace.AccountId || scope.FolderId!=basis.Pin.Workspace.FolderId ||
            scope.AccountRevision>fenced.AccountRevision) throw new CatalogGenerationCaptureConflictException();
        var publisher=basis.Head.ReadPublishers(journal.RetainedControllerReference(basis.Pin,basis.Head.Publishers,"library-publisher-authorization",transaction).Span,basis.Pin);
        var sources=basis.Head.ReadSources(journal.RetainedControllerReference(basis.Pin,basis.Head.Sources,"library-source-configuration",transaction).Span,basis.Pin);
        var lineage=new OwnedCatalogLineageReader(this,scope,transaction,token);
        if(publisher.Publisher is not {} actor || actor.ActorId!=scope.ActorId || actor.Epoch!=scope.ActorEpoch ||
            publisher.PublisherKeyId!=lineage.Publisher.KeyId || lineage.Publisher.Sequence!=intent.Sequence || sources.Revision>human.Revision)
            throw new SigningHistoryRecoveryRequiredException();
        if(intent.Envelope is {} envelope)
        {
            var verified=new SignedProtocolDocuments([CatalogPublicationReader.Capability,DriveCatalogFacts.Capability]).VerifyAndRead(envelope.Span,actor);
            _=new CatalogPublicationReader(basis.Pin.Workspace).ReadCommit(verified);
        }
        var selected=ReadPublishedHead(transaction);
        if(basis.Head.Catalog is null ? selected is not null : selected is null || selected.Commit!=basis.Head.Catalog)
            throw new CatalogGenerationCaptureConflictException();
        lineage.RequirePath(publication.Basis.Parent,publication.Basis.ParentSequence,basis.Head.Catalog);
        // This existing helper checks Root-target absence, not unsigned status. An
        // actual pending/applied/withdrawn Root plan requires its separate recovery.
        new CurrentControlTransitionJournal(_database,_budget).AssertUnsignedCatalogHasNoRootIn(capture,transaction,token);
        using(var receipts=Command(transaction,"SELECT (SELECT count(*) FROM catalog_generation_verification WHERE id=$id)+(SELECT count(*) FROM processor_meta WHERE key GLOB $selection)",
            ("$id",generationId.ToString("D")),("$selection","current_catalog_generation_selection_v1:"+generationId.ToString("D")+"*")))
            if((long)receipts.ExecuteScalar()! !=0) throw new CatalogGenerationCaptureConflictException();
        var header=new WorkspacePublicationRepository(_database,_budget).Read(capture.CommitIntentId,transaction);
        if(header is not null && (intent.Envelope is null || header.Scope!=scope || header.Role!="catalog-commit" || header.MimeType!="application/json" ||
            header.Sha256!=intent.EnvelopeSha256 || !header.Payload.Span.SequenceEqual(intent.Envelope.Value.Span) || header.FileId==basis.Pin.CurrentFileId))
            throw new WorkspacePublicationConflictException();
        var planSha=PlanHash(capture,publication.Basis,PublicationEncode(plan));
        var claim=Convert.ToHexStringLower(SHA256.HashData(new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain="mpa-current-original-catalog-header-inspection-v1",capture.Scope,capture.SnapshotId,capture.CommitIntentId,capture.Phase,
            intent.Sequence,intent.BodySha256,intent.EnvelopeSha256,planSha,OriginalFileId=header?.FileId,VerifiedVersion=header?.VerifiedVersion,
            SelectedAnchor=basis.Head.Catalog,HumanRevision=human.Revision,HumanSourcesSha256=humanSha,SigningSha256=signing.Sha256,
            fenced.Id,fenced.Revision,fenced.AccountRevision,fenced.Version,fenced.ContentSha256
        },CurrentCompletionJson))));
        token.ThrowIfCancellationRequested();
        return new(capture,publication,intent,header,new(capture,intent,header,basis.Head.Catalog,human.Revision,humanSha,claim));
    }
    private sealed record CurrentHeaderRecoveryInputs(CatalogGenerationCapture Capture,CapturedCatalogPublication Publication,
        ProtocolSigningIntent Intent,WorkspacePublicationRecord? Header,CurrentCatalogHeaderInspection Inspection);
}
