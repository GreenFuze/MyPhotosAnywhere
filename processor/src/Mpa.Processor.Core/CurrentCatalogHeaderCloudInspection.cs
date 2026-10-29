using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Exact header description, never a durable or caller-constructed grant.
/// Any later physical action must repeat the same current transaction checks.</summary>
public sealed class CurrentCatalogHeaderCloudInspection
{
    public CurrentCatalogHeaderInspection Identity {get;}
    public WorkspacePublicationRecord Header {get;}
    public CurrentWorkspaceInventoryCheckpoint Inventory {get;}
    internal CurrentCatalogHeaderCloudInspection(CurrentCatalogHeaderInspection original,
        WorkspacePublicationRecord header,CurrentWorkspaceInventoryCheckpoint inventory)
    {Identity=original;Header=header;Inventory=inventory;}
}

public sealed partial class CatalogGenerationCaptureRepository
{
    public CurrentCatalogHeaderCloudInspection InspectCurrentCatalogHeaderCloud(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();
        var result=ReadCurrentHeaderCloudInputsIn(generationId,inventory,signing,fenced,publications,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    // The eventual per-header receipt factory must use this in its OWN borrowed
    // transaction. A public hash/value never bypasses actual complete inventory.
    internal CurrentCatalogHeaderCloudInspection ReadCurrentHeaderCloudInputsIn(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,
        SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Header admission requires its owning transaction.");
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(
            inventory,signing,fenced,publications,transaction,token);
        var original=ReadCurrentHeaderProviderInputsIn(generationId,signing,fenced,transaction,token);
        var header=original.Header ?? throw new SigningHistoryRecoveryRequiredException();
        if(original.Capture.Phase!="commit-sealed" || original.Intent.Envelope is null ||
            header.Id!=original.Capture.CommitIntentId || header.Scope!=original.Capture.Scope ||
            header.Role!="catalog-commit" || header.MimeType!="application/json" ||
            header.Sha256!=original.Intent.EnvelopeSha256 || !header.Payload.Span.SequenceEqual(original.Intent.Envelope.Value.Span))
            throw new WorkspacePublicationConflictException();
        var basis=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token);
        if(inventory.Context.LibraryId!=header.Scope.LibraryId || inventory.Context.AccountId!=header.Scope.AccountId ||
            inventory.Context.FolderId!=header.Scope.FolderId || inventory.Context.AccountRevision<header.Scope.AccountRevision)
            throw new WorkspacePublicationConflictException();
        // A header cannot impersonate the Root or either selected policy document.
        // Provider allocation must additionally reject every observed/captured role
        // alias before any create. No caller-supplied allocation is accepted here.
        if(header.FileId is {} file && (file==basis.Pin.CurrentFileId ||
            file==basis.Head.Publishers.Reference.FileId || file==basis.Head.Sources.Reference.FileId))
            throw new WorkspacePublicationConflictException();
        token.ThrowIfCancellationRequested();return new(original.Inspection,header,inventory);
    }
}
