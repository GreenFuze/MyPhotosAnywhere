using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    internal bool BorrowsSnapshotState(ProcessorStateCoordinator state,StorageBudgetManager budget)=>ReferenceEquals(state,_state) && ReferenceEquals(budget,_budget);

    // The actual reader lane owns the WHOLE private-copy operation, including the
    // child's joined disposal. Stop cannot release native/account/state borrowers
    // while a copy or retained file is still live. No account lease is held for copying.
    internal Task<T> WithCurrentSnapshotAsync<T>(Func<CatalogGenerationCapture,IWorkspacePublicationAuthority,CancellationToken,Task<T>> prepare,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return OperationAsync(async token=>
        {
            var capture=await CaptureCoreAsync(caller,token).ConfigureAwait(false);
            var inventory=await ObserveWorkspaceInventoryCoreAsync(capture,caller,token).ConfigureAwait(false);
            var claim=await _state.ExecuteAsync(database=>
            {
                Check(caller,token);AssertSnapshotInventory(database,inventory);
                var journal=new CatalogGenerationCaptureRepository(database,_budget);var old=journal.Active().SingleOrDefault();
                return old is null ? journal.BeginNewCurrentGeneration(inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token)
                    : journal.ValidateCurrentSnapshot(old.Scope.GenerationId,inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
            },token).ConfigureAwait(false);
            var permission=new CurrentSnapshotPermission(this,inventory,claim,caller);
            Check(caller,token);return await prepare(claim,permission,token).ConfigureAwait(false);
        },caller);
    }

    private void AssertSnapshotInventory(ProcessorStateDatabase database,LocalCurrentWorkspaceInventory inventory)
    {
        if(!ReferenceEquals(inventory.Owner,_identity)) throw new CurrentControlObservationConflictException();
        AssertHeaderFenceOwner(database,inventory.Fence);var authority=new LibraryAuthorityRepository(database,_budget);
        _keys.AssertPublicationIdentity(authority.Controller() ?? throw new CatalogPublicationNotAuthorizedException(),authority.CaptureLocalPublicationAuthority());
    }

    // Deliberately private and never returned as a cloud grant. This object is
    // immutable for one joined Reader operation and validates in the actual state
    // lane directly; recursively taking the reader lane would deadlock its owner.
    private sealed class CurrentSnapshotPermission(LocalCurrentControlReader owner,LocalCurrentWorkspaceInventory inventory,
        CatalogGenerationCapture original,CancellationToken caller) : IWorkspacePublicationAuthority,ICurrentCatalogSnapshotAdmission
    {
        public Task ValidateAsync(WorkspacePublicationScope scope,CancellationToken token)=>owner._state.ExecuteAsync(database=>
        {Assert(database,scope,token);return true;},token);
        public CatalogGenerationCapture Begin(ProcessorStateDatabase database,WorkspacePublicationScope scope,Guid snapshotId,Guid intentId,CancellationToken token)
        {
            var actual=Assert(database,scope,token);
            if(snapshotId!=original.SnapshotId || intentId!=original.CommitIntentId) throw new CatalogGenerationCaptureConflictException();
            return actual;
        }
        public CatalogGenerationCapture Seal(ProcessorStateDatabase database,Guid generationId,CatalogSnapshotDescriptor descriptor,CancellationToken token)
        {
            _=Assert(database,original.Scope,token);
            if(generationId!=original.Scope.GenerationId) throw new CatalogGenerationCaptureConflictException();
            return new CatalogGenerationCaptureRepository(database,owner._budget).SealCurrentSnapshot(generationId,descriptor,
                inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
        }
        private CatalogGenerationCapture Assert(ProcessorStateDatabase database,WorkspacePublicationScope scope,CancellationToken token)
        {
            owner.Check(caller,token);owner.AssertSnapshotInventory(database,inventory);
            if(scope!=original.Scope) throw new CatalogGenerationCaptureConflictException();
            var value=new CatalogGenerationCaptureRepository(database,owner._budget).ValidateCurrentSnapshot(scope.GenerationId,
                inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
            if(value.SnapshotId!=original.SnapshotId || value.CommitIntentId!=original.CommitIntentId) throw new CatalogGenerationCaptureConflictException();
            return value;
        }
    }
}
