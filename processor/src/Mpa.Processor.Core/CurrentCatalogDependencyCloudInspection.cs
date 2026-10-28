using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Exact original dependency description under one actual inventory.
/// It is not a persistent, scope-wide or caller-constructed provider grant.</summary>
public sealed class CurrentCatalogDependencyCloudInspection
{
    public CapturedCatalogPublication Publication {get;}
    public WorkspacePublicationRecord Dependency {get;}
    public CurrentWorkspaceInventoryCheckpoint Inventory {get;}
    internal CurrentCatalogDependencyCloudInspection(CapturedCatalogPublication publication,WorkspacePublicationRecord dependency,
        CurrentWorkspaceInventoryCheckpoint inventory)
    {Publication=publication;Dependency=dependency;Inventory=inventory;}
}

public sealed partial class CatalogGenerationCaptureRepository
{
    public CurrentCatalogDependencyCloudInspection InspectCurrentCatalogDependencyCloud(Guid publicationId,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(publicationId);using var transaction=_database.Connection.BeginTransaction();
        var result=ReadCurrentDependencyCloudInputsIn(publicationId,inventory,signing,fenced,publications,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    private CurrentCatalogDependencyCloudInspection ReadCurrentDependencyCloudInputsIn(Guid publicationId,CurrentWorkspaceInventoryCheckpoint inventory,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,
        SqliteTransaction transaction,CancellationToken token)
    {
        Id(publicationId);var row=new WorkspacePublicationRepository(_database,_budget).Read(publicationId,transaction) ?? throw new WorkspacePublicationConflictException();
        if(row.Role is not("facts-chunk" or "facts-index" or "coverage-page" or "coverage-root")) throw new WorkspacePublicationConflictException();
        var payload=CatalogDependencyPayload.Capture(row.Role,row.MimeType,row.Payload.Span);
        if(payload.IdFor(row.Scope)!=row.Id) throw new WorkspacePublicationConflictException();
        var inputs=RequireCurrentBasisInputsIn(row.Scope.GenerationId,inventory,signing,fenced,publications,transaction,token);
        var publication=ReadPublication(inputs.Capture,transaction) ?? throw new CatalogGenerationCaptureConflictException();
        if(inputs.Capture.Scope!=row.Scope || inputs.Capture.Phase!="publication-planned" || publication.Plan is not null)
            throw new CatalogGenerationCaptureConflictException();
        AssertPreparedDependencyBasisIn(publication,inputs.Root,inputs.Sources,transaction,token);
        var pin=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(fenced,transaction,token).Pin;
        if(row.FileId is {} file && (file==pin.CurrentFileId || file==inputs.Root.Publishers.Reference.FileId ||
            file==inputs.Root.Sources.Reference.FileId || file==inputs.Root.Catalog?.FileId)) throw new WorkspacePublicationConflictException();
        token.ThrowIfCancellationRequested();return new(publication,row,inventory);
    }
}
