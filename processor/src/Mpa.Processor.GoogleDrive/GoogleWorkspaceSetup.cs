using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveWorkspaceAccess(string AccountId, string FolderId, string Name, long DriveVersion, bool Shared, bool VerifiedEmpty);
public sealed class DriveWorkspaceSelectionException() : IOException("Choose a dedicated metadata folder, rather than a Google Drive root.");

/// <summary>Read-only setup inspection. This is neither an authority grant nor a folder
/// creation operation. Recheck the chosen actual Drive ID before local enrollment.</summary>
public interface IGoogleWorkspaceSetup
{
    Task<DriveWorkspaceAccess> InspectWorkspaceAsync(string folderId, CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceSetup
{
    public Task<DriveWorkspaceAccess> InspectWorkspaceAsync(string folderId, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(folderId) || folderId == "root") throw new ArgumentException("Choose a dedicated metadata workspace folder by its actual Drive ID.", nameof(folderId));
        var account = _expectedPermissionId ?? throw new InvalidOperationException("Workspace setup requires an independently connected account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var rootBytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/root?fields=id"), 4096, token).ConfigureAwait(false);
            using (var rootInfo = JsonDocument.Parse(rootBytes))
            {
                if (!rootInfo.RootElement.TryGetProperty("id", out var rootId) || rootId.ValueKind != JsonValueKind.String || !ValidIdentity(rootId.GetString()))
                    throw new InvalidDataException("Google did not return a canonical My Drive root identity.");
                if (rootId.GetString() == folderId) throw new DriveWorkspaceSelectionException();
            }
            const string fields = "id,name,mimeType,trashed,version,shared,driveId,capabilities(canListChildren,canAddChildren)";
            var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(folderId) +
                "?supportsAllDrives=true&fields=" + Uri.EscapeDataString(fields)), 32768, token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.GetProperty("id").GetString() != folderId ||
                    root.GetProperty("mimeType").GetString() != "application/vnd.google-apps.folder" || root.GetProperty("trashed").ValueKind != JsonValueKind.False)
                    throw new DriveWorkspaceObjectChangedException();
                var version = root.GetProperty("version").GetString(); var name = root.GetProperty("name").GetString();
                if (!long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 ||
                    number.ToString(CultureInfo.InvariantCulture) != version || string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 16384)
                    throw new InvalidDataException("Workspace name or revision is invalid.");
                var capabilities = root.GetProperty("capabilities");
                if (!Flag(capabilities, "canListChildren") || !Flag(capabilities, "canAddChildren"))
                    throw new HttpRequestException("Choose a metadata folder that MPA can both read and write.", null, HttpStatusCode.Forbidden);
                var teamDrive = false;
                if (root.TryGetProperty("driveId", out var drive))
                {
                    if (drive.ValueKind != JsonValueKind.String || !ValidIdentity(drive.GetString())) throw new InvalidDataException("Invalid shared-drive identity.");
                    if (drive.GetString() == folderId) throw new DriveWorkspaceSelectionException();
                    teamDrive = true;
                }
                // Drive omits `shared` for shared-drive items. Their driveId already
                // requires shared-workspace approval; omission never means private.
                // https://developers.google.com/workspace/drive/api/reference/rest/v3/files
                var shared = root.TryGetProperty("shared", out _) ? Flag(root, "shared") :
                    teamDrive ? true : throw new InvalidDataException("My Drive workspace sharing state is missing.");
                shared |= teamDrive;
                var children = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files?pageSize=1&spaces=drive&includeItemsFromAllDrives=true&supportsAllDrives=true&q=" +
                    Uri.EscapeDataString("trashed = false and '" + folderId + "' in parents") + "&fields=" + Uri.EscapeDataString("files(id),nextPageToken,incompleteSearch")), 8192, token).ConfigureAwait(false);
                using var listing = JsonDocument.Parse(children); var page = listing.RootElement; var files = page.GetProperty("files");
                if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 1 || files.EnumerateArray().Any(file =>
                    file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("id", out var fileId) || fileId.ValueKind != JsonValueKind.String || !ValidIdentity(fileId.GetString())))
                    throw new InvalidDataException("Invalid bounded workspace contents inspection.");
                var incomplete = Flag(page, "incompleteSearch"); var hasNext = page.TryGetProperty("nextPageToken", out var next);
                if (hasNext)
                {
                    if (next.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid workspace contents continuation.");
                    var nextText = next.GetString();
                    if (nextText is not { Length: > 0 and <= 4096 } || nextText.Any(char.IsControl)) throw new InvalidDataException("Invalid workspace contents continuation.");
                }
                token.ThrowIfCancellationRequested(); return new DriveWorkspaceAccess(account, folderId, name, number, shared,
                    files.GetArrayLength() == 0 && !hasNext && !incomplete);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { throw new InvalidDataException("Google returned malformed metadata workspace access information.", error); }
        }, cancellationToken);
    }
    private static bool Flag(JsonElement value, string name) => value.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => throw new InvalidDataException("Workspace access flags must be explicit booleans.")
    };
}
