using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Joined serial owner of actual generation capture and dependency preparation.
/// Borrows current reconciliation, state and native keys; owns its retained-reader service.
/// Every retry resumes the journal's original identities and frozen facts. This owner
/// stops at dependencies-ready; signing/commit publication/activation remain separate.
/// Retire this service before retiring any borrowed owner.</summary>
public sealed class CatalogGenerationPreparationService : IAsyncDisposable
{
    private readonly LocalCatalogReconciliationService _current;
    private readonly CatalogGenerationCaptureService _capture;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;

    public CatalogGenerationPreparationService(ProcessorStateCoordinator state, string stateDirectory, Guid installationId,
        StorageBudgetManager budget, LocalCatalogReconciliationService current, LibrarySigningIdentityOwner keys)
    {
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _capture = new(state, stateDirectory, installationId, budget, new LocalCapturePermission(state, keys, budget));
    }
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }

    public async Task<CapturedCatalogPublication> PrepareAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            var token = linked.Token; await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Check(token); var claim = await _current.BeginCurrentGenerationAsync(token).ConfigureAwait(false);
                var result = await _capture.RunAsync(claim.Scope, claim.SnapshotId, claim.CommitIntentId, async (_, frozen, ownedToken) =>
                {
                    var captured = await _current.CaptureCurrentBasisAsync(claim.Scope.GenerationId, ownedToken).ConfigureAwait(false);
                    if (captured.Publication.Plan is { } retainedPlan)
                        return await _current.SealCurrentDependenciesAsync(claim.Scope.GenerationId, retainedPlan, ownedToken).ConfigureAwait(false);
                    var basis = captured.Publication.Basis;
                    if (basis.Snapshot.Assets != frozen.Count || basis.Snapshot.DiscoveryRevision != frozen.DiscoveryRevision ||
                        basis.Snapshot.ConfigurationRevision != frozen.ConfigurationRevision || basis.Snapshot.LibraryId != frozen.LibraryId)
                        throw new CatalogGenerationCaptureConflictException();
                    var prepared = await new CatalogGenerationDependencyBuilder(new(claim.Scope.AccountId, claim.Scope.FolderId),
                        new ReconciledCatalogGenerationSink(_current, claim.Scope), WorkspaceObservationRepository.MaximumDocuments)
                        .BuildAsync(frozen, captured.Sources, basis.Parent, ownedToken).ConfigureAwait(false);
                    if (prepared.Assets != basis.Snapshot.Assets || prepared.SourcePolicySha256 != basis.SourcePolicySha256)
                        throw new CatalogGenerationCaptureConflictException();
                    return await _current.SealCurrentDependenciesAsync(claim.Scope.GenerationId, new(basis, prepared.RootIndex, prepared.SourceCoverage), ownedToken).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
                Check(token); return result;
            }
            finally { _serial.Release(); }
        }
    }

    // Private local capture permission only. Never passed to an uploader or returned
    // as a cloud grant. Actual writes use the concrete reconciler's one-object owner.
    private sealed class LocalCapturePermission(ProcessorStateCoordinator state, LibrarySigningIdentityOwner keys, StorageBudgetManager budget) : IWorkspacePublicationAuthority
    {
        public Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken cancellationToken) => state.ExecuteAsync(database =>
        {
            cancellationToken.ThrowIfCancellationRequested(); var authority = new LibraryAuthorityRepository(database, budget);
            authority.AssertLocalPublicationScope(scope);
            keys.AssertPublicationIdentity(authority.Controller() ?? throw new CatalogPublicationNotAuthorizedException(), authority.CaptureLocalPublicationAuthority());
            return true;
        }, cancellationToken);
    }
    private void Check(CancellationToken token)
    {
        lock (_gate) if (_stopping || token.IsCancellationRequested) throw new OperationCanceledException("Catalog generation preparation was retired.", token);
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
        var errors = new List<Exception>();
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        var captureStop = _capture.DisposeAsync().AsTask();
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await captureStop.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Generation preparation retirement is unconfirmed; retain borrowed owners.", errors);
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
}
