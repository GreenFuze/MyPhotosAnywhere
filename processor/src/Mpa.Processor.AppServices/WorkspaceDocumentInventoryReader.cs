using System.Collections.ObjectModel;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed class WorkspaceInventoryIncompleteException() : InvalidOperationException("Workspace metadata changed or its inventory was incomplete. Keep the previous catalog and reconcile again before publication.");
public sealed record WorkspaceDocumentInventory(string AccountId, string FolderId, string? SharedDriveId,
    ReadOnlyCollection<DriveWorkspaceDocumentCandidate> Documents, ReadOnlyCollection<string> MissingKnownIds, DriveChangeCursor Cursor);

/// <summary>Joined durable observation callbacks, not permission. Implementations must
/// persist before returning and must not re-enter the borrowed account/network owner.</summary>
public interface IWorkspaceInventoryObserver
{
    Task StartedAsync(DriveChangeCursor cursor, CancellationToken token);
    Task PageAsync(IReadOnlyCollection<DriveWorkspaceDocumentCandidate> candidates, CancellationToken token);
}

/// <summary>Bounded read-only inventory bracketed by the account's change feed. This
/// is not a signature/authority proof or atomic remote snapshot. Candidate content must
/// still be verified, relevant observations persisted, and a final change check completed
/// before a separate publication owner may admit work. Never persist only this cursor.</summary>
public sealed class WorkspaceDocumentInventoryReader
{
    public const int MaximumDocuments = 4096;
    public const int MaximumPages = 128;
    private readonly IGoogleWorkspaceDiscovery _documents;
    private readonly IGoogleWorkspaceChanges _changes;
    private readonly string _accountId;
    private readonly string _folderId;
    private readonly string? _sharedDriveId;
    private readonly bool _allChildChanges;
    private readonly bool _includeBinaryMetadata;

