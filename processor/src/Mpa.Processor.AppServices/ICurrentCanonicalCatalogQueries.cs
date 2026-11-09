using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Borrowed historical metadata queries only. No refresh, publication,
/// original access, source approval or disposal capability is transferred.</summary>
public interface ICurrentCanonicalCatalogQueries
{
    Task<CurrentCanonicalReadPage?> ReadPageAsync(Guid? expectedStageId,CatalogCursor? after,int limit,CancellationToken caller);
    Task<CurrentCanonicalReadPage?> ReadPreviousPageAsync(Guid expectedStageId,CatalogCursor before,int limit,CancellationToken caller);
    Task<CurrentCanonicalReadPage?> ReadAtOrBeforeAsync(Guid? expectedStageId,long utcMilliseconds,int limit,CancellationToken caller);
    Task<CurrentCanonicalReadAsset?> FindAssetAsync(Guid expectedStageId,Guid assetId,CancellationToken caller);
    Task<CurrentCanonicalReadPage?> ReadAccountPageAsync(Guid? expectedStageId,CurrentCatalogAccountFilter account,CatalogCursor? after,int limit,CancellationToken caller);
    Task<CurrentCanonicalReadPage?> ReadAccountPreviousPageAsync(Guid expectedStageId,CurrentCatalogAccountFilter account,CatalogCursor before,int limit,CancellationToken caller);
    Task<CurrentCanonicalReadPage?> ReadAccountAtOrBeforeAsync(Guid? expectedStageId,CurrentCatalogAccountFilter account,long utcMilliseconds,int limit,CancellationToken caller);
    Task<CurrentCanonicalReadAsset?> FindAccountAssetAsync(Guid expectedStageId,CurrentCatalogAccountFilter account,Guid assetId,CancellationToken caller);
}
