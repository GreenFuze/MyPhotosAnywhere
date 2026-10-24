using System.Collections.ObjectModel;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed class WorkspaceIncrementalRefreshRequiredException() : InvalidOperationException(
    "This incremental read needs a complete current inventory. No publication was admitted.");

/// <summary>Actual durable history startup and per-page callbacks inside the borrowed
/// account lease. Callbacks must be joined and must not reenter that account.</summary>
public interface IWorkspaceIncrementalInventoryObserver : IWorkspaceInventoryObserver, IWorkspaceHistoryObserver
{
    Task<DriveChangeCursor> StartHistoryAsync(CancellationToken token);
}

/// <summary>Read-only bounded inventory derivation from a supplied baseline and actual
/// changes. A baseline or returned inventory is not a current-proof/write capability.
/// The concrete owner must separately bind native baseline ownership, access, signatures,
/// exact bytes, complete local inventories and final fences before any admission.</summary>
public sealed class WorkspaceIncrementalInventoryReader
{
    private readonly IGoogleWorkspaceChanges _changes;
    private readonly WorkspaceDocumentInventory _baseline;
    private readonly WorkspacePublicationExpectation? _expected;

    public WorkspaceIncrementalInventoryReader(IGoogleWorkspaceChanges changes, WorkspaceDocumentInventory baseline,
        WorkspacePublicationExpectation? expected = null)
    {
        _changes = changes ?? throw new ArgumentNullException(nameof(changes)); ArgumentNullException.ThrowIfNull(baseline);
        if (!Identity(baseline.AccountId) || !Identity(baseline.FolderId) || baseline.FolderId == "root" ||
            baseline.SharedDriveId is not null && !Identity(baseline.SharedDriveId) || baseline.MissingKnownIds.Count != 0 ||
            baseline.Documents.Count > WorkspaceDocumentInventoryReader.MaximumDocuments)
            throw new ArgumentException("Incremental reads require a complete bounded baseline in one captured corpus.", nameof(baseline));
        var rows = baseline.Documents.ToArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (!Identity(row.FileId) || row.FileId is "root" || row.FileId == baseline.FolderId || !ids.Add(row.FileId) || row.Version < 1 ||
                row.Bytes < 1 || row.Bytes > WorkspaceDocumentInventoryReader.CandidateByteLimit(row.MimeType, includeBinaryMetadata: true))
                throw new ArgumentException("Incremental baseline changed its metadata identity, type or bounds.", nameof(baseline));
        _baseline = baseline with { Documents = new(rows), MissingKnownIds = new([]) }; CheckCursor(_baseline.Cursor);
        if (expected is not null && (expected.Id == Guid.Empty || expected.Scope.GenerationId == Guid.Empty ||
            expected.Scope.AccountId != baseline.AccountId || expected.Scope.FolderId != baseline.FolderId ||
            expected.FileId is not { } file || !Identity(file) || file is "root" || file == baseline.FolderId || expected.VerifiedVersion is not >= 1 ||
            expected.Bytes < 1 || expected.Bytes > WorkspaceDocumentInventoryReader.CandidateByteLimit(expected.MimeType, includeBinaryMetadata: true) ||
            expected.Sha256 is not { Length: 64 } || expected.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            (expected.Role == "facts-chunk" ? expected.MimeType != "application/gzip" :
                expected.Role is not ("facts-index" or "coverage-page" or "coverage-root") || expected.MimeType != "application/json")))
            throw new ArgumentException("Only one exact previously read-back generation dependency may extend an incremental inventory.", nameof(expected));
        _expected = expected;
    }

