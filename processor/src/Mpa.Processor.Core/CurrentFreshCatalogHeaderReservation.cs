namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Reserve only the original current capture's exact plan-derived
    /// native header. This local checkpoint spends the prior native inventory;
    /// it returns no new workspace scan, signing permission or cloud admission.</summary>
    public ProtocolSigningIntent ReserveCurrentFreshCatalogHeader(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        token.ThrowIfCancellationRequested();using var transaction=_database.Connection.BeginTransaction();
        var observations=new WorkspaceObservationRepository(_database,_budget);
        observations.AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var capture=RequiredCapture(generationId,transaction);
        var journal=new ProtocolSigningJournal(_database,_budget);
        var existing=journal.Read(capture.CommitIntentId,transaction);
        if(existing is not null)
        {
            // Exact retained progress only. Never reserve a replacement counter,
            // body or signature to turn a previously pending header into new work.
            var retained=ReadCurrentFreshHeaderInputsIn(generationId,signing,fenced,transaction,token);
            token.ThrowIfCancellationRequested();transaction.Commit();return retained.Intent;
        }
        var publication=ReadPublication(capture,transaction)??throw new CatalogGenerationCaptureConflictException();
        var plan=publication.Plan??throw new CatalogGenerationCaptureConflictException();
        if(capture.Phase!="dependencies-ready") throw new CatalogGenerationCaptureConflictException();
        var before=InspectCurrentDependencyGraphIn(generationId,plan,inventory,signing,fenced,publications,transaction,token);
        if(before.Publication!=publication) throw new CatalogGenerationCaptureConflictException();
        var human=new LibrarySourceRepository(_database).Snapshot(transaction);
        var humanSha=HashPredecessorHumanSources(human);
        if(human.Revision!=capture.Scope.ConfigurationRevision) throw new StaleLibraryConfigurationException();
        using var reserved=journal.ReserveIn(capture.CommitIntentId,capture.Scope.ActorId,capture.Scope.ActorEpoch,
            "catalog-commit",sequence=>sequence==checked(plan.Basis.ParentSequence+1)?
                plan.EncodeBody(capture.Scope,sequence):throw new ProtocolSigningConflictException(),transaction);
        var afterSigning=new CurrentControlTransitionJournal(_database,_budget)
            .AssertOnlyCurrentHeaderReservationChangedIn(signing,fenced,reserved.Value,transaction,token);
        var after=ReadCurrentFreshHeaderInputsIn(generationId,afterSigning,fenced,transaction,token);
        if(after.Capture!=capture || after.Publication!=publication || after.Intent.Id!=reserved.Value.Id ||
            after.Intent.BodySha256!=reserved.Value.BodySha256 || after.Intent.Sequence!=reserved.Value.Sequence ||
            !after.Intent.Body.Span.SequenceEqual(reserved.Value.Body.Span) || after.Header is not null ||
            after.Inspection.HumanConfigurationRevision!=human.Revision || after.Inspection.HumanConfigurationSha256!=humanSha)
            throw new CatalogGenerationCaptureConflictException();
        observations.AssertCurrentHeaderObservationAfterNativeMutationIn(inventory,signing,afterSigning,
            fenced,publications,transaction,null,token);
        token.ThrowIfCancellationRequested();reserved.Recheck();transaction.Commit();return after.Intent;
    }
}
