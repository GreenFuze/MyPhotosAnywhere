using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed record PublishedInitialLibraryPolicies(LibraryInitialPublicationPlan Plan, WorkspacePublicationRecord Publisher,
    WorkspacePublicationRecord Sources, WorkspaceInventoryCheckpoint Observation, LibraryInitialPublicationCompletion Completion);

/// <summary>Joined owner for this installation's two initial policies only. Every write
/// is preceded by actual folder access and complete unfiltered child reconciliation.
/// Unknown content, newer policies, missing verified work and stale access stop setup;
/// nothing is adopted, overwritten or deleted. This neither publishes nor activates a
/// catalog, repairs an existing library, or grants authority to another producer.
/// The composition root must retire this owner before its borrowed keys/accounts/state.</summary>
public sealed class InitialLibraryPublicationService(ProcessorStateCoordinator state, ProcessorAccountService accounts,
    LibrarySigningIdentityOwner keys, StorageBudgetManager budget) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly LibrarySigningIdentityOwner _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }

    public async Task<PublishedInitialLibraryPolicies> PublishAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ThrowIfCanceled(cancellationToken, linked.Token);
                var prepared = await new InitialLibraryPublicationPreparation(_state, _keys, _budget).PrepareAsync(linked.Token).ConfigureAwait(false);
                ThrowIfCanceled(cancellationToken, linked.Token);
                var current = await ReconcileAsync(prepared.Plan, cancellationToken, linked.Token).ConfigureAwait(false);
                foreach (var id in new[] { prepared.Plan.PublisherObjectId, prepared.Plan.SourceObjectId })
                {
                    ThrowIfCanceled(cancellationToken, linked.Token);
                    var record = id == current.Work.Publisher.Id ? current.Work.Publisher : current.Work.Sources;
                    if (record.VerifiedVersion is not null) continue;
                    // This private capability is valid for one joined upload only. Its
                    // callback performs no network work inside the serial account lease.
                    using (var admission = new Admission(this, current, cancellationToken, linked.Token))
                        await new WorkspacePublicationUploader(_state, _accounts, _budget, admission).UploadAsync(id, linked.Token).ConfigureAwait(false);
                    ThrowIfCanceled(cancellationToken, linked.Token);
                    // A transport receipt is not a current observation. Re-read actual
                    // cloud contents, including the just-written object, outside the lease.
                    current = await ReconcileAsync(prepared.Plan, cancellationToken, linked.Token).ConfigureAwait(false);
                }
                if (current.Work.Publisher.VerifiedVersion is null || current.Work.Sources.VerifiedVersion is null || current.Observations.Count != 2)
                    throw new InvalidDataException("Initial publication did not retain and observe both exact verified policies.");
                var completion = await _state.ExecuteAsync(database =>
                {
                    ThrowIfCanceled(cancellationToken, linked.Token);
                    var authority = new LibraryAuthorityRepository(database, _budget);
                    _keys.AssertPublicationIdentity(authority.Controller() ?? throw new InvalidDataException("Initial completion lost its native controller."), authority.CaptureLocalPublicationAuthority());
                    return authority.CompleteInitialPublication(current.Work.Plan, current.Checkpoint);
                }, linked.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    ThrowIfCanceled(cancellationToken, linked.Token); _failure = null;
                    return new(current.Work.Plan, current.Work.Publisher, current.Work.Sources, current.Checkpoint, completion);
                }
            }
            catch (Exception error) { lock (_gate) _failure = error; throw; }
            finally { _serial.Release(); }
        }
    }

    private async Task<Reconciliation> ReconcileAsync(LibraryInitialPublicationPlan plan, CancellationToken caller, CancellationToken token)
    {
        var before = await InspectAsync(plan, caller, token).ConfigureAwait(false);
        var context = new WorkspaceObservationContext(plan.Scope.LibraryId, plan.Scope.AccountId, plan.Scope.FolderId, plan.Scope.AccountRevision, before.SharedDriveId);
        var source = new CatalogWorkspaceDocumentSource(_state, _accounts, new(plan.Scope.AccountId, plan.Scope.FolderId), before.SharedDriveId,
            plan.Scope.AccountRevision, initialPoliciesOnly: true);
        var observer = new LocalControllerPolicyObserver(_state, _budget, _keys, context);
        var work = await ReadCurrentAsync(plan, token).ConfigureAwait(false);
        var observed = new List<WorkspaceDocumentObservation>();
        await using var scan = new WorkspaceObservationService(_state, _budget, context, source);
        var checkpoint = await scan.ScanAsync(async (document, readToken) =>
        {
            ThrowIfCanceled(caller, token);
            // Persist and select independently verified local-controller history even
            // when it closes this initial-only owner. Never fall back to older permission.
            await observer.ObserveAsync(document, readToken).ConfigureAwait(false);
            ThrowIfCanceled(caller, token);
            var expected = document.FileId == work.Publisher.FileId ? work.Publisher : document.FileId == work.Sources.FileId ? work.Sources :
                throw new LibraryInitialPublicationConflictException();
            var digest = Convert.ToHexStringLower(SHA256.HashData(document.Bytes.Span));
            if (digest != expected.Sha256 || !document.Bytes.Span.SequenceEqual(expected.Payload.Span) ||
                expected.VerifiedVersion is { } verified && document.DriveVersion < verified)
                throw new LibraryInitialPublicationConflictException();
            observed.Add(new(document.FileId, document.DriveVersion, document.Bytes.Length, digest));
        }, token, async inspectionToken =>
        {
            foreach (var record in new[] { work.Publisher, work.Sources })
                if (record.VerifiedVersion is not null && !observed.Any(document => document.FileId == record.FileId))
                    throw new WorkspaceObservationConflictException(); // Includes a receipt never checkpointed by an earlier scan.
            var after = await InspectAsync(plan, caller, inspectionToken).ConfigureAwait(false);
            if (before.Access.DriveVersion != after.Access.DriveVersion || before.Access.Shared != after.Access.Shared || before.SharedDriveId != after.SharedDriveId ||
                before.Access.VerifiedEmpty != (observed.Count == 0) || after.Access.VerifiedEmpty != (observed.Count == 0))
                throw new WorkspaceInventoryIncompleteException();
        }).ConfigureAwait(false);
        ThrowIfCanceled(caller, token);
        var result = new Reconciliation(work, checkpoint, observed.OrderBy(document => document.FileId, StringComparer.Ordinal).ToArray());
        await ValidateAsync(result, caller, token).ConfigureAwait(false);
        return result;
    }

    private Task<PreparedInitialLibraryPublication> ReadCurrentAsync(LibraryInitialPublicationPlan plan, CancellationToken token) =>
        _state.ExecuteAsync(database =>
        {
            token.ThrowIfCancellationRequested();
            var authority = new LibraryAuthorityRepository(database, _budget);
            if (authority.InitialPublicationPlan() != plan || authority.PrepareInitialPublication() != plan) throw new LibraryInitialPublicationConflictException();
            authority.AssertLocalPublicationScope(plan.Scope);
            _keys.AssertPublicationIdentity(authority.Controller() ?? throw new InvalidDataException("Initial publication lost its controller."), authority.CaptureLocalPublicationAuthority());
            var outbox = new WorkspacePublicationRepository(database, _budget); var ids = outbox.Identities(2);
            if (ids.Count != 2 || !ids.Contains(plan.PublisherObjectId) || !ids.Contains(plan.SourceObjectId)) throw new LibraryInitialPublicationConflictException();
            var publisher = outbox.Read(plan.PublisherObjectId) ?? throw new InvalidDataException("The initial publisher object is missing.");
            var sources = outbox.Read(plan.SourceObjectId) ?? throw new InvalidDataException("The initial source object is missing.");
            if (publisher.Scope != plan.Scope || sources.Scope != plan.Scope || publisher.Role != "publisher-authorization" || sources.Role != "source-configuration" ||
                publisher.MimeType != "application/json" || sources.MimeType != "application/json" ||
                publisher.Sha256 != plan.PublisherEnvelopeSha256 || sources.Sha256 != plan.SourceEnvelopeSha256)
                throw new LibraryInitialPublicationConflictException();
            return new PreparedInitialLibraryPublication(plan, publisher, sources);
        }, token);

    private async Task<(DriveWorkspaceAccess Access, string? SharedDriveId)> InspectAsync(LibraryInitialPublicationPlan plan, CancellationToken caller, CancellationToken token)
    {
        var approval = await _state.ExecuteAsync(database => new LibraryAuthorityRepository(database, _budget).AssertBootstrapSourcesCurrent(), token).ConfigureAwait(false);
        var result = await _accounts.ReadDriveAsync(plan.Scope.AccountId, async (connection, revision, ownedToken) =>
        {
            ThrowIfCanceled(caller, token);
            if (revision != plan.Scope.AccountRevision) throw new StaleLibraryConfigurationException();
            if (connection is not IGoogleWorkspaceSetup setup) throw new InvalidOperationException("Initial publication requires actual workspace access inspection.");
            var access = await setup.InspectWorkspaceAsync(plan.Scope.FolderId, ownedToken).ConfigureAwait(false);
            ThrowIfCanceled(caller, token);
            var folder = await connection.ReadFolderAsync(plan.Scope.FolderId, ownedToken).ConfigureAwait(false);
            ThrowIfCanceled(caller, token);
            if (access.AccountId != plan.Scope.AccountId || access.FolderId != plan.Scope.FolderId || access.DriveVersion < 1 ||
                folder.Id != plan.Scope.FolderId || !folder.CanListChildren || folder.SharedDriveId is not null && !access.Shared)
                throw new InvalidDataException("Initial workspace access substituted its captured folder or sharing context.");
            if (access.Shared && !approval.AllowSharedMetadata)
                throw new LibraryWorkspaceApprovalException("The metadata workspace is now shared. Review its metadata-sharing permission before publishing.");
            return (Access: access, folder.SharedDriveId);
        }, token).ConfigureAwait(false);
        ThrowIfCanceled(caller, token); return result;
    }

    private async Task ValidateAsync(Reconciliation reconciled, CancellationToken caller, CancellationToken token)
    {
        ThrowIfCanceled(caller, token);
        var current = await ReadCurrentAsync(reconciled.Work.Plan, token).ConfigureAwait(false);
        if (current.Plan != reconciled.Work.Plan) throw new LibraryInitialPublicationConflictException();
        await _state.ExecuteAsync(database =>
        {
            ThrowIfCanceled(caller, token);
            var observations = new WorkspaceObservationRepository(database, _budget);
            if (observations.LastCheckpoint() != reconciled.Checkpoint || !observations.Known(reconciled.Checkpoint.Context).SequenceEqual(reconciled.Observations))
                throw new WorkspaceObservationConflictException();
            return true;
        }, token).ConfigureAwait(false);
        ThrowIfCanceled(caller, token);
    }

    private sealed record Reconciliation(PreparedInitialLibraryPublication Work, WorkspaceInventoryCheckpoint Checkpoint, IReadOnlyList<WorkspaceDocumentObservation> Observations);

    private sealed class Admission(InitialLibraryPublicationService owner, Reconciliation reconciled, CancellationToken caller, CancellationToken run) : IWorkspacePublicationAuthority, IDisposable
    {
        private bool _retired;
        public async Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken cancellationToken)
        {
            lock (owner._gate) ObjectDisposedException.ThrowIf(_retired, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (scope != reconciled.Work.Plan.Scope) throw new LibraryInitialPublicationConflictException();
            await owner.ValidateAsync(reconciled, caller, run).ConfigureAwait(false);
            lock (owner._gate) ObjectDisposedException.ThrowIf(_retired, this);
            cancellationToken.ThrowIfCancellationRequested();
        }
        public void Dispose() { lock (owner._gate) _retired = true; }
    }

    private void ThrowIfCanceled(CancellationToken caller, CancellationToken linked)
    {
        lock (_gate)
            if (_stopping || caller.IsCancellationRequested || linked.IsCancellationRequested)
                throw new OperationCanceledException("Initial publication was retired before result admission.", linked);
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
            if (cancellationFailure is not null) throw new AggregateException("Initial publication cancellation needs attention; retain its borrowed owners.", cancellationFailure);
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
}
