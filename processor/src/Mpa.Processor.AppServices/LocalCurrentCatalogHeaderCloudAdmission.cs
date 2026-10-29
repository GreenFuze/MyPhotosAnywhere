using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    public Task<LocalCurrentCatalogHeaderCloudAdmission> InspectCatalogHeaderCloudAsync(
        LocalCurrentWorkspaceInventory inventory,Guid generationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertPredecessorInventoryOwner(inventory);
            var fence=inventory.Fence;AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            var original=new CatalogGenerationCaptureRepository(database,_budget).InspectCurrentCatalogHeaderCloud(generationId,
                inventory.Checkpoint,inventory.Signing,fence.Attempt,inventory.Publications,token);
            Check(caller,token);return new LocalCurrentCatalogHeaderCloudAdmission(_identity,inventory,original);
        },token),caller);
    }

    // Identity only: this never admits provider IO. Each physical request still
    // requires the complete transactional assertion below, under its account lease.
    internal void AssertCatalogHeaderCloudAdmissionOwner(LocalCurrentCatalogHeaderCloudAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping,this);
            if(!ReferenceEquals(admission.Owner,_identity) ||
                !ReferenceEquals(admission.Inventory.Fence.Capture.Owner,_identity))
                throw new CurrentControlObservationConflictException();
            AssertPredecessorInventoryOwner(admission.Inventory);
        }
    }

    // State-only check inside a caller's real account lease. Do not acquire an
    // account or perform a second network reconciliation while the lease is held.
    public Task<WorkspacePublicationRecord> AssertCatalogHeaderCloudAdmissionAsync(
        LocalCurrentCatalogHeaderCloudAdmission admission,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return StateOnlyOperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);
            if(!ReferenceEquals(admission.Owner,_identity)) throw new CurrentControlObservationConflictException();
            var inventory=admission.Inventory;AssertPredecessorInventoryOwner(inventory);
            var fence=inventory.Fence;AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            var actual=new CatalogGenerationCaptureRepository(database,_budget).InspectCurrentCatalogHeaderCloud(
                admission.Original.Identity.OriginalScope.GenerationId,inventory.Checkpoint,inventory.Signing,
                fence.Attempt,inventory.Publications,token);
            var expected=admission.Original;
            if(actual.Identity.HumanConfigurationRevision==expected.Identity.HumanConfigurationRevision &&
                actual.Identity.HumanConfigurationSha256!=expected.Identity.HumanConfigurationSha256)
                throw new InvalidDataException("Human source bindings changed without their archived revision.");
            if(actual.Identity.ClaimSha256!=expected.Identity.ClaimSha256 || actual.Inventory!=expected.Inventory ||
                actual.Header.Id!=expected.Header.Id || actual.Header.Scope!=expected.Header.Scope ||
                actual.Header.Role!=expected.Header.Role || actual.Header.MimeType!=expected.Header.MimeType ||
                actual.Header.Sha256!=expected.Header.Sha256 || actual.Header.FileId!=expected.Header.FileId ||
                actual.Header.VerifiedVersion!=expected.Header.VerifiedVersion ||
                !actual.Header.Payload.Span.SequenceEqual(expected.Header.Payload.Span)) throw new StaleLibraryConfigurationException();
            Check(caller,token);return actual.Header;
        },token),caller);
    }
}
