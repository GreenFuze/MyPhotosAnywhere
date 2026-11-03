using Mpa.Processor.AppServices;

namespace Mpa.Processor.Web;

public interface IDesktopLibraryMetadataSourceApplication
{
    Task<RecoveredMetadataSource?> LibraryMetadataSourceAsync(Guid stageId,Guid assetId,string? accountId,CancellationToken cancellationToken);
}
