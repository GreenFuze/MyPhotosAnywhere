using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Serialize after this owner's previous work and borrow the shared
    /// account lease, which joins prior actual writes. Persist a fresh actual nonce
    /// and Root fence before withdrawal. No target graph, obsolete human-source
    /// admission, fabricated capture or cloud update is needed. Prior usable content
    /// and all original pending publication identities/snapshots remain intact.</summary>
    public Task<CurrentControlTransitionWithdrawal> WithdrawTransitionAsync(Guid transitionId,
        CurrentControlTransitionWithdrawalReason reason,CancellationToken caller)
    {
        if(!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return OperationAsync(async token=>
        {
            var old=await _state.ExecuteAsync(database=>
            {Check(caller,token);return new CurrentControlTransitionJournal(database,_budget).ReadWithdrawal(transitionId,token);},token).ConfigureAwait(false);
            if(old is not null)
            {
                if(old.Reason!=reason) throw new CurrentControlObservationConflictException();
                return old; // Historical replay remains readable after disconnection.
            }
            var capture=await CaptureCoreAsync(caller,token).ConfigureAwait(false);
            var fence=await FenceCapturedCoreAsync(capture,caller,token).ConfigureAwait(false);
            return await _state.ExecuteAsync(database=>
            {
                Check(caller,token);_keys.AssertControllerIdentity(capture.Pin);
                AssertScope(database,new(capture.Pin,capture.Slot,fence.Attempt));
                return new CurrentControlTransitionJournal(database,_budget).Withdraw(transitionId,fence.Attempt,reason,token);
            },token).ConfigureAwait(false);
        },caller);
    }
}
