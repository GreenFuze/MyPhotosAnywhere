using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Historical original-authority inspection only. All entered work
    /// joins this reader's state/native lifetime, with no additional network call
    /// or signing. A foreign or stale fence fails before reading the capture.</summary>
    public Task<CurrentOriginalCatalogAuthority> InspectOriginalCatalogAuthorityAsync(LocalCurrentControlFence fence,Guid generationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(fence);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertHeaderFenceOwner(database,fence);
            var signing=new CurrentControlTransitionJournal(database,_budget).CaptureSigningInventory(fence.Attempt,token);
            var result=new CatalogGenerationCaptureRepository(database,_budget).InspectOriginalCatalogAuthority(generationId,signing,fence.Attempt,token);
            Check(caller,token);return result;
        },token),caller);
    }
}
