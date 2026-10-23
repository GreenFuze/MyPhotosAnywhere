using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Each dependency requires its own actual current reconciliation and private
/// one-upload admission. A caller cannot supply a public read record as authority.
/// The borrowed reconciler owns and joins every request, including cancellation/Exit.</summary>
public sealed class ReconciledCatalogGenerationSink(LocalCatalogReconciliationService owner, WorkspacePublicationScope scope) : ICatalogGenerationObjectSink
{
    private readonly LocalCatalogReconciliationService _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    private readonly WorkspacePublicationScope _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    public Task<CatalogObjectReference> StoreAsync(string role, string mimeType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        _owner.StoreGenerationDependencyAsync(_scope, role, mimeType, payload, cancellationToken);
}

public sealed partial class LocalCatalogReconciliationService
{
    private CompletedRead? _completedRead;
    private sealed record CompletedRead(ReconciledLocalCatalog Result, Captured Captured, WorkspacePublicationInventory Publications,
        CapturedCatalogCommitPlan? Prepared = null);

    internal async Task<CatalogObjectReference> StoreGenerationDependencyAsync(WorkspacePublicationScope scope, string role, string mime,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (role == "facts-chunk" ? mime != "application/gzip" : role is not ("facts-index" or "coverage-page" or "coverage-root") || mime != "application/json")
            throw new ArgumentException("Only catalog dependency roles may use current generation admission.");
        if (payload.Length is < 1 or > WorkspacePublicationRepository.MaximumPayloadBytes || mime == "application/json" && payload.Length > ProtocolDocumentReader.MaximumDocumentBytes)
            throw new ArgumentException("The captured dependency exceeds its exact role's bound.", nameof(payload));
        // Own caller bytes before ANY await, including the initial network read.
        var owned = payload.ToArray();
        if (mime == "application/json" && !new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(owned).AsSpan().SequenceEqual(owned))
            throw new ProtocolFormatException("Dependency JSON must be canonical before current publication admission.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(owned));
        var id = CatalogGenerationWorkspaceSink.ObjectId(scope, role, mime, hash);
        CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            var token = linked.Token; var current = await ReadCurrentAsync(token).ConfigureAwait(false);
            await _serial.WaitAsync(token).ConfigureAwait(false);
            CatalogObjectReference reference; SingleUploadAdmission? grant = null;
            try
            {
                var proof = CurrentRead(current, token);
                var generation = await _state.ExecuteAsync(database => AssertGeneration(database, proof, scope, token), token).ConfigureAwait(false);
                var expected = new WorkspacePublicationExpectation(id, scope, role, mime, owned.Length, hash, null, null);
                grant = new(this, proof, generation, expected);
                // The inner uploader also validates inside its serial account lease.
                // This private grant performs LOCAL checks only; no account reentry.
                reference = await new CatalogGenerationWorkspaceSink(_state, _accounts, _budget, grant, scope)
                    .StoreAsync(role, mime, owned, token).ConfigureAwait(false);
            }
            catch (Exception error) { lock (_gate) _failure = error; throw; }
            finally
            {
                grant?.Retire(); lock (_gate) { _current = null; _completedRead = null; }
                _serial.Release();
            }
            // An earlier upload receipt is insufficient even for an identical retry.
            // Retire the write grant before reacquiring accounts for this actual read.
            current = await ReadCurrentAsync(token).ConfigureAwait(false);
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var proof = CurrentRead(current, token);
                await _state.ExecuteAsync(database =>
                {
                    _ = AssertGeneration(database, proof, scope, token);
                    var actual = new WorkspaceObservationRepository(database, _budget).Known(proof.Result.Observation.Context)
                        .SingleOrDefault(item => item.FileId == reference.FileId);
                    if (actual is null || actual.Bytes != reference.Bytes || actual.Sha256 != reference.Sha256 || (actual.MimeType ?? "application/json") != mime)
                        throw new WorkspaceObservationConflictException();
                    return true;
                }, token).ConfigureAwait(false);
                Check(token); return reference;
            }
            catch (Exception error) { lock (_gate) { _failure = error; _current = null; _completedRead = null; } throw; }
            finally { _serial.Release(); }
        }
    }

