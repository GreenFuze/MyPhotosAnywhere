using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed class WorkspaceHistoryReplayPendingException() : InvalidOperationException(
    "This bounded change-history batch is retained. Continue reconciliation from its durable cursor before publication.");

public sealed record WorkspaceHistoryPageEvidence(DriveChangeCursor From, DriveChangeCursor Next, bool AtEnd,
    ReadOnlyCollection<WorkspaceHistoryEvent> Events, ReadOnlyCollection<DriveWorkspaceDocumentCandidate> Candidates, bool InventoryChanged);

/// <summary>The callback must commit actual observations before returning. It runs
/// inside the joined account lease and must never re-enter that account/network owner.</summary>
public interface IWorkspaceHistoryObserver
{
    Task PageAsync(WorkspaceHistoryPageEvidence page, CancellationToken token);
}

/// <summary>Durable rejection callback inside the joined account lease. Never reenter
/// that account or acknowledge completion after the callback fails.</summary>
public interface IWorkspaceRejectedFenceObserver
{
    Task RejectedAsync(WorkspaceHistoryPageEvidence page, CancellationToken token);
}

public interface IWorkspaceHistoryDocumentSource
{
    bool RequiresHistoryReplay { get; }
    Task<WorkspaceDocumentInventory> ReplayHistoryAsync(WorkspaceDocumentInventory inventory, DriveChangeCursor from,
        IWorkspaceHistoryObserver observer, CancellationToken token);
}

/// <summary>Replay the retained interval against the actual current bounded inventory.
/// Unknown removals/access changes remain explicit recovery. Successful transport does
/// not verify signatures/ancestry, advance the earliest unresolved cursor or grant access.</summary>
public sealed class WorkspaceHistoryReplayReader(IGoogleWorkspaceChanges changes)
{
    private readonly IGoogleWorkspaceChanges _changes = changes ?? throw new ArgumentNullException(nameof(changes));

