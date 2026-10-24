using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveChangeCursor(string AccountId, string? SharedDriveId, string Token);
public sealed record DriveChangedFile(string MimeType, ReadOnlyCollection<string> Parents, bool Trashed, long Version, long? Bytes, bool? CanDownload);
public sealed record DriveObservedChange(string Kind, string Id, bool Removed, DriveChangedFile? File);
public sealed record DriveChangePage(ReadOnlyCollection<DriveObservedChange> Changes, DriveChangeCursor? Next, DriveChangeCursor? AtEnd);

/// <summary>Account/shared-drive feed, not a workspace-filtered or atomic snapshot.
/// The owner must reconcile both current parent membership and previously known IDs,
/// persist applied observations before advancing, and verify signed authority separately.
/// Removed observations confer no instruction or permission to delete an original.</summary>
public interface IGoogleWorkspaceChanges
{
    Task<DriveChangeCursor> GetChangeCursorAsync(string? sharedDriveId, CancellationToken cancellationToken);
    Task<DriveChangePage> ReadChangesAsync(DriveChangeCursor cursor, CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceChanges
{
    public Task<DriveChangeCursor> GetChangeCursorAsync(string? sharedDriveId, CancellationToken cancellationToken)
    {
        if (sharedDriveId is not null && !ValidIdentity(sharedDriveId)) throw new ArgumentException("Invalid shared-drive cursor scope.", nameof(sharedDriveId));
        var account = _expectedPermissionId ?? throw new InvalidOperationException("Change cursors require a captured Google account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var uri = "https://www.googleapis.com/drive/v3/changes/startPageToken?supportsAllDrives=true&fields=startPageToken" +
                (sharedDriveId is null ? "" : "&driveId=" + Uri.EscapeDataString(sharedDriveId));
            var bytes = await MediaGetAsync(credential, new(uri), 8192, token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes); var value = ChangeToken(document.RootElement.GetProperty("startPageToken"));
                token.ThrowIfCancellationRequested(); return new DriveChangeCursor(account, sharedDriveId, value);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new InvalidDataException("Google returned a malformed change cursor.", error); }
        }, cancellationToken);
    }

    public Task<DriveChangePage> ReadChangesAsync(DriveChangeCursor cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        _ = _expectedPermissionId ?? throw new InvalidOperationException("Change cursors require a captured Google account identity.");
        if (cursor.AccountId != _expectedPermissionId || cursor.SharedDriveId is not null && !ValidIdentity(cursor.SharedDriveId) || !ValidChangeToken(cursor.Token))
            throw new ArgumentException("A captured cursor must belong to this account and drive scope.", nameof(cursor));
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            const string fields = "nextPageToken,newStartPageToken,changes(fileId,driveId,changeType,removed,file(id,mimeType,parents,trashed,version,size,capabilities(canDownload)))";
            var uri = "https://www.googleapis.com/drive/v3/changes?pageSize=100&spaces=drive&supportsAllDrives=true&includeItemsFromAllDrives=true&includeRemoved=true&includeCorpusRemovals=true&fields=" +
                Uri.EscapeDataString(fields) + "&pageToken=" + Uri.EscapeDataString(cursor.Token) +
                (cursor.SharedDriveId is null ? "" : "&driveId=" + Uri.EscapeDataString(cursor.SharedDriveId));
            var bytes = await MediaGetAsync(credential, new(uri), 512 * 1024, token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes); var root = document.RootElement; var rows = root.GetProperty("changes");
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100) throw new InvalidDataException("Drive changes exceeded the captured page bound.");
                var next = root.TryGetProperty("nextPageToken", out var nextValue) ? ChangeToken(nextValue) : null;
                var end = root.TryGetProperty("newStartPageToken", out var endValue) ? ChangeToken(endValue) : null;
                if ((next is null) == (end is null) || next == cursor.Token) throw new InvalidDataException("Drive change continuation is missing, ambiguous or nonadvancing.");
                var changes = new List<DriveObservedChange>();
                foreach (var row in rows.EnumerateArray())
                {
                    var kind = row.GetProperty("changeType").GetString();
                    if (kind is not ("file" or "drive")) throw new InvalidDataException("Unsupported Drive change kind; its cursor was not advanced.");
                    var id = row.GetProperty(kind == "file" ? "fileId" : "driveId").GetString();
                    if (!ValidIdentity(id)) throw new InvalidDataException("A Drive change lost its opaque identity.");
                    var removed = row.GetProperty("removed").GetBoolean(); DriveChangedFile? file = null;
                    if (row.TryGetProperty("file", out var value))
                    {
                        if (kind != "file" || value.ValueKind != JsonValueKind.Object || value.GetProperty("id").GetString() != id)
                            throw new InvalidDataException("Drive change substituted another file identity or kind.");
                        var parents = new List<string>(); var unique = new HashSet<string>(StringComparer.Ordinal);
                        if (value.TryGetProperty("parents", out var parentValues))
                        {
                            if (parentValues.ValueKind != JsonValueKind.Array || parentValues.GetArrayLength() > 16) throw new InvalidDataException("Changed file parents exceed their bound.");
                            foreach (var parent in parentValues.EnumerateArray())
                            {
                                var folder = parent.GetString(); if (!ValidIdentity(folder) || !unique.Add(folder!)) throw new InvalidDataException("Changed file parent identity is invalid.");
                                parents.Add(folder!);
                            }
                        }
                        var mime = Text(value.GetProperty("mimeType"), 256); var version = WorkspacePositive(value, "version"); long? length = null;
                        if (value.TryGetProperty("size", out var size))
                        {
                            if (size.ValueKind != JsonValueKind.String || !long.TryParse(size.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                                parsed < 0 || parsed.ToString(CultureInfo.InvariantCulture) != size.GetString()) throw new InvalidDataException("Changed file size is not canonical.");
                            length = parsed;
                        }
                        bool? downloadable = value.TryGetProperty("capabilities", out var capabilities) && capabilities.TryGetProperty("canDownload", out var canDownload) ? canDownload.GetBoolean() : null;
                        file = new(mime, parents.AsReadOnly(), value.GetProperty("trashed").GetBoolean(), version, length, downloadable);
                    }
                    if (kind == "file" && !removed && file is null) throw new InvalidDataException("A current file change lost its required observed state.");
                    changes.Add(new(kind, id!, removed, file));
                }
                token.ThrowIfCancellationRequested(); return new DriveChangePage(changes.AsReadOnly(), next is null ? null : cursor with { Token = next }, end is null ? null : cursor with { Token = end });
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new InvalidDataException("Google returned malformed change observations; its cursor was not advanced.", error); }
        }, cancellationToken);
    }
    private static bool ValidChangeToken(string? value) => value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl);
    private static string ChangeToken(JsonElement value)
    {
        var token = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return ValidChangeToken(token) ? token! : throw new InvalidDataException("Invalid opaque Drive change token.");
    }
}
