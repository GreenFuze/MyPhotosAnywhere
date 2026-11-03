namespace Mpa.Processor.Core;

/// <summary>Borrow an exact canonical read owner for one disposable identity
/// capture. On unconfirmed cleanup, throw CatalogSessionCaptureException carrying
/// that actual owner; the session owner retains it until physical disposal.</summary>
public interface ICanonicalCatalogViewerSource
{
    bool Borrows(ProcessorStateCoordinator state,StorageBudgetManager storage);
    Task<CatalogViewerSessionIndex> CaptureAsync(CatalogViewerRequest request,string ownedIndexPath,CancellationToken cancellationToken);
}
