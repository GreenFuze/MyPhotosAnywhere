using Mpa.Processor.Core;

namespace Mpa.Processor.Web;

public sealed record DesktopImports(bool CanChooseFolder,IReadOnlyList<TakeoutImportActivity> Imports,bool Paused=false);
public interface IDesktopImportApplication
{
    Task<DesktopImports> ImportsAsync(CancellationToken token);
    Task<TakeoutMetadataIssuePage> ImportMetadataIssuesAsync(Guid importId,string? after,CancellationToken token);
    Task<TakeoutImportActivity?> ChooseImportFolderAsync(Guid sourceId,CancellationToken token);
    Task<TakeoutImportActivity> StartImportAsync(Guid importId,CancellationToken token);
    Task<TakeoutImportActivity> RetryImportAsync(Guid importId,CancellationToken token);
}
