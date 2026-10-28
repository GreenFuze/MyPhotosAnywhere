using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Captures caller bytes before queuing; prepares only original local
    /// dependency retry work after genuine current inventory/final Root reads.</summary>
    public Task<WorkspacePublicationRecord> PrepareCurrentDependencyAsync(Guid generationId,string role,string mime,
        ReadOnlyMemory<byte> payload,CancellationToken caller)
    {
        if(generationId==Guid.Empty) throw new ArgumentException("An original generation identity is required.",nameof(generationId));
        caller.ThrowIfCancellationRequested();var owned=CatalogDependencyPayload.Capture(role,mime,payload.Span);
        return OperationAsync(async token=>
        {
            var capture=await CaptureCoreAsync(caller,token).ConfigureAwait(false);
            var inventory=await ObserveWorkspaceInventoryCoreAsync(capture,caller,token).ConfigureAwait(false);
            return await _state.ExecuteAsync(database=>
            {
                Check(caller,token);AssertSnapshotInventory(database,inventory);
                return new CatalogGenerationCaptureRepository(database,_budget).PrepareCurrentDependency(generationId,owned,
                    inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
            },token).ConfigureAwait(false);
        },caller);
    }
}
