using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

/// <summary>Joined full-scan owner. Persists exact read identities before invoking the
/// caller's signature/history/policy verifier. Persists a new incomplete scan fence
/// before network work and all captured candidate identities before downloads. Records
/// a cursor and exact completed attempt atomically only after every
/// document and the final Drive change check succeed. Stored cursors never skip startup
/// reconciliation. A checkpoint is evidence, not publication or catalog-head admission.</summary>
public sealed class WorkspaceObservationService(ProcessorStateCoordinator state, StorageBudgetManager budget,
    WorkspaceObservationContext context, IWorkspaceDocumentSource source) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly WorkspaceObservationContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly IWorkspaceDocumentSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }

    public async Task<WorkspaceInventoryCheckpoint> ScanAsync(Func<DriveWorkspaceObject, CancellationToken, Task> verifyAndPersist,
        CancellationToken cancellationToken, Func<CancellationToken, Task>? inspectBeforeFinalChangeCheck = null)
    {
        ArgumentNullException.ThrowIfNull(verifyAndPersist); CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this); var token = linked.Token;
                var attempt = await _state.ExecuteAsync(database => new WorkspaceObservationRepository(database, _budget).BeginScan(_context), token).ConfigureAwait(false);
                ThrowIfCanceled(cancellationToken, token);
                var known = await _state.ExecuteAsync(database => new WorkspaceObservationRepository(database, _budget).KnownIdentities(_context), token).ConfigureAwait(false);
                var publications = await _state.ExecuteAsync(database => new WorkspacePublicationRepository(database, _budget).Inventory(_context), token).ConfigureAwait(false);
                var expectedFiles = publications.Objects.Where(item => item.FileId is not null).ToDictionary(item => item.FileId!, StringComparer.Ordinal);
                var relevant = known.Union(expectedFiles.Keys, StringComparer.Ordinal).ToArray();
                var progress = new InventoryProgress(this, attempt, cancellationToken, token);
                var inventory = await _source.CaptureAsync(relevant, progress, token).ConfigureAwait(false);
                ThrowIfCanceled(cancellationToken, token);
                attempt = progress.CapturedAttempt();
                if (inventory.AccountId != _context.AccountId || inventory.FolderId != _context.FolderId || inventory.SharedDriveId != _context.SharedDriveId ||
                    inventory.Documents.Count > WorkspaceObservationRepository.MaximumDocuments || inventory.Documents.Select(document => document.FileId).Distinct(StringComparer.Ordinal).Count() != inventory.Documents.Count)
                    throw new InvalidDataException("Actual workspace inventory changed its captured context or identity bound.");
                foreach (var candidate in inventory.Documents)
                {
                    if (candidate.FileId is not { Length: > 0 and <= 128 } || candidate.FileId is "root" || candidate.FileId == _context.FolderId ||
                        candidate.FileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
                        candidate.Version < 1 || candidate.Bytes < 1 || candidate.Bytes > WorkspaceDocumentInventoryReader.CandidateByteLimit(candidate.MimeType, includeBinaryMetadata: true))
                        throw new InvalidDataException("Captured metadata document exceeds its identity or byte bound before reading.");
                }
                attempt = await _state.ExecuteAsync(database => new WorkspaceObservationRepository(database, _budget).RememberCandidates(attempt,
                    inventory.Documents.Select(CandidateObservation).ToArray()), token).ConfigureAwait(false);
                ThrowIfCanceled(cancellationToken, token);
                var observed = new List<WorkspaceDocumentObservation>();
                foreach (var candidate in inventory.Documents)
                {
                    ThrowIfCanceled(cancellationToken, token); var document = await _source.ReadAsync(candidate, token).ConfigureAwait(false);
                    ThrowIfCanceled(cancellationToken, token);
                    if (document.AccountId != _context.AccountId || document.FolderId != _context.FolderId || document.FileId != candidate.FileId ||
                        document.DriveVersion != candidate.Version || document.Bytes.Length != candidate.Bytes || document.Bytes.Length < 1 ||
                        document.Bytes.Length > WorkspaceDocumentInventoryReader.CandidateByteLimit(candidate.MimeType, includeBinaryMetadata: true))
                        throw new InvalidDataException("Actual document read substituted its captured identity, revision or size.");
                    var observation = new WorkspaceDocumentObservation(document.FileId, document.DriveVersion, document.Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(document.Bytes.Span)), StoredMime(candidate.MimeType));
                    await _state.ExecuteAsync(database => new WorkspaceObservationRepository(database, _budget).RememberRead(_context, observation), token).ConfigureAwait(false);
                    ThrowIfCanceled(cancellationToken, token);
                    if (expectedFiles.TryGetValue(document.FileId, out var expected) && (expected.Bytes != observation.Bytes || expected.Sha256 != observation.Sha256 ||
                        expected.MimeType != candidate.MimeType || expected.VerifiedVersion is { } version && observation.Version < version))
                        throw new WorkspaceObservationConflictException();
                    // If persistence fails, the verifier never receives this document.
                    // Its policy writes also must succeed before this scan can complete.
                    await verifyAndPersist(document, token).ConfigureAwait(false); ThrowIfCanceled(cancellationToken, token); observed.Add(observation);
                }
                var present = inventory.Documents.Select(item => item.FileId).ToHashSet(StringComparer.Ordinal);
                if (inventory.MissingKnownIds.Any(id => known.Contains(id, StringComparer.Ordinal) || !expectedFiles.TryGetValue(id, out var expected) || expected.VerifiedVersion is not null) ||
                    expectedFiles.Values.Any(expected => expected.VerifiedVersion is not null && !present.Contains(expected.FileId!)))
                    throw new WorkspaceObservationConflictException();
                // Extra folder/access checks must finish before the last change fence,
                // not open an unchecked network interval after that fence/checkpoint.
                if (inspectBeforeFinalChangeCheck is not null)
                {
                    ThrowIfCanceled(cancellationToken, token);
                    await inspectBeforeFinalChangeCheck(token).ConfigureAwait(false);
                    ThrowIfCanceled(cancellationToken, token);
                }
                var cursor = await _source.CheckUnchangedAsync(inventory, progress, token).ConfigureAwait(false);
                if (cursor.AccountId != _context.AccountId || cursor.SharedDriveId != _context.SharedDriveId) throw new InvalidDataException("The final change cursor belongs to another workspace access context.");
                ThrowIfCanceled(cancellationToken, token);
                attempt = progress.CapturedAttempt();
                var checkpoint = await _state.ExecuteAsync(database => new WorkspaceObservationRepository(database, _budget).CompleteScan(attempt, cursor.Token, observed, publications), token).ConfigureAwait(false);
                lock (_gate) { ThrowIfCanceled(cancellationToken, token); _failure = null; return checkpoint; }
            }
            catch (Exception error) { lock (_gate) _failure = error; throw; }
            finally { _serial.Release(); }
        }
    }

    private sealed class InventoryProgress(WorkspaceObservationService owner, WorkspaceObservationAttempt attempt, CancellationToken caller,
        CancellationToken run) : IWorkspaceInventoryObserver
    {
        private WorkspaceObservationAttempt _attempt = attempt;
        public async Task StartedAsync(DriveChangeCursor cursor, CancellationToken token)
        {
            owner.ThrowIfCanceled(caller, run); token.ThrowIfCancellationRequested();
            if (cursor.AccountId != owner._context.AccountId || cursor.SharedDriveId != owner._context.SharedDriveId)
                throw new InvalidDataException("Inventory progress substituted its captured account or drive.");
            _attempt = await owner._state.ExecuteAsync(database => new WorkspaceObservationRepository(database, owner._budget).StartInventory(_attempt, cursor.Token), token).ConfigureAwait(false);
            owner.ThrowIfCanceled(caller, run);
        }
        public async Task PageAsync(IReadOnlyCollection<DriveWorkspaceDocumentCandidate> candidates, CancellationToken token)
        {
            owner.ThrowIfCanceled(caller, run); token.ThrowIfCancellationRequested();
            if (_attempt.InventoryStartCursor is null || candidates.Count > 100) throw new InvalidDataException("Inventory page lost its persisted bounded start.");
            _attempt = await owner._state.ExecuteAsync(database => new WorkspaceObservationRepository(database, owner._budget).RememberCandidates(_attempt,
                candidates.Select(CandidateObservation).ToArray()), token).ConfigureAwait(false);
            owner.ThrowIfCanceled(caller, run);
        }
        public WorkspaceObservationAttempt CapturedAttempt() => _attempt.InventoryStartCursor is not null ? _attempt :
            throw new InvalidOperationException("The owned inventory source omitted durable start/page observation.");
    }
    private static string? StoredMime(string mimeType) => mimeType == "application/json" ? null : mimeType;
    private static WorkspaceCandidateObservation CandidateObservation(DriveWorkspaceDocumentCandidate candidate) => new(candidate.FileId, candidate.Version, candidate.Bytes, StoredMime(candidate.MimeType));
    private void ThrowIfCanceled(CancellationToken caller, CancellationToken linked)
    {
        lock (_gate)
            if (_stopping || caller.IsCancellationRequested || linked.IsCancellationRequested) throw new OperationCanceledException("Workspace observation was retired before result admission.", linked);
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_disposal is { IsFaulted: true } && !_exitConfirmed) _disposal = null;
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        Exception? cancellationFailure = null;
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { cancellationFailure = error; }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cancellationFailure is not null) throw new AggregateException("Workspace observation cancellation needs attention; retain its account/state owners.", cancellationFailure);
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
}
