using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Borrow the existing account connection. The publication owner must durably
/// capture the allocated ID, selected workspace, bytes/hash and authority before create.
/// This interface has no update/delete/original-upload operation.</summary>
public interface IGoogleWorkspacePublication
{
    Task<string> AllocateWorkspaceObjectIdAsync(CancellationToken cancellationToken);
    Task<DriveWorkspaceObject> CreateWorkspaceObjectAsync(string folderId, string allocatedId, string mimeType,
        ReadOnlyMemory<byte> bytes, string sha256, CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection
{
    public Task<string> AllocateWorkspaceObjectIdAsync(CancellationToken cancellationToken)
    {
        _ = _expectedPermissionId ?? throw new InvalidOperationException("Publication requires a captured Google account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/generateIds?count=1&space=drive&type=files&fields=ids,space"), 4096, token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes); var root = document.RootElement; var ids = root.GetProperty("ids");
                if (root.GetProperty("space").GetString() != "drive" || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 1 ||
                    ids[0].ValueKind != JsonValueKind.String || !ValidIdentity(ids[0].GetString()) || ids[0].GetString() == "root")
                    throw new InvalidDataException("Drive did not allocate exactly one canonical ordinary-file identity.");
                token.ThrowIfCancellationRequested(); return ids[0].GetString()!;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { throw new InvalidDataException("Drive returned malformed generated-file identities.", error); }
        }, cancellationToken);
    }

    /// <summary>Create a bounded new metadata object at a previously journaled ID. A
    /// successful create or retry conflict is accepted only after exact download/hash and
    /// before/after parent/revision verification. A conflicting existing file is never
    /// updated. Network uncertainty propagates so resume can reuse this same ID.</summary>
    public Task<DriveWorkspaceObject> CreateWorkspaceObjectAsync(string folderId, string allocatedId, string mimeType,
        ReadOnlyMemory<byte> bytes, string sha256, CancellationToken cancellationToken) =>
        CreateWorkspaceObjectOwnedAsync(folderId,allocatedId,mimeType,bytes,sha256,null,cancellationToken);

    private Task<DriveWorkspaceObject> CreateWorkspaceObjectOwnedAsync(string folderId,string allocatedId,string mimeType,
        ReadOnlyMemory<byte> bytes,string sha256,Func<CancellationToken,Task>? admit,CancellationToken cancellationToken)
    {
        if (!ValidIdentity(folderId) || folderId == "root" || !ValidIdentity(allocatedId) || allocatedId == "root" || allocatedId == folderId ||
            mimeType is not ("application/json" or "application/gzip" or "application/octet-stream") ||
            bytes.Length is < 1 or > MaximumWorkspaceObjectBytes || sha256 is null || sha256.Length != 64 ||
            sha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Publication requires captured workspace/file identities, bounded metadata bytes/type and exact SHA-256.");
        var account = _expectedPermissionId ?? throw new InvalidOperationException("Publication requires a captured Google account identity.");
        cancellationToken.ThrowIfCancellationRequested();
        // The caller's memory is borrowed only synchronously; later mutations cannot
        // change the captured upload while authentication/network awaits are pending.
        var owned = bytes.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(owned), Convert.FromHexString(sha256)))
            throw new InvalidDataException("Publication input does not match its captured SHA-256.");
        async Task<DriveWorkspaceObject> CreateOwned(CancellationToken token)
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            await VerifyWorkspaceWritableAsync(credential, folderId, token).ConfigureAwait(false);
            await CreateWorkspaceMetadataAsync(credential, folderId, allocatedId, mimeType, owned, sha256, token).ConfigureAwait(false);
            var result = await ReadWorkspaceObjectCoreAsync(credential, account, folderId, allocatedId, owned.Length, sha256, token, mimeType).ConfigureAwait(false);
            await VerifyWorkspaceWritableAsync(credential, folderId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return result;
        }
        return admit is null ? ExecuteAsync(CreateOwned,cancellationToken)
            : ExecuteAdmittedMetadataAsync(CreateOwned,admit,cancellationToken);
    }

    private async Task VerifyWorkspaceWritableAsync(UserCredential credential, string folderId, CancellationToken token)
    {
        const string fields = "id,mimeType,trashed,capabilities(canAddChildren)";
        var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(folderId) +
            "?supportsAllDrives=true&fields=" + Uri.EscapeDataString(fields)), 16384, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            if (root.GetProperty("id").GetString() != folderId || root.GetProperty("mimeType").GetString() != "application/vnd.google-apps.folder" ||
                root.GetProperty("trashed").ValueKind != JsonValueKind.False)
                throw new DriveWorkspaceObjectChangedException();
            var writable = root.GetProperty("capabilities").GetProperty("canAddChildren").ValueKind;
            if (writable is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Workspace write capability must be boolean.");
            if (writable != JsonValueKind.True)
                throw new HttpRequestException("Google Drive does not permit adding objects to this metadata workspace.", null, HttpStatusCode.Forbidden);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Drive returned malformed metadata workspace properties.", error); }
    }

    private async Task CreateWorkspaceMetadataAsync(UserCredential credential, string folder, string file, string mime,
        byte[] bytes, string sha256, CancellationToken token)
    {
        var extension = mime == "application/json" ? ".json" : mime == "application/gzip" ? ".json.gz" : ".bin";
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { id = file, name = "mpa-" + sha256 + extension, mimeType = mime,
            parents = new[] { folder }, properties = new { mpaRole = "catalog-object-v1", mpaSha256 = sha256 } });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&supportsAllDrives=true&fields=id");
            var content = new MultipartContent("related"); request.Content = content;
            var properties = new ByteArrayContent(metadata); content.Add(properties); properties.Headers.ContentType = new("application/json") { CharSet = "UTF-8" };
            var body = new ByteArrayContent(bytes); content.Add(body); body.Headers.ContentType = new(mime);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessOwnedAsync(credential,token).ConfigureAwait(false));
            using var response = await SendOwnedAsync(request,token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await RefreshOwnedAsync(credential,token).ConfigureAwait(false)) continue;
            if (response.StatusCode == HttpStatusCode.Conflict) return;
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Workspace create failed with HTTP " +
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ". Retain its allocated identity for retry.", null, response.StatusCode);
            // Do not buffer an unbounded response or trust a successful status as proof
            // of storage. The following independent metadata/download checks prove it.
            CheckNetworkActive(token); return;
        }
        throw new InvalidOperationException("Workspace create authorization retry was exhausted.");
    }
}
