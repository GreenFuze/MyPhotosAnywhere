using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Same-reader selected graph plus complete immutable inventory and ONE
/// actual final Root fence. The native stage must already have been sealed while
/// captured. This result grants no outgoing generation or original-file operation.</summary>
public sealed class LocalCurrentReconciledCatalog
{
    internal object Owner {get;}
    internal LocalCurrentWorkspaceInventory Inventory {get;}
    internal LocalCurrentGraphFence Fence {get;}
    public LocalCurrentControlGraph Graph=>Fence.Graph;
    public CurrentWorkspaceInventoryCheckpoint Checkpoint=>Inventory.Checkpoint;
    internal LocalCurrentReconciledCatalog(object owner,LocalCurrentWorkspaceInventory inventory,LocalCurrentGraphFence fence)
    {Owner=owner;Inventory=inventory;Fence=fence;}
}

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Read all remaining immutable metadata after selected graph/leaf
    /// preparation and native sealing. Reuse that actual last Root fence; never
    /// manufacture another captured nonce or open a later unchecked network read.</summary>
    public Task<LocalCurrentReconciledCatalog> ObserveSelectedInventoryAsync(LocalCurrentControlGraph graph,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return OperationAsync(async token=>
        {
            if(!ReferenceEquals(graph.Owner,_identity) || !ReferenceEquals(graph,_graph) || graph.Stage is not LocalCurrentSqliteStage stage)
                throw new CurrentControlObservationConflictException();
            return await stage.UseSealedCurrentGraphAsync(_state,graph,async owned=>
            {
                var inventory=await ObserveWorkspaceInventoryCoreAsync(graph.Policies.Capture,caller,owned).ConfigureAwait(false);
                Check(caller,owned);
                if(!ReferenceEquals(graph,_graph) || !ReferenceEquals(inventory.Owner,_identity) ||
                    !ReferenceEquals(inventory.Fence.Capture,graph.Policies.Capture)) throw new CurrentControlObservationConflictException();
                return new LocalCurrentReconciledCatalog(_identity,inventory,new(_identity,graph,inventory.Fence));
            },token).ConfigureAwait(false);
        },caller);
    }

    /// <summary>The joined sealed file owner stays live across complete inventory,
    /// selected graph, current human policy and pointer/terminal admission in one
    /// transaction. A stored record alone cannot reconstruct this opaque result.</summary>
    public Task<CurrentCatalogActivation> ActivateReconciledAsync(LocalCurrentReconciledCatalog catalog,LocalCurrentSqliteStage stage,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(catalog);ArgumentNullException.ThrowIfNull(stage);
        return OperationAsync(async token=>
        {
            var inventory=catalog.Inventory;var fence=catalog.Fence;
            if(!ReferenceEquals(catalog.Owner,_identity) || !ReferenceEquals(inventory.Owner,_identity) ||
                !ReferenceEquals(fence.Owner,_identity) || !ReferenceEquals(fence.Graph.Owner,_identity) ||
                !ReferenceEquals(fence.Current,inventory.Fence) || !ReferenceEquals(fence.Graph,_graph) ||
                !ReferenceEquals(fence.Graph.Policies.Capture,fence.Current.Capture)) throw new CurrentControlObservationConflictException();
            var publisher=fence.Graph.Policies.PublisherEnvelope;var sources=fence.Graph.Policies.SourceEnvelope;
            return await stage.UseSealedGraphAsync(_state,fence,(claim,owned)=>_state.ExecuteAsync(database=>
            {
                Check(caller,owned);_keys.AssertControllerIdentity(fence.Current.Capture.Pin);
                return new CurrentCatalogActivationRepository(database,_budget).ActivateReconciled(fence.Current.Attempt,claim,publisher,sources,
                    fence.Graph.Sequence,fence.Graph.Commits,fence.Graph.Observations,inventory.Checkpoint,inventory.Signing,inventory.Publications,owned);
            },owned),token).ConfigureAwait(false);
        },caller);
    }
}
