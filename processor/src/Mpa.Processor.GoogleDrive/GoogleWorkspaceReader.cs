using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveWorkspaceObject(string AccountId, string FileId, string FolderId, long DriveVersion, ReadOnlyMemory<byte> Bytes)
{
    // Actual read metadata, never policy authority. Keep the existing five-field
    // constructor/deconstruction for captured work and synthetic transport callers.
    public string MimeType {get;init;}="application/json";
    public string? SharedDriveId {get;init;}
}
public sealed class DriveWorkspaceObjectChangedException() : IOException("This metadata workspace object no longer matches its captured reference.");
public sealed class DriveWorkspaceDownloadDeniedException() : IOException("Google Drive does not permit downloading this metadata workspace object.");

public sealed partial class GoogleAccountConnection
{
    public const int MaximumWorkspaceObjectBytes = 4 * 1024 * 1024;
    private const string WorkspaceObjectFields = "id,mimeType,version,size,parents,driveId,trashed,capabilities(canDownload)";

    /// <summary>Reads one captured immutable metadata object only. Actual workspace membership,
    /// byte length/digest and before/after Drive revision are checked inside the account's
    /// existing serialized credential owner. Never follows a shortcut, exports a Google Doc,
    /// returns a credential/URL, writes a source or activates a catalog.</summary>
    public Task<DriveWorkspaceObject> ReadWorkspaceObjectAsync(string folderId, string fileId, long expectedBytes, string expectedSha256, CancellationToken cancellationToken)
    {
        CheckWorkspaceReadArguments(folderId,fileId,expectedBytes,expectedSha256);
        var account = _expectedPermissionId ?? throw new InvalidOperationException("Metadata workspace reading requires a captured Google account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            return await ReadWorkspaceObjectCoreAsync(credential, account, folderId, fileId, expectedBytes, expectedSha256, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static void CheckWorkspaceReadArguments(string folderId,string fileId,long expectedBytes,string expectedSha256)
    {
        if (!ValidIdentity(folderId) || folderId == "root" || !ValidIdentity(fileId) || fileId == folderId ||
            expectedBytes is < 1 or > MaximumWorkspaceObjectBytes || expectedSha256 is null || expectedSha256.Length != 64 ||
            expectedSha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A captured metadata workspace/file, bounded exact length and lowercase SHA-256 are required.");
    }

    private async Task<DriveWorkspaceObject> ReadWorkspaceObjectCoreAsync(UserCredential credential, string account, string folderId,
        string fileId, long expectedBytes, string expectedSha256, CancellationToken token, string? expectedMimeType = null,
        IGoogleWorkspaceObjectObserver? observer=null)
    {
        var before = await ReadWorkspaceMetadataAsync(credential, folderId, fileId, expectedBytes, token).ConfigureAwait(false);
        if (expectedMimeType is not null && before.MimeType != expectedMimeType) throw new DriveWorkspaceObjectChangedException();
        DriveWorkspaceObjectMetadata Actual(WorkspaceMetadata metadata)=>new(account,folderId,fileId,metadata.Version,checked((int)expectedBytes),
            metadata.MimeType,metadata.SharedDriveId,metadata.Parents);
        CheckNetworkActive(token);
        if(observer is not null) await observer.RememberMetadataAsync(Actual(before),token).ConfigureAwait(false);
        CheckNetworkActive(token);
        var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(fileId) +
            "?supportsAllDrives=true&alt=media"), (int)expectedBytes, token).ConfigureAwait(false);
        var matches=CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),Convert.FromHexString(expectedSha256));
        if (bytes.LongLength != expectedBytes || observer is null && !matches)
            throw new InvalidDataException("Metadata workspace object is truncated or does not match its immutable SHA-256 reference.");
        var after = await ReadWorkspaceMetadataAsync(credential, folderId, fileId, expectedBytes, token).ConfigureAwait(false);
        CheckNetworkActive(token);
        if(observer is not null) await observer.RememberMetadataAsync(Actual(after),token).ConfigureAwait(false);
        CheckNetworkActive(token);
        if (before != after) throw new DriveWorkspaceObjectChangedException();
        if(observer is not null) await observer.RememberBytesAsync(new(Actual(after),bytes),token).ConfigureAwait(false);
        CheckNetworkActive(token);
        if(!matches) throw new InvalidDataException("Metadata workspace object does not match its immutable SHA-256 reference.");
        return new DriveWorkspaceObject(account, fileId, folderId, after.Version, bytes) {MimeType=after.MimeType,SharedDriveId=after.SharedDriveId};
    }

    private async Task<WorkspaceMetadata> ReadWorkspaceMetadataAsync(UserCredential credential, string folderId, string fileId, long expectedBytes, CancellationToken token)
    {
        var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(fileId) +
            "?supportsAllDrives=true&fields=" + Uri.EscapeDataString(WorkspaceObjectFields)), 32768, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            return ParseWorkspaceMetadata(root, folderId, fileId, expectedBytes);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Google returned malformed metadata workspace file properties.", error); }
    }
    private static WorkspaceMetadata ParseWorkspaceMetadata(JsonElement root, string folderId, string fileId, long expectedBytes)
    {
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("id").GetString() != fileId)
            throw new InvalidDataException("Google returned a different metadata workspace identity.");
        var parents = root.GetProperty("parents");
        if (parents.ValueKind != JsonValueKind.Array || parents.GetArrayLength() is < 1 or > 16 ||
            root.GetProperty("trashed").ValueKind != JsonValueKind.False)
            throw new DriveWorkspaceObjectChangedException();
        var memberships = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parent in parents.EnumerateArray())
        {
            if (parent.ValueKind != JsonValueKind.String || !ValidIdentity(parent.GetString()) || !memberships.Add(parent.GetString()!))
                throw new InvalidDataException("Invalid actual Drive metadata workspace parent memberships.");
        }
        if (!memberships.Contains(folderId)) throw new DriveWorkspaceObjectChangedException();
        string? drive=null;
        if(root.TryGetProperty("driveId",out var corpus))
        {
            if(corpus.ValueKind!=JsonValueKind.String || !ValidIdentity(corpus.GetString()) || corpus.GetString()=="root")
                throw new InvalidDataException("Metadata workspace corpus must be a concrete opaque Drive identity when present.");
            drive=corpus.GetString();
        }
        var mime = root.GetProperty("mimeType").GetString();
        if (mime is not ("application/json" or "application/gzip" or "application/octet-stream"))
            throw new InvalidDataException("A metadata workspace reference cannot read originals, shortcuts, folders or Google Workspace documents.");
        if (root.GetProperty("capabilities").GetProperty("canDownload").ValueKind != JsonValueKind.True)
            throw new DriveWorkspaceDownloadDeniedException();
        var version = WorkspacePositive(root, "version");
        if (WorkspacePositive(root, "size") != expectedBytes) throw new DriveWorkspaceObjectChangedException();
        return new(version, mime,string.Join('\n',memberships.Order(StringComparer.Ordinal)),drive);
    }
    private static long WorkspacePositive(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || !long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count < 1 || count.ToString(CultureInfo.InvariantCulture) != value.GetString())
            throw new InvalidDataException("Metadata workspace size/version must be a canonical positive int64 string.");
        return count;
    }
    private sealed record WorkspaceMetadata(long Version, string MimeType,string Parents,string? SharedDriveId);
}
