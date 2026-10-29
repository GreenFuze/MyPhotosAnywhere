using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    // Quiesce the actual provider account, not merely the reader's state lane.
    // A previously entered ID allocation/create must finish its joined callback
    // before this transaction freezes every original pending description.
    public Task<CurrentSignedPartialDisposition> DisposeSignedPartialGenerationAsync(
        LocalCurrentWorkspaceInventory inventory,Guid generationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);AssertPredecessorInventoryOwner(inventory);
        return OperationAsync(token=>_accounts.ReadDriveAsync(inventory.Checkpoint.Context.AccountId,async(_,revision,owned)=>
        {
            Check(caller,owned);AssertPredecessorInventoryOwner(inventory);
            if(revision!=inventory.Checkpoint.Context.AccountRevision) throw new StaleDriveConnectionException();
            return await _state.ExecuteAsync(database=>
            {
                Check(caller,owned);var fence=inventory.Fence;
                AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
                var result=new CatalogGenerationCaptureRepository(database,_budget).DisposeSignedPartialCurrentGeneration(generationId,
                    inventory.Checkpoint,inventory.Signing,fence.Attempt,inventory.Publications,owned);
                Check(caller,owned);return result;
            },owned).ConfigureAwait(false);
        },token),caller);
    }
}
