using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Stateless borrowing adapter. The actual canonical runtime owns and
/// joins admission, streaming and rejected-result cleanup; no authority is minted.</summary>
public sealed class CurrentCanonicalViewerSource(CurrentCanonicalReadRuntime catalog) : ICanonicalCatalogViewerSource
{
    private readonly CurrentCanonicalReadRuntime _catalog=catalog ?? throw new ArgumentNullException(nameof(catalog));
    public bool Borrows(ProcessorStateCoordinator state,StorageBudgetManager storage)=>_catalog.BorrowsViewerOwners(state,storage);
    public Task<CatalogViewerSessionIndex> CaptureAsync(CatalogViewerRequest request,string ownedIndexPath,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.StageId is not {} stage || stage==Guid.Empty) throw new ArgumentException("An exact canonical viewer stage is required.",nameof(request));
        return _catalog.CaptureViewerIndexAsync(stage,ownedIndexPath,request.SelectedId,request.AccountId is null?null:new(request.AccountId),cancellationToken);
    }
}

public sealed partial class CurrentCanonicalReadRuntime
{
    internal bool BorrowsViewerOwners(ProcessorStateCoordinator state,StorageBudgetManager storage)=>ReferenceEquals(state,_state) && ReferenceEquals(storage,_budget);
}
