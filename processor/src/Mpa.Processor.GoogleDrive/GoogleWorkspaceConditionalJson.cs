using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Owned actual JSON observation and strong provider condition. The private
/// connection identity cannot be reconstructed by callers from an ETag/file name.
/// This grants no Root authority, permission to publish, original write or activation.
/// Semantic owners must persist their exact signed intent before submitting an update.</summary>
public sealed class DriveWorkspaceConditionalJson
{
    internal object Owner { get; }
    internal string Parents { get; }
    private readonly byte[] _bytes;
    public string AccountId { get; }
    public string FolderId { get; }
    public string FileId { get; }
    public long Version { get; }
    public string EntityTag { get; }
    public string? SharedDriveId { get; }
    public bool CanEdit { get; }
    public string Sha256 { get; }
    public int ByteLength=>_bytes.Length;
    public ReadOnlyMemory<byte> Bytes=>_bytes.ToArray();

    internal DriveWorkspaceConditionalJson(object owner,string account,string folder,string file,long version,string tag,
        string parents,string? drive,bool canEdit,byte[] bytes)
    {
        Owner=owner;AccountId=account;FolderId=folder;FileId=file;Version=version;EntityTag=tag;Parents=parents;
        SharedDriveId=drive;CanEdit=canEdit;_bytes=bytes.ToArray();Sha256=Convert.ToHexStringLower(SHA256.HashData(_bytes));
    }
}

