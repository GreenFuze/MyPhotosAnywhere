using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class WorkspacePublicationRepository
{
    // Bounded local descriptions under the actual caller's existing transaction.
    // This read-only bridge is not a checkpoint, hash override or cloud grant.
    internal WorkspacePublicationInventory CaptureCurrentHeaderInventoryIn(WorkspaceObservationContext context,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection))
            throw new InvalidOperationException("Current header inventory requires its owning transaction.");
        return Inventory(context,WorkspaceObservationRepository.MaximumDocuments,transaction);
    }
}
