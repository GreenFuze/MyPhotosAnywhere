using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

/// <summary>One scan's private transport composition. Native byte reuse is supplied
/// only by the actual current owner; this source never grants publication authority.
/// A completed inventory is exposed only after the real final change fence joins.</summary>
internal sealed class CatalogReconciliationDocumentSource(CatalogWorkspaceDocumentSource full,
    WorkspaceDocumentInventory? baseline = null, WorkspacePublicationExpectation? expected = null,
    Func<DriveWorkspaceDocumentCandidate, CancellationToken, Task<DriveWorkspaceObject?>>? localRead = null)
    : IWorkspaceDocumentSource, IWorkspaceHistoryDocumentSource
{
    private readonly CatalogWorkspaceDocumentSource _full = full ?? throw new ArgumentNullException(nameof(full));
    public WorkspaceDocumentInventory? CompletedInventory { get; private set; }
    public bool RequiresHistoryReplay => baseline is null;

    public Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, CancellationToken token) =>
        throw new InvalidOperationException("Current reconciliation requires actual durable inventory progress.");

    public Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, IWorkspaceInventoryObserver observer, CancellationToken token) =>
        baseline is null ? _full.CaptureAsync(previouslyKnownIds, observer, token) :
        observer is IWorkspaceIncrementalInventoryObserver incremental
            ? _full.CaptureIncrementalAsync(baseline, expected, incremental, token)
            : throw new InvalidOperationException("Incremental reconciliation requires joined durable history progress.");

    public Task<WorkspaceDocumentInventory> ReplayHistoryAsync(WorkspaceDocumentInventory inventory, DriveChangeCursor from,
        IWorkspaceHistoryObserver observer, CancellationToken token) => baseline is null
        ? _full.ReplayHistoryAsync(inventory, from, observer, token)
        : throw new InvalidOperationException("The actual incremental capture already owns its complete history replay.");

    public async Task<DriveWorkspaceObject> ReadAsync(DriveWorkspaceDocumentCandidate candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (baseline is not null && localRead is not null && candidate.FileId != expected?.FileId)
        {
            var local = await localRead(candidate, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (local is not null) return local;
        }
        return await _full.ReadAsync(candidate, token).ConfigureAwait(false);
    }

    public Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, CancellationToken token) =>
        throw new InvalidOperationException("Current reconciliation requires durable rejected-fence observation.");

    public async Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, IWorkspaceInventoryObserver observer, CancellationToken token)
    {
        var cursor = await _full.CheckUnchangedAsync(inventory, observer, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        CompletedInventory = inventory with { Cursor = cursor }; return cursor;
    }
}
