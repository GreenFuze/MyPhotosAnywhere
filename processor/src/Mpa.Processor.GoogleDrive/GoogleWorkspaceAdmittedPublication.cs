namespace Mpa.Processor.GoogleDrive;

/// <summary>Bounded immutable metadata creation only. The borrowed callback is
/// joined immediately before every physical Drive request under the same account
/// operation. It must inspect local state only, never reacquire this connection.</summary>
public interface IGoogleWorkspaceAdmittedPublication
{
    Task<DriveWorkspaceObject> CreateAdmittedWorkspaceObjectAsync(string folderId,string allocatedId,string mimeType,
        ReadOnlyMemory<byte> bytes,string sha256,Func<CancellationToken,Task> admit,CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceAdmittedPublication
{
    public Task<DriveWorkspaceObject> CreateAdmittedWorkspaceObjectAsync(string folderId,string allocatedId,string mimeType,
        ReadOnlyMemory<byte> bytes,string sha256,Func<CancellationToken,Task> admit,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admit);
        return CreateWorkspaceObjectOwnedAsync(folderId,allocatedId,mimeType,bytes,sha256,admit,cancellationToken);
    }
}
