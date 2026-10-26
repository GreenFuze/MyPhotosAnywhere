using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Local canonical admission only. The last actual network fence must
    /// already have completed in this same reader. Reader, stage and native file
    /// owners stay joined through the single state transaction. No outgoing Root
    /// update, PublishedHead receipt or permission to change originals is granted.</summary>
    public Task<CurrentCatalogActivation> ActivateAsync(LocalCurrentGraphFence fence,LocalCurrentSqliteStage stage,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(fence);ArgumentNullException.ThrowIfNull(stage);
        return OperationAsync(async token=>
        {
            if(!ReferenceEquals(fence.Owner,_identity) || !ReferenceEquals(fence.Graph.Owner,_identity) || !ReferenceEquals(fence.Current.Owner,_identity) ||
                !ReferenceEquals(fence.Graph,_graph) || !ReferenceEquals(fence.Graph.Policies.Capture,fence.Current.Capture)) throw new CurrentControlObservationConflictException();
            var publisher=fence.Graph.Policies.PublisherEnvelope;var sources=fence.Graph.Policies.SourceEnvelope;
            return await stage.UseSealedGraphAsync(_state,fence,(claim,owned)=>_state.ExecuteAsync(database=>
            {
                Check(caller,owned);_keys.AssertControllerIdentity(fence.Current.Capture.Pin);
                return new CurrentCatalogActivationRepository(database,_budget).Activate(fence.Current.Attempt,claim,publisher,sources,
                    fence.Graph.Sequence,fence.Graph.Commits,fence.Graph.Observations,owned);
            },owned),token).ConfigureAwait(false);
        },caller);
    }
}
