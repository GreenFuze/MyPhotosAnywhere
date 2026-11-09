using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveOriginalUpload(Guid TransferId,string AccountId,string FolderId,string FileId,string Name,string MimeType,
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long Bytes,string Sha256);

/// <summary>Actual stable provider properties. A missing ContentSha256 requires
/// an independently verified bounded content read before any backup receipt.</summary>
public sealed class DriveOriginalObject
{
    public string AccountId {get;}
    public string FolderId {get;}
    public string FileId {get;}
    public string Name {get;}
    public string MimeType {get;}
    public long Bytes {get;}
    public long Version {get;}
    public string? ContentSha256 {get;}
    internal DriveOriginalObject(DriveOriginalUpload capture,string name,string mime,long version,string? hash)
    {AccountId=capture.AccountId;FolderId=capture.FolderId;FileId=capture.FileId;Name=name;MimeType=mime;Bytes=capture.Bytes;Version=version;ContentSha256=hash;}
}
public sealed class DriveOriginalUploadProgress
{
    public Guid TransferId {get;}
    public string FileId {get;}
    public long AcknowledgedBytes {get;}
    public DriveOriginalObject? CompletedObject {get;}
    internal DriveOriginalUploadProgress(DriveOriginalUpload capture,long offset,DriveOriginalObject? completed=null)
    {TransferId=capture.TransferId;FileId=capture.FileId;AcknowledgedBytes=offset;CompletedObject=completed;}
}
/// <summary>New binary originals at an already journaled identity only. No
/// replacement, delete, name-based deduplication or raw session URL capability.
/// Each bounded step retains the existing account/request deadlines.</summary>
public interface IGoogleOriginalTransfer
{
    Task<DriveOriginalUploadProgress> PrepareOriginalAsync(DriveOriginalUpload capture,Func<CancellationToken,Task> admit,CancellationToken caller);
    Task<DriveOriginalUploadProgress> UploadOriginalChunkAsync(DriveOriginalUpload capture,long offset,ReadOnlyMemory<byte> chunk,Func<CancellationToken,Task> admit,CancellationToken caller);
    Task<byte[]> ReadOriginalRangeAsync(DriveOriginalUpload capture,long version,long offset,int count,Func<CancellationToken,Task> admit,CancellationToken caller);
}

public sealed partial class GoogleAccountConnection : IGoogleOriginalTransfer
{
    public const int MaximumOriginalChunkBytes=1024*1024;
    public const long MaximumOriginalBytes=256L*1024*1024*1024;
    private readonly OriginalUploadSessionStore _originalSessions;