public interface IGoogleWorkspaceConditionalJson
{
    Task<DriveWorkspaceConditionalJson> CaptureConditionalWorkspaceJsonAsync(string folderId,string fileId,CancellationToken cancellationToken);
    Task<DriveWorkspaceConditionalJson> ReplaceConditionalWorkspaceJsonAsync(DriveWorkspaceConditionalJson expected,
        ReadOnlyMemory<byte> bytes,string sha256,CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceConditionalJson
{
    private readonly object _jsonConditions=new();
    // No v3 conditional fallback: the isolated live probe proved that v3 ignored
    // If-Match. Numeric versions are observations, never provider entity tags.
    private const string ConditionalJsonFields="id,etag,version,mimeType,fileSize,parents(id),driveId,labels(trashed),capabilities(canDownload,canEdit)";

    public Task<DriveWorkspaceConditionalJson> CaptureConditionalWorkspaceJsonAsync(string folderId,string fileId,CancellationToken cancellationToken)
    {
        CheckConditionalIdentities(folderId,fileId);
        var account=_expectedPermissionId ?? throw new InvalidOperationException("Conditional JSON needs a captured actual Google account.");
        return ExecuteAsync(async token=>
        {
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);
            return await CaptureConditionalJsonCoreAsync(credential,account,folderId,fileId,token).ConfigureAwait(false);
        },cancellationToken);
    }

    public Task<DriveWorkspaceConditionalJson> ReplaceConditionalWorkspaceJsonAsync(DriveWorkspaceConditionalJson expected,
        ReadOnlyMemory<byte> bytes,string sha256,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(expected.Owner,_jsonConditions) || expected.AccountId!=_expectedPermissionId)
            throw new ArgumentException("Use an actual capture from this same owned Google connection.",nameof(expected));
        if (!expected.CanEdit) throw new HttpRequestException("The captured JSON cannot be edited.",null,HttpStatusCode.Forbidden);
        if (bytes.Length is <1 or >MaximumWorkspaceDocumentBytes || sha256 is not {Length:64} ||
            sha256.Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Conditional JSON requires bounded owned bytes and an exact SHA-256.",nameof(bytes));
        var owned=bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(owned))!=sha256) throw new InvalidDataException("Conditional JSON input changed its captured digest.");
        if (sha256==expected.Sha256) throw new ArgumentException("Reconcile an unchanged observation without submitting another write.",nameof(bytes));
        try
        {
            using var document=JsonDocument.Parse(owned);
            if (document.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Conditional metadata must be a JSON object.");
        }
        catch(JsonException error) { throw new InvalidDataException("Conditional metadata is not valid bounded JSON.",error); }
        return ExecuteAsync(async token=>
        {
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);
            var actual=await ReadConditionalJsonMetadataAsync(credential,expected.FolderId,expected.FileId,token).ConfigureAwait(false);
            if (actual.Version<expected.Version || actual.Tag!=expected.EntityTag || actual.Bytes!=expected.ByteLength ||
                actual.Parents!=expected.Parents || actual.Drive!=expected.SharedDriveId)
                throw ConditionalJsonConflict();
            if (!actual.CanEdit) throw new HttpRequestException("Google no longer permits editing the selected JSON.",null,HttpStatusCode.Forbidden);
            await PutConditionalJsonAsync(credential,expected,owned,token).ConfigureAwait(false);
            // A status is never a receipt. An uncertain response propagates with no
            // allocation/replacement. The semantic owner recovers its exact pending
            // bytes at this same ID before it considers any fresh condition.
            var observed=await CaptureConditionalJsonCoreAsync(credential,expected.AccountId,expected.FolderId,expected.FileId,token).ConfigureAwait(false);
            if (observed.Version<=expected.Version || observed.EntityTag==expected.EntityTag || observed.Sha256!=sha256 ||
                observed.Parents!=expected.Parents || observed.SharedDriveId!=expected.SharedDriveId)
                throw new DriveWorkspaceObjectChangedException();
            token.ThrowIfCancellationRequested();return observed;
        },cancellationToken);
    }

    private async Task<DriveWorkspaceConditionalJson> CaptureConditionalJsonCoreAsync(UserCredential credential,string account,string folder,string file,CancellationToken token)
    {
        var before=await ReadConditionalJsonMetadataAsync(credential,folder,file,token).ConfigureAwait(false);
        // No checksum-bearing v3 metadata request inside this exact v2 window.
        // JSON MIME/scope/size are checked before any body. No original can qualify.
        var body=await MediaGetAsync(credential,new("https://www.googleapis.com/drive/v3/files/"+Uri.EscapeDataString(file)+
            "?supportsAllDrives=true&alt=media"),checked((int)before.Bytes),token).ConfigureAwait(false);
        if (body.LongLength!=before.Bytes) throw new DriveWorkspaceObjectChangedException();
        var after=await ReadConditionalJsonMetadataAsync(credential,folder,file,token).ConfigureAwait(false);
        if (before!=after) throw new DriveWorkspaceObjectChangedException();
        token.ThrowIfCancellationRequested();
        return new(_jsonConditions,account,folder,file,after.Version,after.Tag,after.Parents,after.Drive,after.CanEdit,body);
    }

    private async Task<ConditionalJsonMetadata> ReadConditionalJsonMetadataAsync(UserCredential credential,string folder,string file,CancellationToken token)
    {
        var body=await MediaGetAsync(credential,new("https://www.googleapis.com/drive/v2/files/"+Uri.EscapeDataString(file)+
            "?supportsAllDrives=true&fields="+Uri.EscapeDataString(ConditionalJsonFields)),32768,token).ConfigureAwait(false);
        try
        {
            using var document=JsonDocument.Parse(body);var root=document.RootElement;
            if (root.GetProperty("id").GetString()!=file || root.GetProperty("mimeType").GetString()!="application/json" ||
                root.GetProperty("labels").GetProperty("trashed").ValueKind!=JsonValueKind.False) throw new DriveWorkspaceObjectChangedException();
            var length=WorkspacePositive(root,"fileSize");var version=WorkspacePositive(root,"version");
            if (length>MaximumWorkspaceDocumentBytes) throw new InvalidDataException("Conditional JSON exceeds its wire bound.");
            var rawTag=root.GetProperty("etag").GetString();
            if (rawTag is not {Length:>0 and <=1024} || !EntityTagHeaderValue.TryParse(rawTag,out var tag) || tag.IsWeak || tag.Tag=="*" || tag.ToString()!=rawTag)
                throw new InvalidDataException("Conditional JSON requires an actual canonical strong entity tag.");
            var parents=root.GetProperty("parents");var ids=new SortedSet<string>(StringComparer.Ordinal);
            if (parents.ValueKind!=JsonValueKind.Array || parents.GetArrayLength() is <1 or >16) throw new DriveWorkspaceObjectChangedException();
            foreach(var parent in parents.EnumerateArray())
            {
                var id=parent.GetProperty("id").GetString();
                if (!ValidIdentity(id) || !ids.Add(id!)) throw new InvalidDataException("Conditional JSON has invalid actual parent identities.");
            }
            if (!ids.Contains(folder)) throw new DriveWorkspaceObjectChangedException();
            var capabilities=root.GetProperty("capabilities");
            if (capabilities.GetProperty("canDownload").ValueKind!=JsonValueKind.True) throw new DriveWorkspaceDownloadDeniedException();
            var edit=capabilities.GetProperty("canEdit").ValueKind;
            if (edit is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Actual edit capability must be boolean.");
            string? drive=null;
            if (root.TryGetProperty("driveId",out var driveId))
            { drive=driveId.GetString();if (!ValidIdentity(drive) || drive=="root") throw new InvalidDataException("Conditional JSON has an invalid actual Drive corpus."); }
            return new(version,length,rawTag,string.Join('\n',ids),drive,edit==JsonValueKind.True);
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Google returned malformed conditional JSON metadata.",error); }
    }

    private async Task PutConditionalJsonAsync(UserCredential credential,DriveWorkspaceConditionalJson expected,byte[] bytes,CancellationToken token)
    {
        for(var attempt=0;attempt<2;attempt++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Put,"https://www.googleapis.com/upload/drive/v2/files/"+
                Uri.EscapeDataString(expected.FileId)+"?uploadType=media&supportsAllDrives=true&fields=id");
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(expected.EntityTag));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await AccessOwnedAsync(credential,token).ConfigureAwait(false));
            request.Content=new ByteArrayContent(bytes);request.Content.Headers.ContentType=new("application/json");
            using var response=await SendOwnedAsync(request,token).ConfigureAwait(false);
            if (response.StatusCode==HttpStatusCode.Unauthorized && attempt==0 && await RefreshOwnedAsync(credential,token).ConfigureAwait(false)) continue;
            if (response.StatusCode==HttpStatusCode.PreconditionFailed) throw ConditionalJsonConflict();
            if (response.StatusCode!=HttpStatusCode.OK) throw new HttpRequestException("Conditional JSON update returned HTTP "+
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)+". Retain its exact pending identity and bytes.",null,response.StatusCode);
            CheckNetworkActive(token);return;
        }
        throw new InvalidOperationException("Conditional JSON authorization retry was exhausted.");
    }
    private static HttpRequestException ConditionalJsonConflict()=>new("The actual JSON condition changed. Reconcile the same file before another update.",null,HttpStatusCode.PreconditionFailed);
    private static void CheckConditionalIdentities(string folder,string file)
    {
        if (!ValidIdentity(folder) || folder=="root" || !ValidIdentity(file) || file=="root" || file==folder)
            throw new ArgumentException("A captured concrete workspace and distinct metadata file are required.");
    }
    private sealed record ConditionalJsonMetadata(long Version,long Bytes,string Tag,string Parents,string? Drive,bool CanEdit);
}
