using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveSourceItem(string Id, long Version, string Name, string MimeType, long? Size,
    DriveFolder? Folder, DriveMediaFile? Media);
public sealed record DriveSourceItemFailure(string? FileId, int PageIndex, string Code);
public sealed record DriveSourcePage(IReadOnlyList<DriveSourceItem> Items, IReadOnlyList<DriveSourceItemFailure> Failures,
    string? NextPageToken, bool IncompleteSearch);

public sealed partial class GoogleAccountConnection
{
    /// <summary>One bounded metadata-only page within a captured canonical folder. Malformed
    /// items are isolated, while parent/response integrity failures cannot masquerade as an
    /// empty folder. Shortcuts are observed as shortcuts, never followed into new source scope.</summary>
    public Task<DriveSourcePage> ReadSourcePageAsync(string parentId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(parentId) || parentId == "root" || sharedDriveId is not null && !ValidIdentity(sharedDriveId))
            throw new ArgumentException("A canonical source folder and optional shared-drive identity are required.");
        if (pageToken is not null && (pageToken.Length is < 1 or > 4096 || pageToken.Any(char.IsControl))) throw new ArgumentException("Invalid source page token.", nameof(pageToken));
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var query = "trashed = false and '" + parentId + "' in parents";
            const string fields = "nextPageToken,incompleteSearch,files(id,name,mimeType,version,size,trashed,parents,driveId,capabilities(canListChildren),modifiedTime,imageMediaMetadata(width,height,time),videoMediaMetadata(width,height))";
            var uri = "https://www.googleapis.com/drive/v3/files?pageSize=60&spaces=drive&orderBy=name_natural&includeItemsFromAllDrives=true&supportsAllDrives=true" +
                "&q=" + Uri.EscapeDataString(query) + "&fields=" + Uri.EscapeDataString(fields) +
                (sharedDriveId is null ? "&corpora=user" : "&corpora=drive&driveId=" + Uri.EscapeDataString(sharedDriveId)) +
                (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var bytes = await MediaGetAsync(credential, new(uri), 512 * 1024, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            var items = new List<DriveSourceItem>(); var failures = new List<DriveSourceItemFailure>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var file in root.GetProperty("files").EnumerateArray())
            {
                if (index == 60) throw new InvalidDataException("Drive exceeded the captured source page bound.");
                string? id = null;
                if (file.ValueKind == JsonValueKind.Object && file.TryGetProperty("id", out var identity) && identity.ValueKind == JsonValueKind.String && ValidIdentity(identity.GetString()))
                {
                    id = identity.GetString();
                    if (!seen.Add(id!)) throw new InvalidDataException("Drive repeated a source file identity within a page.");
                }
                // Scope is validated independently from optional/individual metadata. A page
                // cannot smuggle in a file from another folder, even if all its facts parse.
                if (id is not null && !CapturedParent(file, parentId, sharedDriveId)) throw new InvalidDataException("Drive source page returned an item outside its captured folder/drive.");
                try { items.Add(SourceItem(file)); }
                catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                { failures.Add(new(id, index, "invalid-source-metadata")); }
                index++;
            }
            var next = root.TryGetProperty("nextPageToken", out var cursor) ? Text(cursor, 4096) : null;
            if (next is not null && (next.Length == 0 || next.Any(char.IsControl))) throw new InvalidDataException("Drive returned an invalid source checkpoint.");
            return new DriveSourcePage(items.AsReadOnly(), failures.AsReadOnly(), next, root.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean());
        }, cancellationToken);
    }
    private static DriveSourceItem SourceItem(JsonElement file)
    {
        var id = Text(file.GetProperty("id"), 256);
        if (!ValidIdentity(id) || file.TryGetProperty("trashed", out var trash) && trash.GetBoolean()) throw new InvalidDataException("Unavailable source item.");
        var version = Text(file.GetProperty("version"), 19);
        if (!long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1 || revision.ToString(CultureInfo.InvariantCulture) != version)
            throw new InvalidDataException("Invalid monotonic source version.");
        var name = Text(file.GetProperty("name"), 4096); var mime = Text(file.GetProperty("mimeType"), 256);
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || string.IsNullOrWhiteSpace(mime) || mime.Any(char.IsControl)) throw new InvalidDataException("Invalid source display metadata.");
        long? size = null;
        if (file.TryGetProperty("size", out var sizeValue))
        {
            var raw = Text(sizeValue, 19);
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 || length.ToString(CultureInfo.InvariantCulture) != raw)
                throw new InvalidDataException("Invalid source length.");
            size = length;
        }
        return new(id, revision, name, mime, size, mime == "application/vnd.google-apps.folder" ? Folder(file) : null,
            mime.StartsWith("image/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal) ? MediaFile(file) : null);
    }
    private static bool CapturedParent(JsonElement file, string parentId, string? driveId)
    {
        if (!file.TryGetProperty("parents", out var parents) || parents.ValueKind != JsonValueKind.Array || parents.GetArrayLength() is < 1 or > 16) return false;
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parent in parents.EnumerateArray())
        {
            if (parent.ValueKind != JsonValueKind.String || !ValidIdentity(parent.GetString()) || !values.Add(parent.GetString()!)) return false;
        }
        if (!values.Contains(parentId)) return false;
        return driveId is null || file.TryGetProperty("driveId", out var drive) && drive.ValueKind == JsonValueKind.String && drive.GetString() == driveId;
    }
}
