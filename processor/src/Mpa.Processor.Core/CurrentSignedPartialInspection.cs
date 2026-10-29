using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    private sealed record SignedPartialPredecessor(CatalogObjectReference OriginalCommit,
        CatalogObjectReference? SelectedAnchor,long Sequence,long HumanConfigurationRevision,
        string HumanConfigurationSha256,int WithdrawnRootPlans);

    // These are the same current/native/human/Root/lineage checks required by the
    // full inspector. Only the explicitly typed partial path may retain unfinished
    // extra rows; the original materialized inspector is unchanged and still fails.
    private SignedPartialPredecessor InspectSignedPartialPredecessorIn(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,
        SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Predecessor inspection requires its owning transaction.");
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        _=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token);
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
        var header=publications.Objects.SingleOrDefault(v=>v.Id==capture.CommitIntentId)
            ?? throw new WorkspacePublicationConflictException();
        if(header.Scope!=scope || header.FileId!=original.FileId || header.VerifiedVersion is null ||
            !observations.TryGetValue(original.FileId,out var committed) || committed.Version<header.VerifiedVersion ||
            committed.Sha256!=original.Sha256 || committed.Bytes!=original.Bytes ||
            (committed.MimeType ?? "application/json")!="application/json")
            throw new WorkspaceObservationConflictException();
        var own=publications.Objects.Where(v=>v.Scope.GenerationId==generationId).ToArray();
        if(own.Any(v=>v.Scope!=scope) || !own.Any(v=>v.Id!=capture.CommitIntentId && v.VerifiedVersion is null))
            throw new CatalogGenerationCaptureConflictException();
        foreach(var dependency in new CatalogDependencyReferenceRepository(_database,_budget).ForScope(scope,transaction))
        {
            token.ThrowIfCancellationRequested();var retained=new WorkspacePublicationRepository(_database,_budget).Read(dependency.OriginalPublicationId,transaction)
                ?? throw new WorkspacePublicationConflictException();
            if(retained.VerifiedVersion is null || !observations.TryGetValue(dependency.FileId,out var actual) || actual.Version<retained.VerifiedVersion ||
                actual.Bytes!=dependency.Bytes || actual.Sha256!=dependency.Sha256 || (actual.MimeType ?? "application/json")!=dependency.MimeType)
                throw new WorkspaceObservationConflictException();
        }
        var humanHash=HashPredecessorHumanSources(human);
        if(ReadArchivedPredecessorHumanSources(human.Revision,transaction)!=humanHash)
            throw new InvalidDataException("Signed partial disposition lost its original archived human bindings.");
        token.ThrowIfCancellationRequested();
        return new(original,basis.Head.Catalog,inputs.Intent.Sequence,human.Revision,humanHash,roots);
    }
}
