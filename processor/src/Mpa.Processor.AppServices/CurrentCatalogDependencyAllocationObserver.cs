using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed partial class CurrentCatalogDependencyAllocationService
{
    private async Task<WorkspacePublicationRecord> AllocateObservedAsync(IGoogleWorkspaceObservedAllocation writer,
        LocalCurrentCatalogDependencyCloudAdmission admission,WorkspacePublicationRecord dependency,CancellationToken caller,CancellationToken token)
    {
        var reserved=admission.Inventory.Publications.Objects.Where(value=>value.FileId is not null)
            .Select(value=>value.FileId!).ToHashSet(StringComparer.Ordinal);
        var capture=admission.Inventory.Fence.Capture;
        reserved.Add(capture.Slot.FileId!);reserved.Add(capture.Head.Publishers.Reference.FileId);reserved.Add(capture.Head.Sources.Reference.FileId);
        if(capture.Head.Catalog is {} catalog) reserved.Add(catalog.FileId);
        var observer=new AllocationObserver(this,admission.Original,dependency,reserved);
        var allocated=await writer.ObserveWorkspaceObjectIdAllocationAsync(observer,async owned=>
        {
            Check(caller,owned);
            var actual=await _reader.AssertCatalogDependencyCloudAdmissionAsync(admission,owned).ConfigureAwait(false);
            RequireSame(dependency,actual);
            if(actual.FileId is not null) throw new WorkspacePublicationInventoryChangedException();
        },token).ConfigureAwait(false);
        var saved=observer.Saved ?? throw new InvalidOperationException("The allocation provider did not join its known-ID checkpoint.");
        if(allocated!=saved.FileId) throw new InvalidDataException("Allocation result differs from its first actual checkpointed identity.");
        Check(caller,token);return saved;
    }

    private sealed class AllocationObserver(CurrentCatalogDependencyAllocationService owner,CurrentCatalogDependencyCloudInspection original,WorkspacePublicationRecord dependency,
        HashSet<string> reserved) : IGoogleWorkspaceAllocationObserver
    {
        private readonly object _gate=new();
        private DriveWorkspaceAllocation? _receipt;
        private Task? _retaining;
        private WorkspacePublicationRecord? _saved;
        internal WorkspacePublicationRecord? Saved {get {lock(_gate) return _saved;}}

        public Task RememberAllocatedIdAsync(DriveWorkspaceAllocation allocation)
        {
            ArgumentNullException.ThrowIfNull(allocation);
            lock(_gate)
            {
                if(_receipt is not null && _receipt!=allocation) throw new InvalidDataException("Provider replaced a known allocation inside one operation.");
                _receipt??=allocation;return _retaining??=RetainAsync(allocation);
            }
        }
        private async Task RetainAsync(DriveWorkspaceAllocation allocation)
        {
            // A malformed or already-used provider ID is an invalid receipt, never
            // permission to overwrite the Root, another metadata object or a source.
            if(allocation.AccountId!=dependency.Scope.AccountId || allocation.FileId is not {Length:>0 and <=256} ||
                allocation.FileId is "root" || allocation.FileId==dependency.Scope.FolderId ||
                allocation.FileId.Any(value=>!char.IsAsciiLetterOrDigit(value) && value is not ('_' or '-')) || reserved.Contains(allocation.FileId))
                throw new InvalidDataException("Provider returned a conflicting identity instead of a new canonical metadata allocation.");
            if(owner._pending is {} pending && (pending.Original.Dependency.Id!=dependency.Id || pending.FileId!=allocation.FileId))
                throw new InvalidOperationException("A previous known dependency allocation still owns its checkpoint.");
            // Deliberately BEFORE any cancellation/result admission. PersistPending
            // uses only original immutable/native/local history, not renewed authority.
            owner._pending=new(original,allocation.FileId);
            try
            {
                var saved=await owner.PersistPendingAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Known dependency allocation lost its checkpoint owner.");
                lock(_gate) _saved=saved;
            }
            catch(WorkspaceAllocationAliasException error)
            {
                // Only a proven pre-existing-object collision is an invalid
                // receipt, not a valid known ID awaiting durable storage. Other
                // failures keep the exact pending checkpoint and block Exit.
                owner._pending=null;
                throw new InvalidDataException("Provider returned an existing protected object instead of a new dependency allocation.",error);
            }
        }
    }
}
