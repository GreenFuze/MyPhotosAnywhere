using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Private copy/resume admission only. The original scope and identities
    /// must still exactly match current policies/account. This is not an upload,
    /// predecessor, credential-renewal or signed-work recovery capability.</summary>
    public CatalogGenerationCapture ValidateCurrentSnapshot(Guid generationId,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();
        var result=RequireCurrentSnapshotIn(generationId,inventory,signing,fenced,publications,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    /// <summary>Seal only an actually closed/verified private file. The exact same
    /// complete Root/inventory/native/human proof is checked before and after the
    /// write in its OWN transaction; separate validation cannot close this race.</summary>
    public CatalogGenerationCapture SealCurrentSnapshot(Guid generationId,CatalogSnapshotDescriptor descriptor,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);using var transaction=_database.Connection.BeginTransaction();
        _=RequireCurrentSnapshotIn(generationId,inventory,signing,fenced,publications,transaction,token);
        using var mutation=SealSnapshotIn(generationId,descriptor,transaction);
        if(RequireCurrentSnapshotIn(generationId,inventory,signing,fenced,publications,transaction,token)!=mutation.Value)
            throw new InvalidDataException("Current snapshot changed during atomic sealing.");
        token.ThrowIfCancellationRequested();mutation.Recheck();transaction.Commit();return mutation.Value;
    }

    private CatalogGenerationCapture RequireCurrentSnapshotIn(Guid generationId,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,
        SqliteTransaction transaction,CancellationToken token)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);token.ThrowIfCancellationRequested();
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var basis=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token);
        var capture=RequiredCapture(generationId,transaction);var scope=capture.Scope;
        if(capture.Phase is not("capturing" or "frozen")) throw new CatalogGenerationCaptureConflictException();
        var authority=new LibraryAuthorityRepository(_database,_budget);authority.AssertLocalPublicationScope(scope,transaction);
        var publisher=authority.CurrentPublisherPolicy(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        var sources=authority.CurrentSourceConfiguration(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        if(publisher.Sequence!=basis.Head.Publishers.Sequence || publisher.EnvelopeSha256!=basis.Head.Publishers.Reference.Sha256 ||
            sources.Sequence!=basis.Head.Sources.Sequence || sources.EnvelopeSha256!=basis.Head.Sources.Reference.Sha256 ||
            scope.LibraryId!=inventory.Context.LibraryId || scope.AccountId!=inventory.Context.AccountId ||
            scope.FolderId!=inventory.Context.FolderId || scope.AccountRevision!=inventory.Context.AccountRevision)
            throw new StaleLibraryConfigurationException();
        if(!signing.Signing.Actors.Any(value=>value.ActorId==scope.ActorId && value.Epoch==scope.ActorEpoch &&
            value.Role=="publisher" && value.KeyId==publisher.PublisherKeyId)) throw new SigningHistoryRecoveryRequiredException();
        using(var query=Command(transaction,"""
            SELECT (SELECT count(*) FROM signing_intent WHERE id=$intent)+
              (SELECT count(*) FROM workspace_publication WHERE scope=$scope)
            """,("$intent",capture.CommitIntentId.ToString("D")),("$scope",WorkspacePublicationRepository.EncodeScope(scope))))
            if((long)query.ExecuteScalar()! !=0) throw new CatalogGenerationCaptureConflictException();
        token.ThrowIfCancellationRequested();return capture;
    }
}
