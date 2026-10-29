using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Admit only new work through this reader's actual final inventory
    /// and native key owner. Existing work is never relabeled with this authority.
    /// Snapshot capture, recovery and publication retain their separate owners.</summary>
    public Task<CatalogGenerationCapture> BeginNewCurrentGenerationAsync(LocalCurrentWorkspaceInventory inventory,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);
            if(!ReferenceEquals(inventory.Owner,_identity)) throw new CurrentControlObservationConflictException();
            AssertHeaderFenceOwner(database,inventory.Fence);
            var authority=new LibraryAuthorityRepository(database,_budget);
            _keys.AssertPublicationIdentity(authority.Controller() ?? throw new CatalogPublicationNotAuthorizedException(),authority.CaptureLocalPublicationAuthority());
            return new CatalogGenerationCaptureRepository(database,_budget).BeginNewCurrentGeneration(inventory.Checkpoint,inventory.Signing,
                inventory.Fence.Attempt,inventory.Publications,token);
        },token),caller);
    }
}
