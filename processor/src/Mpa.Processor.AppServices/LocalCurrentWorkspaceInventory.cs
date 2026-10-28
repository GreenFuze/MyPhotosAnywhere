using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Actual same-reader scan result. Stored JSON cannot construct this live
/// capability. It is not catalog activation, policy selection or generation completion.</summary>
public sealed class LocalCurrentWorkspaceInventory
{
    internal object Owner {get;}
    public CurrentWorkspaceInventoryCheckpoint Checkpoint {get;}
    internal CurrentControlSigningInventory Signing {get;}
    internal WorkspacePublicationInventory Publications {get;}
    internal LocalCurrentControlFence Fence {get;}
    internal LocalCurrentWorkspaceInventory(object owner,CurrentWorkspaceInventoryCheckpoint checkpoint,
        CurrentControlSigningInventory signing,WorkspacePublicationInventory publications,LocalCurrentControlFence fence)
    {Owner=owner;Checkpoint=checkpoint;Signing=signing;Publications=publications;Fence=fence;}
}

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Read the complete immutable inventory through actual joined account
    /// leases, retain failed history and all native/outbox work, check the immutable
    /// change feed, then make the actual conditional Root fence the LAST network read.
    /// The completion transaction cannot select policies or dispose captured work.</summary>
    public Task<LocalCurrentWorkspaceInventory> ObserveWorkspaceInventoryAsync(LocalCurrentControlCapture capture,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return OperationAsync(token=>ObserveWorkspaceInventoryCoreAsync(capture,caller,token),caller);
    }

    private CurrentWorkspaceReadCache? _completedDocumentReads;

    /// <summary>Complete current scan reusing only this live reader's previously
    /// completed identical-version observations. Actual listing, access/history,
    /// changes and final Root reads remain required; restart/failure drops reuse.
    /// ObserveWorkspaceInventoryAsync retains its full actual-body-read contract.</summary>
    public Task<LocalCurrentWorkspaceInventory> ObserveWorkspaceInventorySessionAsync(LocalCurrentControlCapture capture,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return OperationAsync(token=>ObserveWorkspaceInventoryCoreAsync(capture,caller,token,reuseCompletedReads:true),caller);
    }

    private async Task<LocalCurrentWorkspaceInventory> ObserveWorkspaceInventoryCoreAsync(LocalCurrentControlCapture capture,CancellationToken caller,CancellationToken token,bool reuseCompletedReads=false)
    {
        try {return await ObserveWorkspaceInventoryOwnedAsync(capture,reuseCompletedReads,caller,token).ConfigureAwait(false);}
        catch {_completedDocumentReads=null;throw;}
    }

    private async Task<LocalCurrentWorkspaceInventory> ObserveWorkspaceInventoryOwnedAsync(LocalCurrentControlCapture capture,bool reuseCompletedReads,CancellationToken caller,CancellationToken token)
    {
        var history=await _state.ExecuteAsync(database=>
        {
            Check(caller,token);if(!ReferenceEquals(capture.Owner,_identity)) throw new CurrentControlObservationConflictException();
            AssertScope(database,new(capture.Pin,capture.Slot,capture.Attempt));
            var signing=new CurrentControlTransitionJournal(database,_budget).CaptureSigningInventory(capture.Attempt,token);
            return new WorkspaceObservationRepository(database,_budget).CaptureCurrentHistory(signing,capture.Attempt,token);
        },token).ConfigureAwait(false);
        var pin=LibraryConnectionDescriptor.Decode(history.Signing.ConnectionCode);
        var channel=new CurrentRootInventoryChannel(pin.CurrentFileId!,pin.Workspace.FolderId,capture.Attempt,
            async(version,owned)=>
            {
                await _state.ExecuteAsync(database=>
                {
                    Check(caller,owned);AssertScope(database,new(capture.Pin,capture.Slot,capture.Attempt));
                    new LocalCurrentControlObservations(database,_budget).RejectInventoryObservation(capture.Attempt,version,owned);return true;
                },owned).ConfigureAwait(false);
            });
        var source=new CatalogWorkspaceDocumentSource(_state,_accounts,pin.Workspace,capture.Attempt.SharedDriveId,capture.Attempt.AccountRevision,channel);
        await using var scan=new WorkspaceObservationService(_state,_budget,history.Context,source);
        return await scan.ScanCurrentObservedAsync(channel.FileId,async(attempt,cursor,observed,publications,owned)=>
        {
            Check(caller,owned);
            var fence=await FenceCapturedCoreAsync(capture,caller,owned).ConfigureAwait(false);
            var checkpoint=await _state.ExecuteAsync(database=>
            {
                Check(caller,owned);AssertScope(database,new(capture.Pin,capture.Slot,fence.Attempt));
                return new WorkspaceObservationRepository(database,_budget).CompleteCurrentInventory(attempt,cursor.Token,observed,history.Signing,fence.Attempt,publications,owned);
            },owned).ConfigureAwait(false);
            Check(caller,owned);
            _completedDocumentReads=new CurrentWorkspaceReadCache(history.Context,observed);
            return new LocalCurrentWorkspaceInventory(_identity,checkpoint,history.Signing,publications,fence);
        },token,reuseCompletedReads?_completedDocumentReads:null).ConfigureAwait(false);
    }

    public Task AssertWorkspaceInventoryUnchangedAsync(LocalCurrentWorkspaceInventory inventory,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);
            if(!ReferenceEquals(inventory.Owner,_identity) || !ReferenceEquals(inventory.Fence.Owner,_identity)) throw new CurrentControlObservationConflictException();
            var fence=inventory.Fence;AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            new WorkspaceObservationRepository(database,_budget).AssertCurrentInventoryUnchanged(inventory.Checkpoint,inventory.Signing,fence.Attempt,inventory.Publications,token);
            return true;
        },token),caller);
    }
}
