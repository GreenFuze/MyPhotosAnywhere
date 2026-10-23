using System.Collections.ObjectModel;
using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveWorkspaceMetadataCandidate(string FileId, long Version, long Bytes, string MimeType);
public sealed record DriveWorkspaceMetadataPage(ReadOnlyCollection<DriveWorkspaceMetadataCandidate> Objects, string? NextPageToken, bool IncompleteSearch);

/// <summary>Unfiltered metadata-workspace discovery with separate JSON/binary caps.
/// Candidates and MIME types are untrusted, not valid compressed facts, signed roles,
/// history, activation or publication rights. Originals and unsupported children are
/// rejected without downloading, deleting, moving or overwriting them.</summary>
public interface IGoogleWorkspaceMetadataDiscovery
{
    Task<DriveWorkspaceMetadataPage> ReadWorkspaceMetadataPageAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken);
    Task<DriveWorkspaceObject> ReadWorkspaceMetadataCandidateAsync(string folderId, DriveWorkspaceMetadataCandidate candidate, CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceMetadataDiscovery
{
    public Task<DriveWorkspaceMetadataPage> ReadWorkspaceMetadataPageAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken) =>
        ReadWorkspaceCandidatesAsync(folderId, sharedDriveId, pageToken, allChildren: true, allowBinary: true, cancellationToken);

    private Task<DriveWorkspaceMetadataPage> ReadWorkspaceCandidatesAsync(string folderId, string? sharedDriveId, string? pageToken,
        bool allChildren, bool allowBinary, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(folderId) || folderId == "root" || sharedDriveId is not null && !ValidIdentity(sharedDriveId))
            throw new ArgumentException("A selected canonical workspace and optional shared-drive identity are required.");
        if (pageToken is not null && (pageToken.Length is < 1 or > 4096 || pageToken.Any(char.IsControl))) throw new ArgumentException("Invalid metadata document page token.", nameof(pageToken));
        _ = _expectedPermissionId ?? throw new InvalidOperationException("Metadata discovery requires a captured Google account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            // Names/properties never decide authority or hide unsupported originals.
            var query = "trashed = false and " + (allChildren ? "" : "mimeType = 'application/json' and ") + "'" + folderId + "' in parents";
            var uri = "https://www.googleapis.com/drive/v3/files?pageSize=100&spaces=drive&includeItemsFromAllDrives=true&supportsAllDrives=true&q=" +
                Uri.EscapeDataString(query) + "&fields=" + Uri.EscapeDataString("nextPageToken,incompleteSearch,files(" + WorkspaceObjectFields + ")") +
                (sharedDriveId is null ? "&corpora=user" : "&corpora=drive&driveId=" + Uri.EscapeDataString(sharedDriveId)) +
                (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var bytes = await MediaGetAsync(credential, new(uri), 512 * 1024, token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes); var root = document.RootElement; var files = root.GetProperty("files");
                if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 100) throw new InvalidDataException("Workspace objects exceeded the requested page bound.");
                var result = new List<DriveWorkspaceMetadataCandidate>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in files.EnumerateArray())
                {
                    var id = file.GetProperty("id").GetString(); var length = WorkspacePositive(file, "size");
                    if (!ValidIdentity(id) || id == folderId || id == "root" || !seen.Add(id!)) throw new InvalidDataException("Workspace candidate identity is invalid.");
                    var metadata = ParseWorkspaceMetadata(file, folderId, id!, length);
                    if (!allowBinary && metadata.MimeType != "application/json" || length > WorkspaceCandidateLimit(metadata.MimeType))
                        throw new InvalidDataException("Workspace candidate exceeds its metadata encoding boundary.");
                    result.Add(new(id!, metadata.Version, length, metadata.MimeType));
                }
                var next = root.TryGetProperty("nextPageToken", out var nextValue) ? Text(nextValue, 4096) : null;
                if (next is not null && (next.Length == 0 || next.Any(char.IsControl))) throw new InvalidDataException("Invalid workspace metadata continuation.");
                var incomplete = root.TryGetProperty("incompleteSearch", out var flag) && flag.GetBoolean();
                token.ThrowIfCancellationRequested(); return new DriveWorkspaceMetadataPage(result.AsReadOnly(), next, incomplete);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new InvalidDataException("Google returned malformed workspace metadata inventory.", error); }
        }, cancellationToken);
    }

    public Task<DriveWorkspaceObject> ReadWorkspaceMetadataCandidateAsync(string folderId, DriveWorkspaceMetadataCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!ValidIdentity(folderId) || folderId == "root" || !ValidIdentity(candidate.FileId) || candidate.FileId is "root" || candidate.FileId == folderId ||
            candidate.Version < 1 || candidate.Bytes < 1 || candidate.Bytes > WorkspaceCandidateLimit(candidate.MimeType))
            throw new ArgumentException("An exact bounded metadata identity, encoding, version and size are required.", nameof(candidate));
        var account = _expectedPermissionId ?? throw new InvalidOperationException("Metadata discovery requires a captured Google account identity.");
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var before = await ReadWorkspaceMetadataAsync(credential, folderId, candidate.FileId, candidate.Bytes, token).ConfigureAwait(false);
            if (before.Version != candidate.Version || before.MimeType != candidate.MimeType) throw new DriveWorkspaceObjectChangedException();
            var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(candidate.FileId) + "?supportsAllDrives=true&alt=media"), (int)candidate.Bytes, token).ConfigureAwait(false);
            if (bytes.LongLength != candidate.Bytes) throw new DriveWorkspaceObjectChangedException();
            var after = await ReadWorkspaceMetadataAsync(credential, folderId, candidate.FileId, candidate.Bytes, token).ConfigureAwait(false);
            if (before != after) throw new DriveWorkspaceObjectChangedException(); token.ThrowIfCancellationRequested();
            return new DriveWorkspaceObject(account, candidate.FileId, folderId, after.Version, bytes);
        }, cancellationToken);
    }
    private static int WorkspaceCandidateLimit(string mimeType) => mimeType switch
    {
        "application/json" => MaximumWorkspaceDocumentBytes,
        "application/gzip" or "application/octet-stream" => MaximumWorkspaceObjectBytes,
        _ => throw new ArgumentException("Only bounded MPA metadata encodings can be read.", nameof(mimeType))
    };
}
