using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Current local graph inputs only; the joined application owner must
    /// read the complete graph and compare the actual retained frozen file before
    /// sealing. Returned planning records are never a provider/signing grant.</summary>
    public CurrentPreparedCatalogBasis InspectCurrentDependencyGraph(Guid generationId,CapturedCatalogCommitPlan plan,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(plan);using var transaction=_database.Connection.BeginTransaction();
        var result=InspectCurrentDependencyGraphIn(generationId,plan,inventory,signing,fenced,publications,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    /// <summary>Atomic local checkpoint after actual graph/frozen-file verification.
    /// Only the original plan and capture phase may advance. No signature, Root
    /// selection, receipt replacement or private snapshot retirement happens here.</summary>
    public CapturedCatalogPublication SealCurrentDependencyGraph(Guid generationId,CapturedCatalogCommitPlan plan,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(plan);using var transaction=_database.Connection.BeginTransaction();
        var before=InspectCurrentDependencyGraphIn(generationId,plan,inventory,signing,fenced,publications,transaction,token);
        using var mutation=SealDependenciesIn(generationId,plan,null,transaction);
        var after=InspectCurrentDependencyGraphIn(generationId,plan,inventory,signing,fenced,publications,transaction,token);
        if(mutation.Value!=after.Publication || after.Publication.Capture!=(before.Publication.Capture with {Phase="dependencies-ready"}) ||
            before.Publication.Basis!=after.Publication.Basis || after.Publication.Plan!=plan || before.Sources.Sequence!=after.Sources.Sequence || before.Sources.EnvelopeSha256!=after.Sources.EnvelopeSha256)
            throw new CatalogGenerationCaptureConflictException();
        token.ThrowIfCancellationRequested();mutation.Recheck();transaction.Commit();return after.Publication;
    }

    private CurrentPreparedCatalogBasis InspectCurrentDependencyGraphIn(Guid generationId,CapturedCatalogCommitPlan plan,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,SqliteTransaction transaction,CancellationToken token)
    {
        var inputs=RequireCurrentBasisInputsIn(generationId,inventory,signing,fenced,publications,transaction,token,plan);
        var publication=ReadPublication(inputs.Capture,transaction)??throw new CatalogGenerationCaptureConflictException();
        if(inputs.Capture.Phase is not("publication-planned" or "dependencies-ready") || publication.Basis!=plan.Basis ||
            publication.Plan is not null && publication.Plan!=plan) throw new CatalogGenerationCaptureConflictException();
        ValidatePlan(inputs.Capture,plan);AssertPreparedDependencyBasisIn(publication,inputs.Root,inputs.Sources,transaction,token);
        AssertDependency(inputs.Capture,plan.RootIndex,"facts-index",transaction);
        AssertDependency(inputs.Capture,plan.SourceCoverage,"coverage-root",transaction);
        var known=new WorkspaceObservationRepository(_database,_budget).Known(inventory.Context,transaction).ToDictionary(value=>value.FileId,StringComparer.Ordinal);
        foreach(var work in publications.Objects.Where(value=>value.Scope==inputs.Capture.Scope))
        {
            token.ThrowIfCancellationRequested();
            if(work.Role is not("facts-chunk" or "facts-index" or "coverage-page" or "coverage-root") || work.FileId is null ||
                work.VerifiedVersion is null || !known.TryGetValue(work.FileId,out var actual) || actual.Version<work.VerifiedVersion ||
                actual.Bytes!=work.Bytes || actual.Sha256!=work.Sha256 || (actual.MimeType??"application/json")!=work.MimeType)
                throw new WorkspacePublicationConflictException();
        }
        return new(publication,inputs.Sources.Configuration);
    }
}
