using Mpa.Processor.Credentials;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Bounded read-only Google operations. Credential refresh remains inside the owned
/// connection; tokens and short-lived download URLs never escape through this interface.</summary>
public interface IGoogleLibraryConnection : IAsyncDisposable
{
    Task<DriveMediaPage> ReadMediaPageAsync(string? pageToken, CancellationToken cancellationToken);
    Task<DriveMediaPreview?> ReadMediaPreviewAsync(string fileId, CancellationToken cancellationToken);
    Task<DriveFolder> ReadFolderAsync(string folderId, CancellationToken cancellationToken);
    Task<DriveFolderPage> ReadFolderPageAsync(string parentId, string? pageToken, CancellationToken cancellationToken);
    Task<DriveSourcePage> ReadSourcePageAsync(string parentId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken);
    Task<DriveSourceItem> ReadSourceItemAsync(string parentId, string? sharedDriveId, string fileId, CancellationToken cancellationToken);
}

public interface IGoogleLibraryConnectionFactory
{
    IGoogleLibraryConnection Create(Guid connectionId, string permissionId);
}

public sealed class GoogleLibraryConnectionFactory(DesktopOAuthConfiguration configuration, ICredentialVault vault) : IGoogleLibraryConnectionFactory
{
    public IGoogleLibraryConnection Create(Guid connectionId, string permissionId) => new GoogleAccountConnection(configuration, vault, connectionId, permissionId);
}
