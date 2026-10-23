using System.Collections.ObjectModel;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveWorkspaceDocumentCandidate(string FileId, long Version, long Bytes, string MimeType = "application/json");
public sealed record DriveWorkspaceDocumentPage(ReadOnlyCollection<DriveWorkspaceDocumentCandidate> Documents, string? NextPageToken, bool IncompleteSearch);

/// <summary>Borrowed account-owned read operations only. A candidate is untrusted content:
/// the caller must verify the enrolled signature/library, complete inventory/change
/// reconciliation and high-water state. An empty/partial page grants no publication rights.</summary>
public interface IGoogleWorkspaceDiscovery
{
    Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken);
    Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>Initial policy bootstrap must account for every child, rather than infer
/// an empty workspace from a JSON-only search. Unsupported children fail closed without
/// downloading, moving or deleting them. This is not general catalog dependency discovery.</summary>
public interface IGoogleInitialWorkspaceDiscovery
{
    Task<DriveWorkspaceDocumentPage> ReadInitialWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceDiscovery, IGoogleInitialWorkspaceDiscovery
{
    // Signed document envelopes have a separate smaller cap than gzip facts objects.
    public const int MaximumWorkspaceDocumentBytes = 256 * 1024;
    public Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken) =>
        ReadWorkspaceDocumentsAsync(folderId, sharedDriveId, pageToken, false, cancellationToken);

    public Task<DriveWorkspaceDocumentPage> ReadInitialWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken) =>
        ReadWorkspaceDocumentsAsync(folderId, sharedDriveId, pageToken, true, cancellationToken);

    private Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, bool allChildren, CancellationToken cancellationToken)
    {
        // Validate synchronously before creating the mapping task, preserving the
        // existing invalid-input contract and the strict initial JSON-only boundary.
        var reading = ReadWorkspaceCandidatesAsync(folderId, sharedDriveId, pageToken, allChildren, allowBinary: false, cancellationToken);
        return DocumentsAsync(reading);
        static async Task<DriveWorkspaceDocumentPage> DocumentsAsync(Task<DriveWorkspaceMetadataPage> source)
        {
            var page = await source.ConfigureAwait(false);
            return new(page.Objects.Select(item => new DriveWorkspaceDocumentCandidate(item.FileId, item.Version, item.Bytes)).ToList().AsReadOnly(), page.NextPageToken, page.IncompleteSearch);
        }
    }
    public Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.MimeType != "application/json") throw new ArgumentException("Document reading requires the captured JSON encoding.", nameof(candidate));
        return ReadWorkspaceMetadataCandidateAsync(folderId, new(candidate.FileId, candidate.Version, candidate.Bytes, "application/json"), cancellationToken);
    }
}
