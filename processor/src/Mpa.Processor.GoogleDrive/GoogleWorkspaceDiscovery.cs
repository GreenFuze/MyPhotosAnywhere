using System.Collections.ObjectModel;
using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveWorkspaceDocumentCandidate(string FileId, long Version, long Bytes);
public sealed record DriveWorkspaceDocumentPage(ReadOnlyCollection<DriveWorkspaceDocumentCandidate> Documents, string? NextPageToken, bool IncompleteSearch);

/// <summary>Borrowed account-owned read operations only. A candidate is untrusted content:
/// the caller must verify the enrolled signature/library, complete inventory/change
/// reconciliation and high-water state. An empty/partial page grants no publication rights.</summary>
public interface IGoogleWorkspaceDiscovery
{
    Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken);
    Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceDiscovery
{
    // Signed document envelopes have a separate smaller cap than gzip facts objects.
    public const int MaximumWorkspaceDocumentBytes = 256 * 1024;
    public Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(folderId) || folderId == "root" || sharedDriveId is not null && !ValidIdentity(sharedDriveId))
            throw new ArgumentException("A selected canonical workspace and optional shared-drive identity are required.");
        if (pageToken is not null && (pageToken.Length is < 1 or > 4096 || pageToken.Any(char.IsControl))) throw new ArgumentException("Invalid metadata document page token.", nameof(pageToken));
        _ = _expectedPermissionId ?? throw new InvalidOperationException("Metadata discovery requires a captured Google account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            // Properties and names are not an authority filter: all ordinary JSON
            // candidates in this workspace remain subject to signature admission.
            var query = "trashed = false and mimeType = 'application/json' and '" + folderId + "' in parents";
            var uri = "https://www.googleapis.com/drive/v3/files?pageSize=100&spaces=drive&includeItemsFromAllDrives=true&supportsAllDrives=true&q=" +
                Uri.EscapeDataString(query) + "&fields=" + Uri.EscapeDataString("nextPageToken,incompleteSearch,files(" + WorkspaceObjectFields + ")") +
                (sharedDriveId is null ? "&corpora=user" : "&corpora=drive&driveId=" + Uri.EscapeDataString(sharedDriveId)) +
                (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var bytes = await MediaGetAsync(credential, new(uri), 512 * 1024, token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes); var root = document.RootElement; var files = root.GetProperty("files");
                if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 100) throw new InvalidDataException("Workspace documents exceeded the requested page bound.");
                var result = new List<DriveWorkspaceDocumentCandidate>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in files.EnumerateArray())
                {
                    var id = file.GetProperty("id").GetString(); var length = WorkspacePositive(file, "size");
                    if (!ValidIdentity(id) || id == folderId || id == "root" || !seen.Add(id!) || length > MaximumWorkspaceDocumentBytes)
                        throw new InvalidDataException("Workspace candidate identity or size is invalid; inventory remains unverified.");
                    var metadata = ParseWorkspaceMetadata(file, folderId, id!, length);
                    if (metadata.MimeType != "application/json") throw new InvalidDataException("A document candidate cannot substitute an original or another metadata role.");
                    result.Add(new(id!, metadata.Version, length));
                }
                var next = root.TryGetProperty("nextPageToken", out var nextValue) ? Text(nextValue, 4096) : null;
                if (next is not null && (next.Length == 0 || next.Any(char.IsControl))) throw new InvalidDataException("Invalid workspace document continuation.");
                var incomplete = root.TryGetProperty("incompleteSearch", out var flag) && flag.GetBoolean();
                token.ThrowIfCancellationRequested(); return new DriveWorkspaceDocumentPage(result.AsReadOnly(), next, incomplete);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new InvalidDataException("Google returned malformed workspace document inventory.", error); }
        }, cancellationToken);
    }

    public Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!ValidIdentity(folderId) || folderId == "root" || !ValidIdentity(candidate.FileId) || candidate.FileId is "root" || candidate.FileId == folderId ||
            candidate.Version < 1 || candidate.Bytes is < 1 or > MaximumWorkspaceDocumentBytes)
            throw new ArgumentException("A bounded captured document identity, version and size are required.");
        var account = _expectedPermissionId ?? throw new InvalidOperationException("Metadata discovery requires a captured Google account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var before = await ReadWorkspaceMetadataAsync(credential, folderId, candidate.FileId, candidate.Bytes, token).ConfigureAwait(false);
            if (before.Version != candidate.Version || before.MimeType != "application/json") throw new DriveWorkspaceObjectChangedException();
            var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(candidate.FileId) + "?supportsAllDrives=true&alt=media"), (int)candidate.Bytes, token).ConfigureAwait(false);
            if (bytes.LongLength != candidate.Bytes) throw new DriveWorkspaceObjectChangedException();
            var after = await ReadWorkspaceMetadataAsync(credential, folderId, candidate.FileId, candidate.Bytes, token).ConfigureAwait(false);
            if (before != after) throw new DriveWorkspaceObjectChangedException(); token.ThrowIfCancellationRequested();
            return new DriveWorkspaceObject(account, candidate.FileId, folderId, after.Version, bytes);
        }, cancellationToken);
    }
}
