using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Birth of NEW work only, under the actual complete inventory and
    /// final Root. Existing work requires its separate exact recovery owner. This
    /// never selects policies, signs, uploads, completes or retires a generation.</summary>
    public CatalogGenerationCapture BeginNewCurrentGeneration(CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        token.ThrowIfCancellationRequested();using var transaction=_database.Connection.BeginTransaction();
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var basis=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token);
        var authority=new LibraryAuthorityRepository(_database,_budget);var local=authority.ReadLocalPublicationAuthority(transaction);
        var publisher=authority.CurrentPublisherPolicy(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        var sources=authority.CurrentSourceConfiguration(transaction) ?? throw new CatalogPublicationNotAuthorizedException();
        if(publisher.Sequence!=basis.Head.Publishers.Sequence || publisher.EnvelopeSha256!=basis.Head.Publishers.Reference.Sha256 ||
            sources.Sequence!=basis.Head.Sources.Sequence || sources.EnvelopeSha256!=basis.Head.Sources.Reference.Sha256 ||
            local.LibraryId!=inventory.Context.LibraryId || local.AccountId!=inventory.Context.AccountId ||
            local.FolderId!=inventory.Context.FolderId || local.AccountRevision!=inventory.Context.AccountRevision)
            throw new StaleLibraryConfigurationException();
        var journal=new ProtocolSigningJournal(_database,_budget);var history=new LocalPublisherHistory();
        foreach(var id in basis.ObservedRoots)
        {
            token.ThrowIfCancellationRequested();var intent=journal.Read(id,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
            var root=LibraryCurrentControl.Read(intent.Envelope!.Value.Span,basis.Pin);
            history.Observe(root.ReadPublishers(journal.RetainedControllerReference(basis.Pin,root.Publishers,"library-publisher-authorization",transaction).Span,basis.Pin));
        }
        history.Observe(publisher);
        ValidateRetiredHistory(transaction);
        using(var query=Command(transaction,"SELECT 1 FROM catalog_generation_capture WHERE phase NOT IN('abandoned','retired') LIMIT 1"))
            if(query.ExecuteScalar() is not null) throw new CatalogGenerationInProgressException();
        var roots=signing.Roots.Select(value=>value.Intent.Id).ToHashSet();
        if(signing.Signing.Intents.Any(value=>!roots.Contains(value.Id) && value.EnvelopeSha256 is null))
            throw new SigningHistoryRecoveryRequiredException();
        var terminal=new Dictionary<Guid,bool>();
        foreach(var work in publications.Objects)
        {
            token.ThrowIfCancellationRequested();
            if(work.VerifiedVersion is null && !IsCurrentPartialPublicationDisposedIn(work.Id,transaction,terminal))
                throw new CatalogGenerationInProgressException();
        }
        // An immutable JSON body without an original native/outbox witness can
        // contain missing signed history. A completed transport inventory alone
        // cannot prove a safe native high-water mark. Leave it for explicit recovery.
        var witnessed=signing.Signing.Intents.Where(value=>value.EnvelopeSha256 is not null)
            .Select(value=>(Sha256:value.EnvelopeSha256!,Bytes:(long)value.EnvelopeBytes!.Value)).ToHashSet();
        foreach(var work in publications.Objects.Where(value=>value.MimeType=="application/json")) witnessed.Add((work.Sha256,work.Bytes));
        var observed=new WorkspaceObservationRepository(_database,_budget).CaptureCurrentHistoryIn(signing,fenced,transaction,token);
        foreach(var item in observed.ImmutableObservations)
        {
            token.ThrowIfCancellationRequested();
            if(item.MimeType is null && !witnessed.Contains((item.Sha256,item.Bytes))) throw new SigningHistoryRecoveryRequiredException();
        }
        var selected=ReadPublishedHead(transaction);
        if(basis.Head.Catalog is null ? selected is not null : selected is null || selected.Commit!=basis.Head.Catalog)
            throw new CatalogGenerationCaptureConflictException();
        var scope=new WorkspacePublicationScope(local.LibraryId,local.ActorId,local.ActorEpoch,Guid.NewGuid(),local.AccountId,
            local.AccountRevision,local.FolderId,local.ConfigurationRevision,local.AuthoritySha256);
        var lineage=new OwnedCatalogLineageReader(this,scope,transaction,token);
        if(lineage.Publisher.KeyId!=local.PublisherKeyId) throw new SigningHistoryRecoveryRequiredException();
        lineage.RequireNativeTip(basis.Head.Catalog);
        using var mutation=BeginIn(scope,Guid.NewGuid(),Guid.NewGuid(),transaction);
        // Revalidate after the sole mutation as well: a failing write, accidental
        // trigger or same-revision corruption must roll back the complete birth.
        if(Read(scope.GenerationId,transaction)!=mutation.Value) throw new InvalidDataException("New capture changed during admission.");
        authority.AssertLocalPublicationScope(scope,transaction);
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        token.ThrowIfCancellationRequested();mutation.Recheck();transaction.Commit();return mutation.Value;
    }
}
