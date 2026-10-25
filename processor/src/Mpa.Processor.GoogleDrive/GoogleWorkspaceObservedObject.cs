using System.Security.Cryptography;
using System.Text;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Actual bounded immutable-object properties, never signed authority.</summary>
public sealed class DriveWorkspaceObjectMetadata
{
    public string AccountId {get;}
    public string FolderId {get;}
    public string FileId {get;}
    public long Version {get;}
    public int ByteLength {get;}
    public string MimeType {get;}
    public string? SharedDriveId {get;}
    public string ParentsSha256 {get;}
    internal DriveWorkspaceObjectMetadata(string account,string folder,string file,long version,int length,string mime,string? drive,string parents)
    {
        AccountId=account;FolderId=folder;FileId=file;Version=version;ByteLength=length;MimeType=mime;SharedDriveId=drive;
        ParentsSha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mpa-drive-parents-v1\n"+parents)));
    }
}

public sealed class DriveWorkspaceObjectObservation
{
    private readonly byte[] _bytes;
    public DriveWorkspaceObjectMetadata Metadata {get;}
    public string Sha256 {get;}
    public ReadOnlyMemory<byte> Bytes=>_bytes.ToArray();
    internal DriveWorkspaceObjectObservation(DriveWorkspaceObjectMetadata metadata,byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(metadata);ArgumentNullException.ThrowIfNull(bytes);
        if(bytes.Length!=metadata.ByteLength) throw new InvalidDataException("Actual immutable bytes lost their exact metadata length.");
        Metadata=metadata;_bytes=bytes.ToArray();Sha256=Convert.ToHexStringLower(SHA256.HashData(_bytes));
    }
}

/// <summary>Awaited bounded local checkpoints only: no recursive network, detachment
/// or retirement of borrowed resources. Metadata is delivered before body AND after
/// body, including a changed valid final revision before the transport rejects it.
/// Complete byte evidence precedes expected-digest/semantic admission and follows all
/// network I/O. A failed first checkpoint prevents the body from being downloaded.</summary>
public interface IGoogleWorkspaceObjectObserver
{
    Task RememberMetadataAsync(DriveWorkspaceObjectMetadata actual,CancellationToken cancellationToken);
    Task RememberBytesAsync(DriveWorkspaceObjectObservation actual,CancellationToken cancellationToken);
}
public interface IGoogleWorkspaceObservedObjectReader
{
    Task<DriveWorkspaceObject> ReadObservedWorkspaceObjectAsync(string folderId,string fileId,long expectedBytes,string expectedSha256,
        IGoogleWorkspaceObjectObserver observer,CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceObservedObjectReader
{
    public Task<DriveWorkspaceObject> ReadObservedWorkspaceObjectAsync(string folderId,string fileId,long expectedBytes,string expectedSha256,
        IGoogleWorkspaceObjectObserver observer,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observer);
        CheckWorkspaceReadArguments(folderId,fileId,expectedBytes,expectedSha256);
        var account=_expectedPermissionId ?? throw new InvalidOperationException("Observed metadata needs a captured actual Google account.");
        return ExecuteAsync(async token=>
        {
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);
            return await ReadWorkspaceObjectCoreAsync(credential,account,folderId,fileId,expectedBytes,expectedSha256,token,observer:observer).ConfigureAwait(false);
        },cancellationToken);
    }
}