    public async Task<WorkspaceDocumentInventory> ReplayAsync(WorkspaceDocumentInventory inventory, DriveChangeCursor from,
        IWorkspaceHistoryObserver observer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(inventory); ArgumentNullException.ThrowIfNull(from); ArgumentNullException.ThrowIfNull(observer);
        CheckCursor(from, inventory);
        if (inventory.Documents.Count > WorkspaceDocumentInventoryReader.MaximumDocuments || inventory.MissingKnownIds.Count > WorkspaceDocumentInventoryReader.MaximumDocuments)
            throw new InvalidDataException("History replay exceeds its complete inventory bound.");
        var present = inventory.Documents.ToDictionary(item => item.FileId, StringComparer.Ordinal);
        var known = present.Keys.Union(inventory.MissingKnownIds, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var cursor = from; var visited = new HashSet<string>(StringComparer.Ordinal) { from.Token };
        for (var number = 0; number < WorkspaceDocumentInventoryReader.MaximumPages; number++)
        {
            token.ThrowIfCancellationRequested(); var page = await _changes.ReadChangesAsync(cursor, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (page.Changes.Count > 100 || (page.Next is null) == (page.AtEnd is null)) throw new InvalidDataException("History replay lost its bounded explicit continuation.");
            var next = page.Next ?? page.AtEnd!; CheckCursor(next, inventory);
            var evidence = Evidence(inventory, present, known, cursor, next, page);
            await observer.PageAsync(evidence, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (evidence.Events.Any(item => item.RecoveryRequired)) throw new WorkspaceHistoryRecoveryRequiredException();
            if (evidence.InventoryChanged) throw new WorkspaceInventoryIncompleteException();
            if (page.AtEnd is not null) return inventory with { Cursor = next };
            if (!visited.Add(next.Token)) throw new WorkspaceInventoryIncompleteException();
            cursor = next;
        }
        throw new WorkspaceHistoryReplayPendingException();
    }

    internal static WorkspaceHistoryPageEvidence Evidence(WorkspaceDocumentInventory inventory, Dictionary<string, DriveWorkspaceDocumentCandidate> present,
        HashSet<string> known, DriveChangeCursor cursor, DriveChangeCursor next, DriveChangePage page, bool observeFolderChanges = false)
    {
        var events = new List<WorkspaceHistoryEvent>(); var candidates = new Dictionary<string, DriveWorkspaceDocumentCandidate>(StringComparer.Ordinal); var changed = false;
        foreach (var item in page.Changes)
        {
            ValidateChange(item); var file = item.File;
            var relevant = item.Kind == "drive" && (inventory.SharedDriveId is null || item.Id == inventory.SharedDriveId) || item.Kind == "file" &&
                (item.Removed || file is null || item.Id == inventory.FolderId || known.Contains(item.Id) || file.Parents.Contains(inventory.FolderId, StringComparer.Ordinal));
            if (!relevant)
            {
                // Inherited permissions may change through an ancestor without child
                // entries. Conservatively require a full refetch for any folder in
                // this corpus; do not invent a permanent recovery verdict for it.
                if (observeFolderChanges && file?.MimeType == "application/vnd.google-apps.folder")
                {
                    var folderBytes = new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(item));
                    events.Add(new(item.Kind, item.Id, false, Convert.ToHexStringLower(SHA256.HashData(folderBytes)))); changed = true;
                }
                continue;
            }
            var recovery = item.Kind == "drive" || item.Id == inventory.FolderId || item.Removed || file is null || file.Trashed ||
                !file.Parents.Contains(inventory.FolderId, StringComparer.Ordinal) || file.CanDownload == false || file.Bytes is null or < 1 ||
                file.MimeType is not ("application/json" or "application/gzip" or "application/octet-stream");
            if (!recovery)
            {
                var candidate = new DriveWorkspaceDocumentCandidate(item.Id, file!.Version, file.Bytes!.Value, file.MimeType);
                recovery = candidate.Bytes > WorkspaceDocumentInventoryReader.CandidateByteLimit(candidate.MimeType, includeBinaryMetadata: true);
                if (!recovery && present.TryGetValue(item.Id, out var actual))
                {
                    recovery = candidate.Bytes != actual.Bytes || candidate.MimeType != actual.MimeType;
                    if (!recovery)
                    {
                        changed |= candidate.Version > actual.Version;
                        if (candidate.Version < actual.Version) candidate = actual; // Replaying old events cannot rewind a current candidate.
                    }
                }
                else if (!recovery) changed = true;
                if (!recovery && (!candidates.TryGetValue(item.Id, out var prior) || prior.Version < candidate.Version)) candidates[item.Id] = candidate;
            }
            var bytes = new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(item));
            events.Add(new(item.Kind, item.Id, recovery, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        return new(cursor, next, page.AtEnd is not null, events.AsReadOnly(), candidates.Values.ToList().AsReadOnly(), changed);
    }

    internal static void ValidateChange(DriveObservedChange item)
    {
        if (item.Kind is not ("file" or "drive") || !Identity(item.Id) || item.Id == "root") throw new InvalidDataException("History replay returned an invalid observed identity.");
        if (item.File is { } file && (item.Kind != "file" || file.Version < 1 || file.Bytes < 0 || file.MimeType is not { Length: > 0 and <= 256 } ||
            file.Parents.Count > 16 || file.Parents.Any(parent => !Identity(parent)) || file.Parents.Distinct(StringComparer.Ordinal).Count() != file.Parents.Count))
            throw new InvalidDataException("History replay returned malformed file metadata.");
    }
    private static void CheckCursor(DriveChangeCursor cursor, WorkspaceDocumentInventory inventory)
    {
        if (cursor.AccountId != inventory.AccountId || cursor.SharedDriveId != inventory.SharedDriveId || cursor.Token is not { Length: > 0 and <= 4096 } || cursor.Token.Any(char.IsControl))
            throw new InvalidDataException("History replay changed its account, drive or opaque cursor.");
    }
    private static bool Identity(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
