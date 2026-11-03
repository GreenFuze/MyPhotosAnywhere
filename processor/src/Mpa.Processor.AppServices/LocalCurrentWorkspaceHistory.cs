using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Classify retained workspace history under this live actual Root
    /// capture and its complete native signing witness. Only the exact pinned current
    /// ID is separated. No observation, unread candidate, failed attempt or cursor is
    /// removed/rewritten; no network, completion or policy/activation grant occurs.</summary>
    public Task<CurrentWorkspaceObservationHistory> CaptureWorkspaceHistoryAsync(CurrentControlSigningInventory signing,
        LocalCurrentControlCapture capture,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(capture);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);
            if(!ReferenceEquals(capture.Owner,_identity)) throw new CurrentControlObservationConflictException();
            AssertScope(database,new(capture.Pin,capture.Slot,capture.Attempt));
            return new WorkspaceObservationRepository(database,_budget).CaptureCurrentHistory(signing,capture.Attempt,token);
        },token),caller);
    }
}
