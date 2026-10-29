using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Own the actual current reads and derive the native predecessor and
    /// source configuration internally. A caller supplies only the original capture
    /// identity; returned planning progress is never an upload/activation grant.</summary>
    public Task<CurrentPreparedCatalogBasis> PrepareCurrentPublicationBasisAsync(Guid generationId,CancellationToken caller)
    {
        if(generationId==Guid.Empty) throw new ArgumentException("An original generation identity is required.",nameof(generationId));
        return OperationAsync(async token=>
        {
            var capture=await CaptureCoreAsync(caller,token).ConfigureAwait(false);
            var inventory=await ObserveWorkspaceInventoryCoreAsync(capture,caller,token).ConfigureAwait(false);
            return await _state.ExecuteAsync(database=>
            {
                Check(caller,token);AssertSnapshotInventory(database,inventory);
                return new CatalogGenerationCaptureRepository(database,_budget).PrepareCurrentPublicationBasis(generationId,
                    inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
            },token).ConfigureAwait(false);
        },caller);
    }
}
