using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Dispose only unsigned partial work after a current source change.
    /// The historical result grants no upload, selected head or file cleanup.</summary>
    public Task<CurrentUnsignedCatalogDisposition> DisposeUnsignedGenerationAsync(LocalCurrentWorkspaceInventory inventory,Guid generationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertPredecessorInventoryOwner(inventory);
            var fence=inventory.Fence;AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            var value=new CatalogGenerationCaptureRepository(database,_budget).DisposeUnsignedCurrentGeneration(generationId,
                inventory.Checkpoint,inventory.Signing,fence.Attempt,inventory.Publications,token);
            Check(caller,token);return value;
        },token),caller);
    }
}
