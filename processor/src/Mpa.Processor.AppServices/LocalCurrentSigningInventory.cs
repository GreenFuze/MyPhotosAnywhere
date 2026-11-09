using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Describe ALL native signing work and exact Root recovery witnesses
    /// under this reader's actual capture. No additional network, signing, policy
    /// admission or publication occurs. The future immutable inventory must still
    /// reconcile its own pending objects without treating this as completion.</summary>
    public Task<CurrentControlSigningInventory> CaptureSigningInventoryAsync(LocalCurrentControlCapture capture,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);
            if(!ReferenceEquals(capture.Owner,_identity)) throw new CurrentControlObservationConflictException();
            AssertScope(database,new(capture.Pin,capture.Slot,capture.Attempt));
            return new CurrentControlTransitionJournal(database,_budget).CaptureSigningInventory(capture.Attempt,token);
        },token),caller);
    }

    /// <summary>Recheck the complete same-nonce description after an actual final
    /// Root fence. It grants no scan acknowledgement, generation completion, snapshot
    /// cleanup or activation. All state/native borrowers join this reader's lifetime.</summary>
    public Task AssertSigningInventoryUnchangedAsync(CurrentControlSigningInventory inventory,LocalCurrentControlFence fence,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);ArgumentNullException.ThrowIfNull(fence);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);
            if(!ReferenceEquals(fence.Owner,_identity) || !ReferenceEquals(fence.Capture.Owner,_identity))
                throw new CurrentControlObservationConflictException();
            AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            new CurrentControlTransitionJournal(database,_budget).AssertSigningInventoryUnchanged(inventory,fence.Attempt,token);return true;
        },token),caller);
    }
}
