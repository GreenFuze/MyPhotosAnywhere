using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveFolder(string Id, string Name, bool CanListChildren, IReadOnlyList<string> Parents, string? SharedDriveId);
public sealed record DriveFolderPage(IReadOnlyList<DriveFolder> Folders, string? NextPageToken, bool IncompleteSearch);

public sealed partial class GoogleAccountConnection
{
    private const string FolderFields = "id,name,mimeType,trashed,parents,driveId,capabilities(canListChildren)";

    public Task<DriveFolder> ReadFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(folderId)) throw new ArgumentException("Invalid Drive folder identity.", nameof(folderId));
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(folderId) +
                "?supportsAllDrives=true&fields=" + Uri.EscapeDataString(FolderFields)), 16384, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes); var folder = Folder(document.RootElement);
            // Drive's root alias resolves to its canonical ID before persistence. Other IDs
            // must match exactly; paths, names and shortcuts cannot substitute a source.
            if (folderId != "root" && folder.Id != folderId) throw new InvalidDataException("Drive returned a different folder identity.");
            return folder;
        }, cancellationToken);
    }

    public Task<DriveFolderPage> ReadFolderPageAsync(string parentId, string? pageToken, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(parentId) || parentId == "root") throw new ArgumentException("Resolve the My Drive root alias before listing its canonical folder identity.", nameof(parentId));
        if (pageToken is not null && (pageToken.Length is < 1 or > 4096 || pageToken.Any(char.IsControl))) throw new ArgumentException("Invalid folder page token.", nameof(pageToken));
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var query = "trashed = false and mimeType = 'application/vnd.google-apps.folder' and '" + parentId + "' in parents";
            var uri = "https://www.googleapis.com/drive/v3/files?pageSize=60&spaces=drive&orderBy=name_natural&includeItemsFromAllDrives=true&supportsAllDrives=true&q=" +
                Uri.EscapeDataString(query) + "&fields=" + Uri.EscapeDataString("nextPageToken,incompleteSearch,files(" + FolderFields + ")") +
                (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var bytes = await MediaGetAsync(credential, new(uri), 256 * 1024, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            var folders = new List<DriveFolder>();
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                if (folders.Count == 60) throw new InvalidDataException("Drive returned more folders than the requested page bound.");
                var folder = Folder(item);
                if (!folder.Parents.Contains(parentId, StringComparer.Ordinal)) throw new InvalidDataException("Drive folder listing returned an item outside the captured parent.");
                folders.Add(folder);
            }
            return new DriveFolderPage(folders.AsReadOnly(), root.TryGetProperty("nextPageToken", out var next) ? Text(next, 4096) : null,
                root.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean());
        }, cancellationToken);
    }
    private static DriveFolder Folder(JsonElement item)
    {
        var id = Text(item.GetProperty("id"), 256);
        if (!ValidIdentity(id) || Text(item.GetProperty("mimeType"), 256) != "application/vnd.google-apps.folder" ||
            item.TryGetProperty("trashed", out var trashed) && trashed.GetBoolean()) throw new InvalidDataException("Drive did not return an available folder.");
        var parents = new List<string>();
        if (item.TryGetProperty("parents", out var parentIds)) foreach (var parent in parentIds.EnumerateArray())
        {
            var value = Text(parent, 256);
            if (parents.Count == 16 || !ValidIdentity(value) || parents.Contains(value, StringComparer.Ordinal)) throw new InvalidDataException("Drive folder parent identities exceed their supported bounds.");
            parents.Add(value);
        }
        var driveId = item.TryGetProperty("driveId", out var drive) ? Text(drive, 256) : null;
        if (driveId is not null && !ValidIdentity(driveId)) throw new InvalidDataException("Drive returned an invalid shared-drive identity.");
        var canList = item.TryGetProperty("capabilities", out var capabilities) && capabilities.TryGetProperty("canListChildren", out var permission) && permission.GetBoolean();
        return new(id, Text(item.GetProperty("name"), 1024), canList, parents.AsReadOnly(), driveId);
    }
}
