using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Web;

public interface IDesktopLibraryPreviewApplication
{
    Task<DesktopPreview?> LibraryPreviewAsync(Guid stageId,Guid assetId,string? accountId,CancellationToken cancellationToken);
}
public interface IDesktopLibraryPreviewTransport
{
    Task<DesktopPreview?> ReadAsync(string accountId,string fileId,long expectedVersion,CancellationToken cancellationToken);
}
public sealed class DesktopLibraryPreviewUnavailableException() : IOException("Version-checked Google Drive previews are unavailable on this host.");

/// <summary>Use the actual active account reader. Historical metadata supplies an
/// identity/version, never a credential or a provider/write permission. Each real
/// provider metadata observation and thumbnail read uses the current account owner.</summary>
public sealed class DesktopLibraryPreviewTransport(ProcessorAccountService accounts) : IDesktopLibraryPreviewTransport
{
    private readonly ProcessorAccountService _accounts=accounts ?? throw new ArgumentNullException(nameof(accounts));
    public Task<DesktopPreview?> ReadAsync(string accountId,string fileId,long expectedVersion,CancellationToken cancellationToken)=>
        _accounts.ReadDriveAsync<DesktopPreview?>(accountId,async (provider,_,token)=>
        {
            if(provider is not IGoogleVersionedMediaPreview previews) throw new DesktopLibraryPreviewUnavailableException();
            var result=await previews.ReadVersionedMediaPreviewAsync(fileId,expectedVersion,token).ConfigureAwait(false);
            return result is null?null:new(result.Bytes,result.MimeType);
        },cancellationToken);
}

/// <summary>One explicit browser preview. Resolve only the exact historical
/// identity and recheck its stage/account/file/version after actual provider IO.
/// Provider failures propagate; late or substituted bytes are never published.</summary>
public sealed class DesktopLibraryPreviewReader(ICurrentCanonicalCatalogQueries catalog,IDesktopLibraryPreviewTransport transport)
{
    private readonly DesktopLibraryCatalogReader _catalog=new(catalog);
    private readonly IDesktopLibraryPreviewTransport _transport=transport ?? throw new ArgumentNullException(nameof(transport));
    public async Task<DesktopPreview?> ReadAsync(Guid stageId,Guid assetId,string? accountId,CancellationToken caller)
    {
        var captured=await _catalog.ResolveAsync(stageId,assetId,accountId,caller).ConfigureAwait(false);
        if(captured.Asset is not {} asset) return null;
        if(asset.DriveVersion<1) throw new InvalidDataException("The canonical photo lost its captured provider version.");
        var preview=await _transport.ReadAsync(asset.AccountId,asset.FileId,asset.DriveVersion,caller).ConfigureAwait(false);
        caller.ThrowIfCancellationRequested();
        var current=await _catalog.ResolveAsync(stageId,assetId,accountId,caller).ConfigureAwait(false);
        if(current.Selection!=captured.Selection) throw new CurrentCanonicalPositionChangedException();
        return current.Asset is {} found && found.AccountId==asset.AccountId && found.FileId==asset.FileId && found.DriveVersion==asset.DriveVersion?preview:null;
    }
}
