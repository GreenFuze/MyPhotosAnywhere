using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Actual bounded JSON bytes at an independently captured workspace/file ID.
/// No listing, changes feed, equal-copy search, signature authority, freshness after
/// return, activation or write is granted. The semantic owner must verify its Root pin
/// and retain/fence the observed head before admitting a canonical generation.</summary>
public interface IGoogleWorkspaceExactJsonReader
{
    Task<DriveWorkspaceObject> ReadWorkspaceJsonByIdAsync(string folderId,string fileId,CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceExactJsonReader
{
    public Task<DriveWorkspaceObject> ReadWorkspaceJsonByIdAsync(string folderId,string fileId,CancellationToken cancellationToken)
    {
        if (!ValidIdentity(folderId) || folderId=="root" || !ValidIdentity(fileId) || fileId is "root" || fileId==folderId)
            throw new ArgumentException("An independently captured concrete workspace and distinct JSON file identity are required.");
        var account=_expectedPermissionId ?? throw new InvalidOperationException("Exact JSON reading requires a captured actual Google identity.");
        return ExecuteAsync(async token=>
        {
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);
            var raw=await MediaGetAsync(credential,new("https://www.googleapis.com/drive/v3/files/"+Uri.EscapeDataString(fileId)+
                "?supportsAllDrives=true&fields="+Uri.EscapeDataString(WorkspaceObjectFields)),32768,token).ConfigureAwait(false);
            WorkspaceMetadata before;long length;
            try
            {
                using var document=JsonDocument.Parse(raw);var root=document.RootElement;
                length=WorkspacePositive(root,"size");
                if (length>MaximumWorkspaceDocumentBytes) throw new InvalidDataException("Exact current JSON exceeds its metadata byte bound.");
                before=ParseWorkspaceMetadata(root,folderId,fileId,length);
                if (before.MimeType!="application/json") throw new DriveWorkspaceObjectChangedException();
            }
            catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new InvalidDataException("Google returned malformed exact JSON metadata.",error); }
            var body=await MediaGetAsync(credential,new("https://www.googleapis.com/drive/v3/files/"+Uri.EscapeDataString(fileId)+
                "?supportsAllDrives=true&alt=media"),checked((int)length),token).ConfigureAwait(false);
            if (body.LongLength!=length) throw new DriveWorkspaceObjectChangedException();
            var after=await ReadWorkspaceMetadataAsync(credential,folderId,fileId,length,token).ConfigureAwait(false);
            if (after!=before) throw new DriveWorkspaceObjectChangedException();
            token.ThrowIfCancellationRequested();return new DriveWorkspaceObject(account,fileId,folderId,after.Version,body);
        },cancellationToken);
    }
}