    public WorkspaceDocumentInventoryReader(IGoogleWorkspaceDiscovery documents, IGoogleWorkspaceChanges changes,
        string accountId, string folderId, string? sharedDriveId, bool allChildChanges = false, bool includeBinaryMetadata = false)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        if (!Identity(accountId) || !Identity(folderId) || folderId == "root" || sharedDriveId is not null && !Identity(sharedDriveId))
            throw new ArgumentException("A captured account and canonical metadata workspace are required.");
        _accountId = accountId; _folderId = folderId; _sharedDriveId = sharedDriveId; _allChildChanges = allChildChanges || includeBinaryMetadata; _includeBinaryMetadata = includeBinaryMetadata;
    }

    public async Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, CancellationToken token, IWorkspaceInventoryObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(previouslyKnownIds);
        var known = KnownIds(previouslyKnownIds); token.ThrowIfCancellationRequested();
        var start = await _changes.GetChangeCursorAsync(_sharedDriveId, token).ConfigureAwait(false); CheckCursor(start);
        token.ThrowIfCancellationRequested();
        if (observer is not null) await observer.StartedAsync(start, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var found = new Dictionary<string, DriveWorkspaceDocumentCandidate>(StringComparer.Ordinal);
        var pages = new HashSet<string>(StringComparer.Ordinal); string? next = null;
        for (var pageNumber = 0; ; pageNumber++)
        {
            token.ThrowIfCancellationRequested();
            if (pageNumber == MaximumPages) throw new WorkspaceInventoryIncompleteException();
            var page = await _documents.ReadWorkspaceDocumentsAsync(_folderId, _sharedDriveId, next, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (page.Documents.Count > 100) throw new InvalidDataException("Workspace inventory exceeded its page bound.");
            foreach (var candidate in page.Documents)
            {
                CheckCandidate(candidate);
                if (!found.TryAdd(candidate.FileId, candidate) || found.Count > MaximumDocuments) throw new WorkspaceInventoryIncompleteException();
            }
            // Even a valid page flagged as incomplete remains actual observed metadata.
            // Persist it before a later page, continuation or change fence can fail.
            if (observer is not null) await observer.PageAsync(page.Documents, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (page.IncompleteSearch) throw new WorkspaceInventoryIncompleteException();
            next = page.NextPageToken;
            if (next is null) break;
            if (!OpaqueToken(next) || !pages.Add(next)) throw new WorkspaceInventoryIncompleteException();
        }
        var missing = known.Except(found.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList().AsReadOnly();
        known.UnionWith(found.Keys); var cursor = await DrainUnchangedAsync(start, known, observer, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new(_accountId, _folderId, _sharedDriveId, found.Values.OrderBy(item => item.FileId, StringComparer.Ordinal).ToList().AsReadOnly(), missing, cursor);
    }

    /// <summary>Recheck after content/signature verification. Missing prior documents
    /// remain explicit; their absence never resets already accepted policies or counters.</summary>
    public Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, CancellationToken token, IWorkspaceInventoryObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.AccountId != _accountId || inventory.FolderId != _folderId || inventory.SharedDriveId != _sharedDriveId)
            throw new ArgumentException("Inventory belongs to another captured workspace.", nameof(inventory));
        CheckCursor(inventory.Cursor);
        if (inventory.Documents.Count > MaximumDocuments) throw new InvalidDataException("Workspace inventory exceeds its bound.");
        foreach (var candidate in inventory.Documents) CheckCandidate(candidate);
        var known = KnownIds(inventory.MissingKnownIds);
        foreach (var candidate in inventory.Documents)
            if (!known.Add(candidate.FileId)) throw new InvalidDataException("Workspace inventory aliases a present or missing identity.");
        if (known.Count > MaximumDocuments * 2) throw new InvalidDataException("Workspace observation identities exceed their bound.");
        return DrainUnchangedAsync(inventory.Cursor, known, observer, token, inventory);
    }

    private async Task<DriveChangeCursor> DrainUnchangedAsync(DriveChangeCursor cursor, HashSet<string> known, IWorkspaceInventoryObserver? observer, CancellationToken token,
        WorkspaceDocumentInventory? completedInventory = null)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { cursor.Token };
        for (var number = 0; number < MaximumPages; number++)
        {
            token.ThrowIfCancellationRequested();
            var page = await _changes.ReadChangesAsync(cursor, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (page.Changes.Count > 100 || (page.Next is null) == (page.AtEnd is null)) throw new InvalidDataException("Change page lost its bounded explicit continuation.");
            var next = page.Next ?? page.AtEnd!; CheckCursor(next);
            if (_includeBinaryMetadata && completedInventory is not null)
            {
                var present = completedInventory.Documents.ToDictionary(item => item.FileId, StringComparer.Ordinal);
                var evidence = WorkspaceHistoryReplayReader.Evidence(completedInventory, present, known, cursor, next, page, observeFolderChanges: true);
                if (evidence.Events.Count != 0)
                {
                    // Keep the entire actual rejected page before failure. Changes
                    // can be coalesced later; an observed removal must not disappear.
                    if (observer is IWorkspaceRejectedFenceObserver rejected)
                        await rejected.RejectedAsync(evidence, token).ConfigureAwait(false);
                    else if (observer is not null)
                        await observer.PageAsync(evidence.Candidates, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); throw new WorkspaceInventoryIncompleteException();
                }
            }
            foreach (var change in page.Changes)
            {
                if (!Identity(change.Id) || change.Kind is not ("file" or "drive")) throw new InvalidDataException("Invalid observed change identity or kind.");
                // Observe old IDs even after a move, trash, permission loss or MIME
                // change. Bootstrap additionally observes every child MIME, so an
                // unsupported original/folder cannot evade its complete empty-workspace check.
                if (change.Kind == "drive" && (_sharedDriveId is null || change.Id == _sharedDriveId) ||
                    change.Kind == "file" && (change.Id == _folderId || known.Contains(change.Id) ||
                    change.File is { } file && (_allChildChanges || file.MimeType == "application/json") && file.Parents.Contains(_folderId, StringComparer.Ordinal)))
                {
                    // Preserve newly seen supported metadata before closing this scan.
                    // Missing/unclassifiable changes still retain the unresolved start;
                    // they cannot advance a completed checkpoint or grant publication.
                    if (observer is not null && change.Kind == "file" && change.Id != _folderId && change.File is { Bytes: >= 1 } changed &&
                        (changed.MimeType == "application/json" || _includeBinaryMetadata && changed.MimeType is "application/gzip" or "application/octet-stream"))
                    {
                        var candidate = new DriveWorkspaceDocumentCandidate(change.Id, changed.Version, changed.Bytes.Value, changed.MimeType); CheckCandidate(candidate);
                        await observer.PageAsync(new[] { candidate }, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                    }
                    throw new WorkspaceInventoryIncompleteException();
                }
            }
            if (page.AtEnd is not null) return next;
            if (!visited.Add(next.Token)) throw new WorkspaceInventoryIncompleteException();
            cursor = next;
        }
        throw new WorkspaceInventoryIncompleteException();
    }

    private static HashSet<string> KnownIds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count > MaximumDocuments) throw new ArgumentException("Known workspace inventory exceeds its supported bound.", nameof(ids));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) if (!Identity(id) || id == "root" || !result.Add(id)) throw new ArgumentException("Known metadata identities must be canonical and distinct.", nameof(ids));
        return result;
    }
    private void CheckCandidate(DriveWorkspaceDocumentCandidate candidate)
    {
        if (!Identity(candidate.FileId) || candidate.FileId is "root" || candidate.FileId == _folderId || candidate.Version < 1 ||
            candidate.Bytes < 1 || candidate.Bytes > CandidateByteLimit(candidate.MimeType, _includeBinaryMetadata)) throw new InvalidDataException("Invalid captured workspace document.");
    }
    internal static int CandidateByteLimit(string mimeType, bool includeBinaryMetadata) => mimeType switch
    {
        "application/json" => GoogleAccountConnection.MaximumWorkspaceDocumentBytes,
        "application/gzip" or "application/octet-stream" when includeBinaryMetadata => GoogleAccountConnection.MaximumWorkspaceObjectBytes,
        _ => throw new InvalidDataException("Workspace candidate has an unsupported metadata encoding.")
    };
    private void CheckCursor(DriveChangeCursor cursor)
    {
        if (cursor.AccountId != _accountId || cursor.SharedDriveId != _sharedDriveId || !OpaqueToken(cursor.Token))
            throw new InvalidDataException("The change feed substituted another captured account, drive or cursor.");
    }
    private static bool Identity(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool OpaqueToken(string? value) => value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl);
}
