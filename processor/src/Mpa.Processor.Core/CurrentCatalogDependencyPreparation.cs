using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Prepares LOCAL immutable retry work under actual current admission.
    /// Its returned row is progress, never a refreshed inventory or upload grant.</summary>
    public WorkspacePublicationRecord PrepareCurrentDependency(Guid generationId,CatalogDependencyPayload payload,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(payload);token.ThrowIfCancellationRequested();
        var owned=payload.CopyBytes();using var transaction=_database.Connection.BeginTransaction();
        var inputs=RequireCurrentBasisInputsIn(generationId,inventory,signing,fenced,publications,transaction,token);
        var capture=inputs.Capture;var scope=capture.Scope;var original=ReadPublication(capture,transaction)
            ?? throw new CatalogGenerationCaptureConflictException();
        if(capture.Phase!="publication-planned" || original.Plan is not null) throw new CatalogGenerationCaptureConflictException();
        AssertPreparedDependencyBasisIn(original,inputs.Root,inputs.Sources,transaction,token);
        var outbox=new WorkspacePublicationRepository(_database,_budget);
        using var write=outbox.PrepareOwnedIn(payload.IdFor(scope),scope,payload.Role,payload.MimeType,owned,transaction);
        // The prepared row spends the original outbox inventory. Check its exact
        // permitted local delta without forging a new remote observation/grant.
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryAfterPreparedObjectIn(inventory,signing,fenced,
            publications,write.Value,transaction,token);
        new LibraryAuthorityRepository(_database,_budget).AssertLocalPublicationScope(scope,transaction);
        var after=RequiredCapture(generationId,transaction);
        if(after!=capture || ReadPublication(after,transaction)!=original) throw new CatalogGenerationCaptureConflictException();
        var root=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token).Head;
        var sources=new LibraryAuthorityRepository(_database,_budget).CurrentSourceConfiguration(transaction)
            ?? throw new CatalogPublicationNotAuthorizedException();
        if(sources.Sequence!=inputs.Sources.Configuration.Sequence || sources.EnvelopeSha256!=inputs.Sources.Configuration.EnvelopeSha256)
            throw new StaleLibraryConfigurationException();
        AssertPreparedDependencyBasisIn(original,root,inputs.Sources,transaction,token);
        token.ThrowIfCancellationRequested();write.Recheck();transaction.Commit();return write.Value;
    }

    private void AssertPreparedDependencyBasisIn(CapturedCatalogPublication publication,LibraryCurrentControl root,CurrentBasisSource sources,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,CancellationToken token)
    {
        var selected=ReadPublishedHead(transaction);
        if(root.Catalog is null ? selected is not null : selected is null || selected.Commit!=root.Catalog)
            throw new CatalogGenerationCaptureConflictException();
        var lineage=new OwnedCatalogLineageReader(this,publication.Capture.Scope,transaction,token);
        if(lineage.Publisher.KeyId!=sources.PublisherKeyId) throw new SigningHistoryRecoveryRequiredException();
        var tip=lineage.ReadNativeTip(root.Catalog);
        if(publication.Basis.Parent!=tip.Parent || publication.Basis.ParentSequence!=tip.Sequence ||
            publication.Basis.SourcePolicySha256!=sources.Configuration.EnvelopeSha256)
            throw new CatalogGenerationCaptureConflictException();
    }
}
