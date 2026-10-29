using Microsoft.Data.Sqlite;
using Google.Apis.Auth.OAuth2.Responses;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public enum InitialLibraryPublicationStage { NeedsApproval, Running, PoliciesPublished, NeedsAttention, Stopping, Stopped }
public sealed record InitialLibraryPublicationActivity(InitialLibraryPublicationStage Stage, string? Detail, bool CanRetry);

/// <summary>Engine-owned setup work, recoverable from durable human approval and the
/// exact outbox/completion journal. Browser cancellation never owns an admitted attempt.
/// A historical completion advances this stage only; it is not a current catalog grant.
/// Stop joins the actual attempt/uploader before borrowed keys/accounts/state may retire.</summary>
public sealed class InitialLibraryPublicationRuntime(ProcessorStateCoordinator state, ProcessorAccountService accounts,
    LibrarySigningIdentityOwner keys, StorageBudgetManager budget, LocalCatalogReconciliationService? catalog = null,
    CatalogGenerationRuntime? generations = null, CurrentLibraryCatalogRuntime? currentCatalog = null) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly InitialLibraryPublicationService _publication = new(state, accounts, keys, budget);
    // Ownership transfers to this runtime; its attempt and the concrete reconciler
    // must both join before the composition root retires keys/accounts/state.
    private readonly LocalCatalogReconciliationService? _catalog = catalog;
    private readonly CatalogGenerationRuntime? _generations = generations is null || catalog is not null && generations.Borrows(catalog)
        ? generations : throw new ArgumentException("Generation work must borrow this runtime's owned current reconciler.", nameof(generations));
    private readonly CurrentLibraryCatalogRuntime? _currentCatalog = currentCatalog is null || catalog is null && generations is null && currentCatalog.Borrows(state, accounts, keys, budget)
        ? currentCatalog : throw new ArgumentException("Current engine work must own its exact state/account/key/budget bundle, with no parallel legacy reconciler.", nameof(currentCatalog));
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _attempt;
    private Task? _disposal;
    private bool _stopping;
    private bool _exitConfirmed;
    private bool _draining;
    private long _requestedVersion;
    private long _drainVersion;
    private Exception? _failure;
    private Exception? _lastAttemptFailure;
    private string? _workspaceAccountId;
    private InitialLibraryPublicationActivity _activity = new(InitialLibraryPublicationStage.NeedsApproval, null, false);
    public event Action<Exception>? Failed;
    public Exception? Failure { get { lock (_gate) return _failure ?? _currentCatalog?.Failure; } }
    public Exception? LastAttemptFailure { get { lock (_gate) return _lastAttemptFailure; } }
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public InitialLibraryPublicationActivity Activity { get { lock (_gate) return _activity; } }
    public ReconciledLocalCatalog? CurrentCatalog => _catalog?.Current;
    public CurrentCanonicalReadRuntime? CurrentCanonicalCatalog => _currentCatalog?.Catalog;
    public Task<CurrentCatalogActivation?> InitializeCurrentCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ThrowIfStopping();
        return _currentCatalog?.InitializeHistoricalAsync(cancellationToken) ?? Task.FromResult<CurrentCatalogActivation?>(null);
    }

    /// <summary>Called on engine startup and after committed workspace approval. The
    /// caller can cancel before admission; after admission only this owner's Stop can
    /// retire the job. The durable approval/outbox recovers even a lost wake-up reply.</summary>
    public InitialLibraryPublicationActivity Request(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_stopping, this);
            if (_failure is not null) throw new InvalidOperationException("Library setup stopped after an internal failure. Inspect it before restarting.", _failure);
            _requestedVersion = checked(_requestedVersion + 1);
            if (_draining && _activity.Stage != InitialLibraryPublicationStage.NeedsAttention) return _activity;
            var previous = _attempt; var drain = _drainVersion = checked(_drainVersion + 1); _draining = true;
            _activity = new(InitialLibraryPublicationStage.Running, null, false); _lastAttemptFailure = null;
            // A retry can arrive after an error is visible but just before the old
            // Task completes. Retain that request and join the old attempt first.
            _attempt = Task.Run(async () =>
            {
                if (previous is not null) await previous.ConfigureAwait(false);
                await DrainAsync(drain).ConfigureAwait(false);
            }, CancellationToken.None); return _activity;
        }
    }

    /// <summary>Committed source data/coverage changes wake owned work, never a browser
    /// task. Approval/recovery attention requires an explicit retry; ordinary progress
    /// notifications cannot silently replace that decision or lose a late wake-up.</summary>
    public void NotifyLocalCatalogChanged()
    {
        lock (_gate)
        {
            if (_stopping || _failure is not null || _activity.Stage is InitialLibraryPublicationStage.NeedsApproval or InitialLibraryPublicationStage.NeedsAttention) return;
            _ = Request(CancellationToken.None);
        }
    }

    /// <summary>A committed human Google connection is an explicit retry of access
    /// attention for this workspace, not a policy/recovery approval or cloud grant.
    /// Scheduling is synchronous and engine-owned even if its browser closes next.</summary>
    public void NotifyAccountConnected(DriveAccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            if (_stopping || _failure is not null || !account.Connected || account.PermissionId != _workspaceAccountId ||
                _activity.Stage == InitialLibraryPublicationStage.NeedsApproval) return;
            if (_activity.Stage == InitialLibraryPublicationStage.NeedsAttention && _lastAttemptFailure is not
                (TokenResponseException or CredentialVaultException or StaleDriveConnectionException or StaleLibraryConfigurationException or
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })) return;
            _ = Request(CancellationToken.None);
        }
    }

    private async Task DrainAsync(long drain)
    {
        while (true)
        {
            long requested;
            lock (_gate) { if (drain != _drainVersion) return; requested = _requestedVersion; }
            await RunAsync().ConfigureAwait(false);
            lock (_gate)
            {
                // An explicit retry can be chained after attention is visible but
                // before the previous task returns. That successor owns later wakes.
                if (drain != _drainVersion) return;
                if (!_stopping && _failure is null && _activity.Stage is not (InitialLibraryPublicationStage.NeedsApproval or InitialLibraryPublicationStage.NeedsAttention) &&
                    requested != _requestedVersion) continue;
                _draining = false; return;
            }
        }
    }

    /// <summary>Observe/join the currently admitted attempt without transferring its
    /// lifetime to the observer. Disposal always joins it without a timeout wrapper.</summary>
    public Task WaitForCurrentAttemptAsync(CancellationToken cancellationToken)
    {
        Task attempt; lock (_gate) attempt = _attempt ?? Task.CompletedTask;
        return attempt.WaitAsync(cancellationToken);
    }

    private async Task RunAsync()
    {
        try
        {
            ThrowIfStopping();
            var readiness = await _state.ExecuteAsync(database =>
            {
                ThrowIfStopping(); var authority = new LibraryAuthorityRepository(database, _budget);
                var controller = authority.Controller();
                lock (_gate) { ThrowIfStopping(); _workspaceAccountId = controller?.AccountId; }
                if (authority.InitialPublicationCompletion() is not null) return InitialLibraryPublicationStage.PoliciesPublished;
                if (controller is null || authority.BootstrapSources() is null) return InitialLibraryPublicationStage.NeedsApproval;
                authority.AssertBootstrapSourcesCurrent(); return InitialLibraryPublicationStage.Running;
            }, _stop.Token).ConfigureAwait(false);
            ThrowIfStopping();
            if (readiness == InitialLibraryPublicationStage.Running)
            {
                await _publication.PublishAsync(_stop.Token).ConfigureAwait(false); ThrowIfStopping();
                readiness = InitialLibraryPublicationStage.PoliciesPublished;
            }
            if (readiness == InitialLibraryPublicationStage.PoliciesPublished && (_catalog is not null || _currentCatalog is not null))
            {
                lock (_gate) { ThrowIfStopping(); _activity = new(InitialLibraryPublicationStage.Running, "Checking current library history.", false); }
                if (_currentCatalog is not null) await _currentCatalog.RunUntilCurrentAsync(_stop.Token).ConfigureAwait(false);
                else if (_generations is not null) await _generations.RunUntilCurrentAsync(_stop.Token).ConfigureAwait(false);
                else while (true)
                {
                    try { await (_catalog ?? throw new InvalidOperationException("The configured legacy catalog owner is missing.")).ReconcileAsync(_stop.Token).ConfigureAwait(false); break; }
                    // Each batch has committed real cursor progress. Continue the next
                    // bounded batch without involving a browser or restarting bootstrap.
                    catch (WorkspaceHistoryReplayPendingException) { ThrowIfStopping(); }
                }
                ThrowIfStopping();
            }
            lock (_gate) { ThrowIfStopping(); _activity = new(readiness, null, false); }
        }
        catch (OperationCanceledException) when (IsStopping()) { }
        catch (Exception error)
        {
            var recoverable = Recoverable(error);
            lock (_gate)
            {
                _lastAttemptFailure = error;
                if (!recoverable) _failure = error;
                if (!_stopping) _activity = new(InitialLibraryPublicationStage.NeedsAttention, Detail(error, recoverable), recoverable);
            }
            if (!recoverable) Failed?.Invoke(error);
        }
    }

    private static bool Recoverable(Exception error) => error is IOException or HttpRequestException or OperationCanceledException or ProtocolFormatException or
        TokenResponseException or CredentialVaultException or
        StaleLibraryConfigurationException or StaleDriveConnectionException or CurrentControlObservationConflictException or LibraryWorkspaceApprovalException or
        LibraryInitialPublicationConflictException or LibraryInitialPolicyConflictException or LibraryInitialPublicationCompletionMissingException or
        WorkspaceObservationConflictException or WorkspaceHistoryRecoveryRequiredException or SigningHistoryRecoveryRequiredException or WorkspaceInventoryIncompleteException or WorkspacePublicationInventoryChangedException or ProtocolSigningInventoryChangedException or InsufficientStorageBudgetException or
        CatalogPublicationNotAuthorizedException or ControllerAuthorityConflictException ||
        error is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 13 };

    private static string Detail(Exception error, bool recoverable)
    {
        if (!recoverable) return "Library setup stopped after an internal error. Its saved work has been preserved.";
        return error switch
        {
            InsufficientStorageBudgetException => "Free space on this computer, then retry library setup.",
            TokenResponseException or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "Reconnect Google Drive in Settings. Saved library work will resume after authorization.",
            CredentialVaultException => "Unlock this computer’s credential storage, then retry library setup. Saved progress has been kept.",
            StaleDriveConnectionException or StaleLibraryConfigurationException or CurrentControlObservationConflictException => "Google Drive access or the approved source settings changed. Review them before retrying.",
            LibraryWorkspaceApprovalException => "Review this workspace’s sharing permission before publishing private metadata.",
            LibraryInitialPublicationCompletionMissingException or LibraryInitialPublicationConflictException or LibraryInitialPolicyConflictException or
                WorkspaceObservationConflictException or WorkspaceHistoryRecoveryRequiredException or SigningHistoryRecoveryRequiredException or CatalogPublicationNotAuthorizedException or ControllerAuthorityConflictException or InvalidDataException or ProtocolFormatException =>
                "Saved library history needs reconciliation. Existing files and metadata have been preserved.",
            WorkspaceInventoryIncompleteException or WorkspacePublicationInventoryChangedException or ProtocolSigningInventoryChangedException => "The workspace or saved work changed during setup. Retry to check it again.",
            OperationCanceledException => "Library setup was interrupted. Saved progress is kept; retry when Google Drive is connected.",
            _ => "Google Drive or local storage could not finish library setup. Review the connection and available space, then retry."
        };
    }

    private bool IsStopping() { lock (_gate) return _stopping; }
    private void ThrowIfStopping()
    {
        lock (_gate) if (_stopping || _stop.IsCancellationRequested) throw new OperationCanceledException("Library setup runtime was retired.", _stop.Token);
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (!_exitConfirmed) _activity = new(InitialLibraryPublicationStage.Stopping, null, false);
            if (_disposal is { IsFaulted: true } && !_exitConfirmed) _disposal = null;
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        Exception? cancellationFailure = null;
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { cancellationFailure = error; }
        var publicationStop = _publication.DisposeAsync().AsTask(); Task attempt;
        var generationStop = _generations?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        lock (_gate) attempt = _attempt ?? Task.CompletedTask;
        var currentStop = _currentCatalog?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        var joined = Task.WhenAll(publicationStop, generationStop, currentStop, attempt);
        try { await joined.ConfigureAwait(false); }
        catch { if (joined.Exception is not null) throw joined.Exception; throw; }
        if (_currentCatalog is { ExitConfirmed: false }) throw new InvalidOperationException("The current library bundle has not physically joined; retain borrowed accounts, keys and state.", _currentCatalog.Failure);
        if (_catalog is not null) await _catalog.DisposeAsync().ConfigureAwait(false); // All borrowers and their actual I/O have joined first.
        if (cancellationFailure is not null) throw new AggregateException("Library setup cancellation needs attention; retain its borrowed owners.", cancellationFailure);
        _stop.Dispose(); lock (_gate) { _exitConfirmed = true; _activity = new(InitialLibraryPublicationStage.Stopped, null, false); }
    }
}