    public async Task<WorkspaceDocumentInventory> CaptureAsync(IWorkspaceIncrementalInventoryObserver observer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observer); token.ThrowIfCancellationRequested();
        await observer.StartedAsync(_baseline.Cursor, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        var cursor = await observer.StartHistoryAsync(token).ConfigureAwait(false); CheckCursor(cursor); token.ThrowIfCancellationRequested();
        var rows = _baseline.Documents.ToDictionary(row => row.FileId, StringComparer.Ordinal);
        var known = rows.Keys.ToHashSet(StringComparer.Ordinal);
        if (_expected?.FileId is { } expectedFile) known.Add(expectedFile);
        var expectedSeen = _expected is null || rows.TryGetValue(_expected.FileId!, out var prior) && prior.Bytes == _expected.Bytes &&
            prior.MimeType == _expected.MimeType && prior.Version >= _expected.VerifiedVersion;
        var visited = new HashSet<string>(StringComparer.Ordinal) { cursor.Token };
        for (var number = 0; number < WorkspaceDocumentInventoryReader.MaximumPages; number++)
        {
            token.ThrowIfCancellationRequested();
            var page = await _changes.ReadChangesAsync(cursor, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (page.Changes.Count > 100 || (page.Next is null) == (page.AtEnd is null))
                throw new InvalidDataException("Incremental changes lost their bounded explicit continuation.");
            var next = page.Next ?? page.AtEnd!; CheckCursor(next); var refresh = false;
            foreach (var change in page.Changes)
            {
                WorkspaceHistoryReplayReader.ValidateChange(change);
                var file = change.File;
                if (_expected?.FileId == change.Id && change.Kind == "file" && !change.Removed && file is { Trashed: false, CanDownload: true } &&
                    file.Parents.Contains(_baseline.FolderId, StringComparer.Ordinal) && file.MimeType == _expected.MimeType &&
                    file.Bytes == _expected.Bytes && file.Version >= _expected.VerifiedVersion)
                {
                    rows[change.Id] = new(change.Id, file.Version, file.Bytes.Value, file.MimeType); expectedSeen = true;
                    if (rows.Count > WorkspaceDocumentInventoryReader.MaximumDocuments) throw new WorkspaceInventoryIncompleteException();
                    continue;
                }
                if (change.Kind == "drive" || change.Removed || file is null || change.Id == _baseline.FolderId ||
                    file.MimeType == "application/vnd.google-apps.folder") { refresh = true; continue; }
                if (rows.TryGetValue(change.Id, out var previous))
                    refresh |= file.Trashed || file.CanDownload != true || !file.Parents.Contains(_baseline.FolderId, StringComparer.Ordinal) ||
                        file.MimeType != previous.MimeType || file.Bytes != previous.Bytes || file.Version > previous.Version;
                else if (file.Parents.Contains(_baseline.FolderId, StringComparer.Ordinal)) refresh = true;
            }
            var inventory = _baseline with { Documents = Ordered(rows.Values), Cursor = next };
            var evidence = WorkspaceHistoryReplayReader.Evidence(inventory, rows, known, cursor, next, page, observeFolderChanges: true);
            await observer.PageAsync(evidence, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (evidence.Events.Any(item => item.RecoveryRequired)) throw new WorkspaceHistoryRecoveryRequiredException();
            if (refresh || evidence.InventoryChanged) throw new WorkspaceIncrementalRefreshRequiredException();
            if (page.AtEnd is not null)
            {
                if (!expectedSeen) throw new WorkspaceIncrementalRefreshRequiredException();
                return inventory;
            }
            if (!visited.Add(next.Token)) throw new WorkspaceInventoryIncompleteException();
            cursor = next;
        }
        throw new WorkspaceHistoryReplayPendingException();
    }

    private void CheckCursor(DriveChangeCursor cursor)
    {
        if (cursor.AccountId != _baseline.AccountId || cursor.SharedDriveId != _baseline.SharedDriveId ||
            cursor.Token is not { Length: > 0 and <= 4096 } || cursor.Token.Any(char.IsControl))
            throw new InvalidDataException("Incremental changes substituted their captured corpus or cursor.");
    }
    private static ReadOnlyCollection<DriveWorkspaceDocumentCandidate> Ordered(IEnumerable<DriveWorkspaceDocumentCandidate> rows) =>
        rows.OrderBy(row => row.FileId, StringComparer.Ordinal).ToList().AsReadOnly();
    private static bool Identity(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
