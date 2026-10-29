using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    public CurrentCatalogHeaderInspection InspectCurrentFreshCatalogHeader(Guid generationId,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(
            inventory,signing,fenced,publications,transaction,token);
        var actual=ReadCurrentFreshHeaderInputsIn(generationId,signing,fenced,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return actual.Inspection;
    }

    // Provider admission and receipt always derive the branch again from actual
    // human state. No JSON flag, caller preference or checkpoint selects a bypass.
    // The existing original-scope public inspection/recovery remains unchanged.
    private CurrentHeaderRecoveryInputs ReadCurrentHeaderProviderInputsIn(Guid generationId,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection))
            throw new InvalidOperationException("Header scope selection requires its owning transaction.");
        var capture=RequiredCapture(generationId,transaction);
        var human=new LibrarySourceRepository(_database).Snapshot(transaction);
        return human.Revision==capture.Scope.ConfigurationRevision?
            ReadCurrentFreshHeaderInputsIn(generationId,signing,fenced,transaction,token):
            ReadCurrentHeaderInputsIn(generationId,signing,fenced,transaction,token);
    }
}
