using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    // Actual newly current scope only. Keep obsolete original-header recovery in
    // ReadCurrentHeaderInputsIn unchanged. Callers still need complete current
    // workspace admission for initial signing and every physical provider request.
    private CurrentHeaderRecoveryInputs ReadCurrentFreshHeaderInputsIn(Guid generationId,CurrentControlSigningInventory signing,
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
        if(human.Revision!=scope.ConfigurationRevision || scope.AccountRevision!=fenced.AccountRevision)
            throw new StaleLibraryConfigurationException();
        var authority=new LibraryAuthorityRepository(_database,_budget);
        authority.AssertLocalPublicationScope(scope,transaction);
        var acceptedPublisher=authority.CurrentPublisherPolicy(transaction)??throw new CatalogPublicationNotAuthorizedException();
        var acceptedSources=authority.CurrentSourceConfiguration(transaction)??throw new CatalogPublicationNotAuthorizedException();
        if(acceptedPublisher.Sequence!=basis.Head.Publishers.Sequence || acceptedPublisher.EnvelopeSha256!=basis.Head.Publishers.Reference.Sha256 ||
            acceptedSources.Sequence!=basis.Head.Sources.Sequence || acceptedSources.EnvelopeSha256!=basis.Head.Sources.Reference.Sha256)
            throw new StaleLibraryConfigurationException();
        var humanSha=HashPredecessorHumanSources(human);
        var rootIntents=signing.Roots.Select(value=>value.Intent.Id).ToHashSet();
        if(signing.Signing.Intents.Any(value=>!rootIntents.Contains(value.Id) && value.Id!=capture.CommitIntentId && value.EnvelopeSha256 is null))
            throw new SigningHistoryRecoveryRequiredException();
        var context=new WorkspaceObservationContext(scope.LibraryId,scope.AccountId,scope.FolderId,fenced.AccountRevision,fenced.SharedDriveId);
        var terminal=new Dictionary<Guid,bool>();
        foreach(var work in new WorkspacePublicationRepository(_database,_budget).CaptureCurrentHeaderInventoryIn(context,transaction).Objects)
        {
            token.ThrowIfCancellationRequested();
            if(work.Scope==scope)
            {
                if(work.Role is("facts-chunk" or "facts-index" or "coverage-page" or "coverage-root"))
                {if(work.FileId is null || work.VerifiedVersion is null) throw new WorkspacePublicationConflictException();}
                else if(work.Role!="catalog-commit" || work.Id!=capture.CommitIntentId) throw new WorkspacePublicationConflictException();
            }
            else if(work.VerifiedVersion is null && !IsCurrentPartialPublicationDisposedIn(work.Id,transaction,terminal))
                throw new CatalogGenerationInProgressException();
        }
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
        var claim=CurrentHeaderClaim(capture,intent,planSha,header,basis.Head.Catalog,human.Revision,humanSha,signing.Sha256,fenced);
        token.ThrowIfCancellationRequested();
        return new(capture,publication,intent,header,new(capture,intent,header,basis.Head.Catalog,human.Revision,humanSha,claim));
    }
}
