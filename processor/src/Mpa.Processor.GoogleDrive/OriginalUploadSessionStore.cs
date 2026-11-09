using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Credentials;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Protected per-connection resume material only. Never serialize this
/// owner or expose its session location to a browser, journal or diagnostic.</summary>
internal sealed class OriginalUploadSessionStore(ICredentialVault vault,Guid connection)
{
    internal sealed record Session(Uri Location,long Offset)
    {public override string ToString()=>"[protected original-upload session]";}
    private static string Context(DriveOriginalUpload binding)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-original-upload-context-1",transferId=binding.TransferId.ToString("D"),accountId=binding.AccountId,folderId=binding.FolderId,fileId=binding.FileId,name=binding.Name,mimeType=binding.MimeType,bytes=binding.Bytes.ToString(CultureInfo.InvariantCulture),sha256=binding.Sha256})));
    private static string Key(Guid connection,string file)=>"google-original-v1:"+connection.ToString("N")+":"+file;
    internal async Task<Session?> ReadAsync(DriveOriginalUpload binding,CancellationToken token)
    {
        using var secret=await vault.ReadAsync(Key(connection,binding.FileId),token).ConfigureAwait(false);if(secret is null)return null;
        if(secret.Bytes.Length>16384)throw new InvalidDataException("Protected upload session exceeds its bound.");
        using var document=JsonDocument.Parse(secret.Bytes);var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=4||root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=4||
            root.GetProperty("format").GetString()!="mpa-original-upload-session-1"||root.GetProperty("context").GetString()!=Context(binding))throw new InvalidDataException("Protected upload session differs from its original captured transfer.");
        var raw=root.GetProperty("offset").GetString();
        if(!long.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out var offset)||offset<0||offset>binding.Bytes||offset.ToString(CultureInfo.InvariantCulture)!=raw)throw new InvalidDataException("Protected upload offset is invalid.");
        return new(ValidateLocation(root.GetProperty("location").GetString()),offset);
    }
    internal async Task WriteAsync(DriveOriginalUpload binding,Session session,CancellationToken token)
    {
        _=ValidateLocation(session.Location.AbsoluteUri);if(session.Offset<0||session.Offset>binding.Bytes)throw new InvalidDataException("Upload offset exceeds its captured original.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {format="mpa-original-upload-session-1",context=Context(binding),location=session.Location.AbsoluteUri,offset=session.Offset.ToString(CultureInfo.InvariantCulture)});
        try {await vault.WriteAsync(Key(connection,binding.FileId),bytes,token).ConfigureAwait(false);}
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    internal Task RemoveAsync(DriveOriginalUpload binding,CancellationToken token)=>vault.DeleteAsync(Key(connection,binding.FileId),token);
    internal static Uri ValidateLocation(string? value)
    {
        if(value is not{Length:>0 and <=4096}||value.Any(char.IsControl)||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="www.googleapis.com"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!="/upload/drive/v3/files")throw new InvalidDataException("Drive returned an unsupported upload-session origin or path; no credential was sent to it.");
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var field in uri.Query.TrimStart('?').Split('&'))
        {
            var pair=field.Split('=',2);if(pair.Length!=2||pair[0] is not("uploadType" or "upload_id" or "supportsAllDrives" or "fields")||!fields.TryAdd(pair[0],Uri.UnescapeDataString(pair[1])))throw new InvalidDataException("Unsupported upload-session parameters.");
        }
        if(!fields.TryGetValue("uploadType",out var type)||type!="resumable"||!fields.TryGetValue("upload_id",out var id)||id.Length is <1 or >2048||id.Any(char.IsControl))throw new InvalidDataException("Drive upload session lacks its bounded resumable identity.");
        return uri;
    }
}
