using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed partial class CurrentCatalogHeaderAllocationService
{
    private async Task<WorkspacePublicationRecord> AllocateObservedAsync(IGoogleWorkspaceObservedAllocation writer,
        LocalCurrentCatalogHeaderCloudAdmission admission,WorkspacePublicationRecord header,CancellationToken caller,CancellationToken token)
    {
        var reserved=admission.Inventory.Publications.Objects.Where(value=>value.FileId is not null)
            .Select(value=>value.FileId!).ToHashSet(StringComparer.Ordinal);
        var capture=admission.Inventory.Fence.Capture;
        reserved.Add(capture.Slot.FileId!);reserved.Add(capture.Head.Publishers.Reference.FileId);reserved.Add(capture.Head.Sources.Reference.FileId);
        if(capture.Head.Catalog is {} catalog) reserved.Add(catalog.FileId);
        var observer=new AllocationObserver(this,header,reserved);
        var allocated=await writer.ObserveWorkspaceObjectIdAllocationAsync(observer,async owned=>
        {
            Check(caller,owned);
            var actual=await _reader.AssertCatalogHeaderCloudAdmissionAsync(admission,owned).ConfigureAwait(false);
            RequireSame(header,actual);
            if(actual.FileId is not null) throw new WorkspacePublicationInventoryChangedException();
        },token).ConfigureAwait(false);
        var saved=observer.Saved ?? throw new InvalidOperationException("The allocation provider did not join its known-ID checkpoint.");
        if(allocated!=saved.FileId) throw new InvalidDataException("Allocation result differs from its first actual checkpointed identity.");
        Check(caller,token);return saved;
    }

    private sealed class AllocationObserver(CurrentCatalogHeaderAllocationService owner,WorkspacePublicationRecord header,
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
            if(allocation.AccountId!=header.Scope.AccountId || allocation.FileId is not {Length:>0 and <=256} ||
                allocation.FileId is "root" || allocation.FileId==header.Scope.FolderId ||
                allocation.FileId.Any(value=>!char.IsAsciiLetterOrDigit(value) && value is not ('_' or '-')) || reserved.Contains(allocation.FileId))
                throw new InvalidDataException("Provider returned a conflicting identity instead of a new canonical metadata allocation.");
            if(owner._pending is {} pending && (pending.Header.Id!=header.Id || pending.FileId!=allocation.FileId))
                throw new InvalidOperationException("A previous known header allocation still owns its checkpoint.");
            // Deliberately BEFORE any cancellation/result admission. PersistPending
            // uses only original immutable/native/local history, not renewed authority.
            owner._pending=new(header,allocation.FileId);
            var saved=await owner.PersistPendingAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Known header allocation lost its checkpoint owner.");
            lock(_gate) _saved=saved;
        }
    }
}