    private async Task<ReconciledLocalCatalog> ReadCurrentAsync(CancellationToken token)
    {
        while (true)
        {
            try { return await ReconcileAsync(token).ConfigureAwait(false); }
            catch (WorkspaceHistoryReplayPendingException) { token.ThrowIfCancellationRequested(); } // Each bounded replay batch is durably committed.
        }
    }
    private CompletedRead CurrentRead(ReconciledLocalCatalog current, CancellationToken token)
    {
        lock (_gate)
        {
            Check(token);
            return _completedRead is { } proof && ReferenceEquals(proof.Result, current) && ReferenceEquals(_current, current)
                ? proof : throw new WorkspaceObservationConflictException();
        }
    }
    private CapturedCatalogPublication AssertGeneration(ProcessorStateDatabase database, CompletedRead proof, WorkspacePublicationScope scope, CancellationToken token, bool allowPrepared = false)
    {
        AssertReadProof(database, proof, token);
        new LibraryAuthorityRepository(database, _budget).AssertLocalPublicationScope(scope);
        var observations = new WorkspaceObservationRepository(database, _budget); observations.AssertCompletedScan(proof.Result.Observation);
        var generation = new CatalogGenerationCaptureRepository(database, _budget).Publication(scope.GenerationId)
            ?? throw new CatalogGenerationCaptureConflictException();
        if (generation.Capture.Scope != scope || !(generation.Capture.Phase == "publication-planned" && generation.Plan is null ||
            allowPrepared && generation.Capture.Phase == "dependencies-ready" && generation.Plan is not null) ||
            generation.Basis.ParentSequence != proof.Result.Sequence || proof.Result.PendingSigning.Count != 0)
            throw new CatalogGenerationCaptureConflictException();
        if (proof.Result.PendingPublications.Any(id => proof.Publications.Objects.Single(item => item.Id == id).Scope != scope))
            throw new CatalogGenerationInProgressException();
        var parent = generation.Basis.Parent; var selected = proof.Result.Parent;
        if (parent is null ? selected is not null : selected is null || parent.AccountId != selected.AccountId || parent.WorkspaceId != selected.WorkspaceId ||
            parent.Bytes != selected.Bytes || parent.Sha256 != selected.Sha256)
            throw new CatalogGenerationCaptureConflictException();
        if (parent is not null && !observations.Known(proof.Result.Observation.Context).Any(item => item.FileId == parent.FileId && item.Bytes == parent.Bytes &&
                item.Sha256 == parent.Sha256 && (item.MimeType ?? "application/json") == "application/json"))
            throw new WorkspaceObservationConflictException(); // Retain the original parent ID even if identical envelope copies exist.
        return generation;
    }

    /// <summary>Constructed only inside a real completed read while this owner holds its
    /// serial admission. Never returned to callers or reused for another upload.</summary>
    private sealed class SingleUploadAdmission : IWorkspacePublicationAuthority
    {
        private readonly LocalCatalogReconciliationService _owner;
        private readonly CompletedRead _proof;
        private readonly CapturedCatalogPublication _generation;
        private readonly WorkspacePublicationExpectation _expected;
        private bool _retired;
        private bool _seen;
        private string? _file;
        private long? _version;

        public SingleUploadAdmission(LocalCatalogReconciliationService owner, CompletedRead proof, CapturedCatalogPublication generation, WorkspacePublicationExpectation expected)
        {
            _owner = owner; _proof = proof; _generation = generation; _expected = expected;
            var old = proof.Publications.Objects.SingleOrDefault(item => item.Id == expected.Id);
            if (old is null && proof.Publications.Objects.Count == proof.Publications.Maximum) throw new CatalogTraversalLimitException();
            if (old is not null) { AssertIdentity(old); _seen = true; _file = old.FileId; _version = old.VerifiedVersion; }
        }
        public void Retire() { lock (_owner._gate) _retired = true; }
        public Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken cancellationToken) =>
            _owner._state.ExecuteAsync(database =>
            {
                lock (_owner._gate) { _owner.Check(cancellationToken); if (_retired || scope != _expected.Scope) throw new WorkspacePublicationConflictException(); }
                var generation = _expected.Role == "catalog-commit"
                    ? _owner.AssertCommitGeneration(database, _proof, scope, cancellationToken)
                    : _owner.AssertGeneration(database, _proof, scope, cancellationToken);
                if (generation != _generation) throw new CatalogGenerationCaptureConflictException();
                var actual = new WorkspacePublicationRepository(database, _owner._budget).Inventory(_proof.Publications.Context, _proof.Publications.Maximum);
                var others = _proof.Publications.Objects.Where(item => item.Id != _expected.Id).ToDictionary(item => item.Id);
                foreach (var item in actual.Objects.Where(item => item.Id != _expected.Id))
                    if (!others.Remove(item.Id, out var before) || before != item) throw new WorkspacePublicationInventoryChangedException();
                if (others.Count != 0) throw new WorkspacePublicationInventoryChangedException();
                var current = actual.Objects.SingleOrDefault(item => item.Id == _expected.Id);
                if (current is null)
                {
                    if (_seen) throw new WorkspacePublicationInventoryChangedException();
                }
                else
                {
                    AssertIdentity(current);
                    if (_file is not null && current.FileId != _file || _version is not null && current.VerifiedVersion != _version)
                        throw new WorkspacePublicationInventoryChangedException();
                    _seen = true; _file = current.FileId; _version = current.VerifiedVersion;
                }
                return true;
            }, cancellationToken);

        private void AssertIdentity(WorkspacePublicationExpectation actual)
        {
            if (actual.Id != _expected.Id || actual.Scope != _expected.Scope || actual.Role != _expected.Role || actual.MimeType != _expected.MimeType ||
                actual.Bytes != _expected.Bytes || actual.Sha256 != _expected.Sha256) throw new WorkspacePublicationConflictException();
        }
    }
}
