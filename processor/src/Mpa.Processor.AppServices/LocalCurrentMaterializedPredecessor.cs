using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Opaque same-reader inspection. Preserves current selected authority
/// separately from an exact owned unselected predecessor. No disposal/publication
/// or successor admission is authorized by retaining or serializing this object.</summary>
public sealed class LocalCurrentMaterializedPredecessor
{
    internal object Owner {get;}
    internal LocalCurrentWorkspaceInventory Inventory {get;}
    public CurrentMaterializedCatalogPredecessor Original {get;}
    internal LocalCurrentMaterializedPredecessor(object owner,LocalCurrentWorkspaceInventory inventory,CurrentMaterializedCatalogPredecessor original)
    {Owner=owner;Inventory=inventory;Original=original;}
}

public sealed partial class LocalCurrentControlReader
{
    public Task<LocalCurrentMaterializedPredecessor> InspectMaterializedPredecessorAsync(LocalCurrentWorkspaceInventory inventory,Guid generationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertPredecessorInventoryOwner(inventory);
            var fence=inventory.Fence;AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            var original=new CatalogGenerationCaptureRepository(database,_budget).InspectMaterializedPredecessor(generationId,
                inventory.Checkpoint,inventory.Signing,fence.Attempt,inventory.Publications,token);
            Check(caller,token);return new LocalCurrentMaterializedPredecessor(_identity,inventory,original);
        },token),caller);
    }

    public Task AssertMaterializedPredecessorUnchangedAsync(LocalCurrentMaterializedPredecessor predecessor,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(predecessor);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);if(!ReferenceEquals(predecessor.Owner,_identity)) throw new CurrentControlObservationConflictException();
            var inventory=predecessor.Inventory;AssertPredecessorInventoryOwner(inventory);
            var fence=inventory.Fence;AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            var actual=new CatalogGenerationCaptureRepository(database,_budget).InspectMaterializedPredecessor(predecessor.Original.OriginalScope.GenerationId,
                inventory.Checkpoint,inventory.Signing,fence.Attempt,inventory.Publications,token);
            if(actual.HumanConfigurationRevision==predecessor.Original.HumanConfigurationRevision &&
                actual.HumanConfigurationSha256!=predecessor.Original.HumanConfigurationSha256)
                throw new InvalidDataException("Human source bindings changed without advancing their configuration revision.");
            if(actual.ClaimSha256!=predecessor.Original.ClaimSha256) throw new StaleLibraryConfigurationException();
            Check(caller,token);return true;
        },token),caller);
    }

    private void AssertPredecessorInventoryOwner(LocalCurrentWorkspaceInventory inventory)
    {
        if(!ReferenceEquals(inventory.Owner,_identity) || !ReferenceEquals(inventory.Fence.Owner,_identity))
            throw new CurrentControlObservationConflictException();
    }
}
