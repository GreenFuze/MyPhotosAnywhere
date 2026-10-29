using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    internal bool BorrowsDependencyAllocationOwners(ProcessorStateCoordinator state,ProcessorAccountService accounts,StorageBudgetManager budget)=>
        ReferenceEquals(state,_state) && ReferenceEquals(accounts,_accounts) && ReferenceEquals(budget,_budget);

    public Task<LocalCurrentCatalogDependencyCloudAdmission> InspectCatalogDependencyCloudAsync(LocalCurrentWorkspaceInventory inventory,
        Guid publicationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if(publicationId==Guid.Empty) throw new ArgumentException("An original dependency identity is required.",nameof(publicationId));
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertPredecessorInventoryOwner(inventory);AssertSnapshotInventory(database,inventory);
            var original=new CatalogGenerationCaptureRepository(database,_budget).InspectCurrentCatalogDependencyCloud(publicationId,
                inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
            Check(caller,token);return new LocalCurrentCatalogDependencyCloudAdmission(_identity,inventory,original);
        },token),caller);
    }

    internal void AssertCatalogDependencyCloudAdmissionOwner(LocalCurrentCatalogDependencyCloudAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping,this);
            if(!ReferenceEquals(admission.Owner,_identity) || !ReferenceEquals(admission.Inventory.Fence.Capture.Owner,_identity))
                throw new CurrentControlObservationConflictException();
            AssertPredecessorInventoryOwner(admission.Inventory);
        }
    }

    // Actual state-only admission inside a provider lease. Never enter the reader
    // network lane or recursively acquire the account from this callback.
    public Task<WorkspacePublicationRecord> AssertCatalogDependencyCloudAdmissionAsync(LocalCurrentCatalogDependencyCloudAdmission admission,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return StateOnlyOperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertCatalogDependencyCloudAdmissionOwner(admission);
            var inventory=admission.Inventory;AssertSnapshotInventory(database,inventory);
            var expected=admission.Original;
            var actual=new CatalogGenerationCaptureRepository(database,_budget).InspectCurrentCatalogDependencyCloud(expected.Dependency.Id,
                inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
            var left=actual.Dependency;var right=expected.Dependency;
            if(actual.Publication!=expected.Publication || actual.Inventory!=expected.Inventory || left.Id!=right.Id || left.Scope!=right.Scope ||
                left.Role!=right.Role || left.MimeType!=right.MimeType || left.Sha256!=right.Sha256 || left.FileId!=right.FileId ||
                left.VerifiedVersion!=right.VerifiedVersion || !left.Payload.Span.SequenceEqual(right.Payload.Span))
                throw new WorkspacePublicationConflictException();
            Check(caller,token);return left;
        },token),caller);
    }
}
