using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Optional read-only provider capability. A stable observed Drive
/// version surrounds the private thumbnail read. No original content is fetched,
/// and neither credentials nor short-lived thumbnail URLs leave the host.</summary>
public interface IGoogleVersionedMediaPreview
{
    Task<DriveMediaPreview?> ReadVersionedMediaPreviewAsync(string fileId,long expectedVersion,CancellationToken cancellationToken);
}
public sealed class DriveMediaVersionChangedException() : IOException("This photo changed in Google Drive. Refresh its gallery before requesting another preview.");

public sealed partial class GoogleAccountConnection : IGoogleVersionedMediaPreview
{
    public Task<DriveMediaPreview?> ReadVersionedMediaPreviewAsync(string fileId,long expectedVersion,CancellationToken cancellationToken)
    {
        if(!ValidIdentity(fileId) || expectedVersion<1) throw new ArgumentException("An exact file identity and positive observed Drive version are required.");
        return ExecuteAsync<DriveMediaPreview?>(async token=>
        {
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);
            var metadataUri="https://www.googleapis.com/drive/v3/files/"+Uri.EscapeDataString(fileId)+"?supportsAllDrives=true&fields=";
            var bytes=await MediaGetAsync(credential,new(metadataUri+"id,version,trashed,thumbnailLink"),16384,token).ConfigureAwait(false);
            using var metadata=JsonDocument.Parse(bytes);var root=metadata.RootElement;
            VerifyPreviewVersion(root,fileId,expectedVersion);
            if(root.GetProperty("trashed").GetBoolean() || !root.TryGetProperty("thumbnailLink",out var link)) return null;
            if(!Uri.TryCreate(Text(link,4096),UriKind.Absolute,out var thumbnail) || thumbnail.Scheme!=Uri.UriSchemeHttps ||
                thumbnail.Host!="lh3.googleusercontent.com" || !thumbnail.IsDefaultPort || thumbnail.UserInfo.Length!=0 || thumbnail.Fragment.Length!=0)
                throw new InvalidDataException("Drive returned an unsupported thumbnail origin. No credential was sent to it.");
            var preview=await MediaGetAsync(credential,thumbnail,4*1024*1024,token).ConfigureAwait(false);
            var type=PreviewType(preview) ?? throw new InvalidDataException("Drive returned unsupported thumbnail bytes.");
            var final=await MediaGetAsync(credential,new(metadataUri+"id,version,trashed"),16384,token).ConfigureAwait(false);
            using var observed=JsonDocument.Parse(final);VerifyPreviewVersion(observed.RootElement,fileId,expectedVersion);
            if(observed.RootElement.GetProperty("trashed").GetBoolean()) throw new DriveMediaVersionChangedException();
            CheckNetworkActive(token);return new(preview,type);
        },cancellationToken);
    }
    private static void VerifyPreviewVersion(JsonElement metadata,string fileId,long expectedVersion)
    {
        if(metadata.ValueKind!=JsonValueKind.Object || !metadata.TryGetProperty("id",out var identity) || identity.ValueKind!=JsonValueKind.String || Text(identity,256)!=fileId)
            throw new InvalidDataException("Drive returned another file for the captured preview.");
        if(!metadata.TryGetProperty("version",out var value) || value.ValueKind!=JsonValueKind.String ||
            !long.TryParse(value.GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out var version) || version<1 ||
            version.ToString(CultureInfo.InvariantCulture)!=value.GetString())
            throw new InvalidDataException("Drive returned an invalid preview version.");
        if(version!=expectedVersion) throw new DriveMediaVersionChangedException();
    }
}