    public Task<DriveOriginalUploadProgress> PrepareOriginalAsync(DriveOriginalUpload capture,Func<CancellationToken,Task> admit,CancellationToken caller)
    {
        CheckOriginal(capture);ArgumentNullException.ThrowIfNull(admit);caller.ThrowIfCancellationRequested();
        return OriginalOperationAsync<DriveOriginalUploadProgress>(async token=>
        {
            // Read protected resume context before authenticating. A substituted
            // context/URL must not be used or silently overwritten by a new session.
            var session=await _originalSessions.ReadAsync(capture,token).ConfigureAwait(false);
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);
            var existing=await ObserveStableOriginalAsync(credential,capture,token).ConfigureAwait(false);
            if(existing is not null){await _originalSessions.RemoveAsync(capture,token).ConfigureAwait(false);return new(capture,capture.Bytes,existing);}
            if(session is not null)
            {
                using var status=new HttpRequestMessage(HttpMethod.Put,session.Location){Content=new ByteArrayContent([])};
                status.Content.Headers.ContentRange=new ContentRangeHeaderValue(capture.Bytes);
                using var response=await SendOriginalAsync(credential,status,token).ConfigureAwait(false);
                if(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)return await CompletedOriginalAsync(credential,capture,token).ConfigureAwait(false);
                if((int)response.StatusCode==308)
                {
                    var offset=OriginalOffset(response,capture.Bytes,session.Offset,capture.Bytes);
                    await _originalSessions.WriteAsync(capture,session with{Offset=offset},token).ConfigureAwait(false);return new(capture,offset);
                }
                if(response.StatusCode!=HttpStatusCode.NotFound)throw OriginalHttpFailure(response.StatusCode);
                // An expired session does not prove that its fixed object ID is
                // absent. Reconcile that SAME ID again before a fresh session.
                existing=await ObserveStableOriginalAsync(credential,capture,token).ConfigureAwait(false);
                if(existing is not null){await _originalSessions.RemoveAsync(capture,token).ConfigureAwait(false);return new(capture,capture.Bytes,existing);}
            }
            await VerifyWorkspaceWritableAsync(credential,capture.FolderId,token).ConfigureAwait(false);
            var metadata=JsonSerializer.SerializeToUtf8Bytes(new{id=capture.FileId,name=capture.Name,mimeType=capture.MimeType,parents=new[]{capture.FolderId}});
            using var create=new HttpRequestMessage(HttpMethod.Post,"https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&supportsAllDrives=true&fields=id"){Content=new ByteArrayContent(metadata)};
            create.Content.Headers.ContentType=new("application/json"){CharSet="utf-8"};create.Headers.Add("X-Upload-Content-Type",capture.MimeType);create.Headers.Add("X-Upload-Content-Length",capture.Bytes.ToString(CultureInfo.InvariantCulture));
            using var created=await SendOriginalAsync(credential,create,token).ConfigureAwait(false);
            if(created.StatusCode==HttpStatusCode.Conflict)return await CompletedOriginalAsync(credential,capture,token).ConfigureAwait(false);
            if(created.StatusCode is not(HttpStatusCode.OK or HttpStatusCode.Created))throw OriginalHttpFailure(created.StatusCode);
            var location=OriginalUploadSessionStore.ValidateLocation(created.Headers.Location?.OriginalString);
            await _originalSessions.WriteAsync(capture,new(location,0),token).ConfigureAwait(false);return new(capture,0);
        },admit,caller);
    }
    public Task<DriveOriginalUploadProgress> UploadOriginalChunkAsync(DriveOriginalUpload capture,long offset,ReadOnlyMemory<byte> chunk,Func<CancellationToken,Task> admit,CancellationToken caller)
    {
        CheckOriginal(capture);ArgumentNullException.ThrowIfNull(admit);
        if(offset<0||offset>=capture.Bytes||chunk.Length is <1 or >MaximumOriginalChunkBytes||chunk.Length>capture.Bytes-offset||offset+chunk.Length!=capture.Bytes&&chunk.Length%(256*1024)!=0)throw new ArgumentException("Invalid bounded original upload chunk/range.");
        caller.ThrowIfCancellationRequested();return UploadOwnedOriginalChunkAsync(capture,offset,chunk.ToArray(),admit,caller);
    }
    private async Task<DriveOriginalUploadProgress> UploadOwnedOriginalChunkAsync(DriveOriginalUpload capture,long offset,byte[] bytes,Func<CancellationToken,Task> admit,CancellationToken caller)
    {
        try
        {
            return await OriginalOperationAsync<DriveOriginalUploadProgress>(async token=>
            {
                var session=await _originalSessions.ReadAsync(capture,token).ConfigureAwait(false)??throw new InvalidOperationException("Prepare the captured original before sending its bounded chunk.");
                if(session.Offset!=offset)throw new InvalidOperationException("Reconcile the actual server offset before resending original bytes.");
                var credential=await MediaCredentialAsync(token).ConfigureAwait(false);await VerifyWorkspaceWritableAsync(credential,capture.FolderId,token).ConfigureAwait(false);
                using var request=new HttpRequestMessage(HttpMethod.Put,session.Location){Content=new ByteArrayContent(bytes)};request.Content.Headers.ContentType=new(capture.MimeType);request.Content.Headers.ContentRange=new(offset,offset+bytes.Length-1,capture.Bytes);
                using var response=await SendOriginalAsync(credential,request,token).ConfigureAwait(false);
                if(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)return await CompletedOriginalAsync(credential,capture,token).ConfigureAwait(false);
                if((int)response.StatusCode!=308)throw OriginalHttpFailure(response.StatusCode);
                var actual=OriginalOffset(response,capture.Bytes,offset,offset+bytes.Length);
                if(actual==offset)throw new IOException("Drive acknowledged no new original bytes; retain the same session and retry after reconciliation.");
                await _originalSessions.WriteAsync(capture,session with{Offset=actual},token).ConfigureAwait(false);return new(capture,actual);
            },admit,caller).ConfigureAwait(false);
        }
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    public Task<byte[]> ReadOriginalRangeAsync(DriveOriginalUpload capture,long version,long offset,int count,Func<CancellationToken,Task> admit,CancellationToken caller)
    {
        CheckOriginal(capture);ArgumentNullException.ThrowIfNull(admit);
        if(version<1||offset<0||offset>=capture.Bytes||count is <1 or >MaximumOriginalChunkBytes||count>capture.Bytes-offset)throw new ArgumentException("Invalid bounded original verification range.");
        caller.ThrowIfCancellationRequested();return OriginalOperationAsync(async token=>
        {
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);var before=await ObserveStableOriginalAsync(credential,capture,token).ConfigureAwait(false);
            if(before is null||before.Version!=version)throw new InvalidDataException("Original changed before independent content verification.");
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://www.googleapis.com/drive/v3/files/"+Uri.EscapeDataString(capture.FileId)+"?alt=media&supportsAllDrives=true");request.Headers.Range=new(offset,offset+count-1);
            using var response=await SendOriginalAsync(credential,request,token).ConfigureAwait(false);var range=response.Content.Headers.ContentRange;
            if(response.StatusCode!=HttpStatusCode.PartialContent||range is null||range.Unit!="bytes"||range.From!=offset||range.To!=offset+count-1||range.Length!=capture.Bytes||response.Content.Headers.ContentLength is{ } length&&length!=count)throw new InvalidDataException("Drive did not return the exact bounded original range.");
            var bytes=await ReadOriginalBodyAsync(response,count,token).ConfigureAwait(false);var after=await ObserveStableOriginalAsync(credential,capture,token).ConfigureAwait(false);
            if(after is null||after.Version!=version)throw new InvalidDataException("Original changed during independent content verification.");return bytes;
        },admit,caller);
    }
    // Reuses only the existing lexical per-Drive-request gateway, not metadata
    // authority. This callback belongs to the actual ORIGINAL action owner.
    private Task<T> OriginalOperationAsync<T>(Func<CancellationToken,Task<T>> operation,Func<CancellationToken,Task> admit,CancellationToken caller)=>
        ExecuteAdmittedMetadataAsync(async token=>{var result=await operation(token).ConfigureAwait(false);caller.ThrowIfCancellationRequested();return result;},
            async token=>{caller.ThrowIfCancellationRequested();await admit(token).ConfigureAwait(false);caller.ThrowIfCancellationRequested();},caller);
    private async Task<HttpResponseMessage> SendOriginalAsync(UserCredential credential,HttpRequestMessage request,CancellationToken token)
    {
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await AccessOwnedAsync(credential,token).ConfigureAwait(false));
        try {return await SendOwnedAsync(request,token).ConfigureAwait(false);}
        catch(HttpRequestException error){throw new HttpRequestException("Original Drive request failed; retain its assigned identity and protected session.",null,error.StatusCode);}
    }
    private async Task<DriveOriginalUploadProgress> CompletedOriginalAsync(UserCredential credential,DriveOriginalUpload capture,CancellationToken token)
    {
        var actual=await ObserveStableOriginalAsync(credential,capture,token).ConfigureAwait(false)??throw new InvalidDataException("Upload completion was not independently observed at its assigned identity.");
        await _originalSessions.RemoveAsync(capture,token).ConfigureAwait(false);return new(capture,capture.Bytes,actual);
    }
    private async Task<DriveOriginalObject?> ObserveStableOriginalAsync(UserCredential credential,DriveOriginalUpload capture,CancellationToken token)
    {
        var before=await ReadOriginalMetadataAsync(credential,capture,token).ConfigureAwait(false);if(before is null)return null;
        var after=await ReadOriginalMetadataAsync(credential,capture,token).ConfigureAwait(false);
        if(after is null||before.Version!=after.Version||before.Name!=after.Name||before.MimeType!=after.MimeType||before.ContentSha256!=after.ContentSha256)throw new InvalidDataException("Original provider properties changed while being observed.");return after;
    }
    private async Task<DriveOriginalObject?> ReadOriginalMetadataAsync(UserCredential credential,DriveOriginalUpload capture,CancellationToken token)
    {
        const string fields="id,name,mimeType,size,version,parents,trashed,sha256Checksum,capabilities(canDownload)";
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://www.googleapis.com/drive/v3/files/"+Uri.EscapeDataString(capture.FileId)+"?supportsAllDrives=true&fields="+Uri.EscapeDataString(fields));
        using var response=await SendOriginalAsync(credential,request,token).ConfigureAwait(false);if(response.StatusCode==HttpStatusCode.NotFound)return null;if(!response.IsSuccessStatusCode)throw OriginalHttpFailure(response.StatusCode);
        var body=await ReadOriginalLimitedAsync(response,16384,token).ConfigureAwait(false);using var document=JsonDocument.Parse(body);var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=root.EnumerateObject().Count())throw new InvalidDataException("Drive returned ambiguous original properties.");
        var parents=root.GetProperty("parents");var mime=root.GetProperty("mimeType").GetString();var name=root.GetProperty("name").GetString();
        if(root.GetProperty("id").GetString()!=capture.FileId||parents.ValueKind!=JsonValueKind.Array||parents.GetArrayLength()!=1||parents[0].GetString()!=capture.FolderId||root.GetProperty("trashed").ValueKind!=JsonValueKind.False||
            !OriginalMime(mime)||name is not{Length:>0 and <=4096}||OriginalDecimal(root,"size")!=capture.Bytes||root.GetProperty("capabilities").GetProperty("canDownload").ValueKind!=JsonValueKind.True)throw new InvalidDataException("The assigned original differs from its captured scope, type, bytes or readable state.");
        var version=OriginalDecimal(root,"version");if(version<1)throw new InvalidDataException("Original version is invalid.");
        var hash=root.TryGetProperty("sha256Checksum",out var checksum)?checksum.GetString():null;
        if(hash is not null&&(!OriginalHash(hash)||hash!=capture.Sha256))throw new InvalidDataException("The assigned original does not match its captured SHA256.");
        return new(capture,name,mime!,version,hash);
    }
    private void CheckOriginal(DriveOriginalUpload capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if(capture.TransferId==Guid.Empty||!ValidIdentity(capture.AccountId)||capture.AccountId!=_expectedPermissionId||!ValidIdentity(capture.FolderId)||capture.FolderId=="root"||!ValidIdentity(capture.FileId)||capture.FileId=="root"||capture.FileId==capture.FolderId||
            capture.Bytes is <1 or >MaximumOriginalBytes||!OriginalHash(capture.Sha256)||!OriginalMime(capture.MimeType)||capture.Name is not{Length:>0 and <=255}||capture.Name is "." or ".."||capture.Name.Any(c=>char.IsControl(c)||c is '/' or '\\'))throw new ArgumentException("A bounded captured binary original, account, destination and previously assigned identity are required.");
        _=new UTF8Encoding(false,true).GetByteCount(capture.Name);
    }
    private static bool OriginalHash(string? hash)=>hash is{Length:64}&&hash.All(c=>c is >='0' and <='9' or >='a' and <='f');
    private static bool OriginalMime(string? mime)=>mime is{Length:>0 and <=128}&&!mime.StartsWith("application/vnd.google-apps.",StringComparison.Ordinal)&&mime.All(c=>c is >= '!' and <= '~')&&MediaTypeHeaderValue.TryParse(mime,out var parsed)&&parsed.Parameters.Count==0&&parsed.MediaType==mime;
    private static long OriginalDecimal(JsonElement root,string name)
    {var raw=root.GetProperty(name).GetString();if(!long.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value<0||value.ToString(CultureInfo.InvariantCulture)!=raw)throw new InvalidDataException("Invalid original decimal property.");return value;}
    private static long OriginalOffset(HttpResponseMessage response,long bytes,long minimum,long maximum)
    {
        long offset=0;
        if(response.Headers.TryGetValues("Range",out var values))
        {
            var all=values.Take(2).ToArray();if(all.Length!=1||!all[0].StartsWith("bytes=0-",StringComparison.Ordinal)||!long.TryParse(all[0].AsSpan(8),NumberStyles.None,CultureInfo.InvariantCulture,out var last)||last<0||last>=bytes)throw new InvalidDataException("Drive returned an invalid original received range.");offset=last+1;
        }
        if(offset<minimum||offset>maximum||offset>=bytes)throw new InvalidDataException("Drive original progress rolled back, exceeded sent bytes or claimed completion without a completed response.");return offset;
    }
    private static HttpRequestException OriginalHttpFailure(HttpStatusCode status)=>new("Original Drive operation failed with HTTP "+((int)status).ToString(CultureInfo.InvariantCulture)+". Retain the assigned identity and protected session for reconciliation.",null,status);
    private static async Task<byte[]> ReadOriginalBodyAsync(HttpResponseMessage response,int count,CancellationToken token)
    {
        await using var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);var body=new byte[count];await input.ReadExactlyAsync(body,token).ConfigureAwait(false);var extra=new byte[1];if(await input.ReadAsync(extra,token).ConfigureAwait(false)!=0)throw new InvalidDataException("Original range exceeded its exact byte count.");return body;
    }
    private static async Task<byte[]> ReadOriginalLimitedAsync(HttpResponseMessage response,int maximum,CancellationToken token)
    {
        if(response.Content.Headers.ContentLength>maximum)throw new InvalidDataException("Original metadata response exceeds its bound.");
        await using var input=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);using var output=new MemoryStream();var buffer=new byte[4096];int count;
        while((count=await input.ReadAsync(buffer,token).ConfigureAwait(false))!=0){if(output.Length+count>maximum)throw new InvalidDataException("Original metadata response exceeds its bound.");output.Write(buffer,0,count);}return output.ToArray();
    }
}
