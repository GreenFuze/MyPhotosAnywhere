using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCatalogReconciliationService
{
    private void AssertReadProof(ProcessorStateDatabase database, CompletedRead proof, CancellationToken token, bool allowPendingSignatureRecovery = false)
    {
        _ = CurrentRead(proof.Result, token);
        if (proof.Captured.Recovery is not null && !allowPendingSignatureRecovery) throw new CatalogGenerationCaptureConflictException();
        var authority = new LibraryAuthorityRepository(database, _budget);
        if (authority.Controller() != proof.Captured.Pin || authority.CaptureLocalPublicationAuthority() != proof.Captured.Authority ||
            _keys.PublicIdentity != proof.Captured.Native) throw new StaleLibraryConfigurationException();
        _keys.AssertPublicationIdentity(proof.Captured.Pin, proof.Captured.Authority);
        new ProtocolSigningJournal(database, _budget).AssertInventoryUnchanged(proof.Captured.Signing);
        new WorkspaceObservationRepository(database, _budget).AssertCompletedScan(proof.Result.Observation, proof.AttemptId);
    }

    // These entry points own their actual reads. Neither a caller's summary nor a
    // previously returned record can choose identities or checkpoint a prepared graph.
    internal async Task<CatalogGenerationCapture> BeginCurrentGenerationAsync(CancellationToken token)
    {
        await EnsureActiveGenerationAccessAsync(token).ConfigureAwait(false);
        var read = await ReadCurrentAsync(token).ConfigureAwait(false);
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proof = CurrentRead(read, token);
            return await _state.ExecuteAsync(database =>
            {
                AssertReadProof(database, proof, token);
                new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                var journal = new CatalogGenerationCaptureRepository(database, _budget); var retained = journal.Active().SingleOrDefault();
                if (read.PendingSigning.Count != 0 || read.PendingPublications.Any(id =>
                    retained is null || proof.Publications.Objects.Single(item => item.Id == id).Scope != retained.Scope))
                    throw new CatalogGenerationInProgressException();
                if (retained is not null)
                {
                    AssertLocalGenerationScope(database, retained.Scope, token);
                    if (retained.Phase == "publication-planned") _ = AssertGeneration(database, proof, retained.Scope, token);
                    return retained;
                }
                var local = proof.Captured.Authority;
                var scope = new WorkspacePublicationScope(local.LibraryId, local.ActorId, local.ActorEpoch, Guid.NewGuid(), local.AccountId,
                    local.AccountRevision, local.FolderId, local.ConfigurationRevision, local.AuthoritySha256);
                return journal.Begin(scope, Guid.NewGuid(), Guid.NewGuid());
            }, token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    internal async Task<(CapturedCatalogPublication Publication, AcceptedSourceConfiguration Sources)> CaptureCurrentBasisAsync(Guid generationId, CancellationToken token)
    {
        await EnsureGenerationAccessAsync(generationId, token).ConfigureAwait(false);
        var read = await ReadCurrentAsync(token).ConfigureAwait(false);
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proof = CurrentRead(read, token);
            return await _state.ExecuteAsync(database =>
            {
                AssertReadProof(database, proof, token);
                new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                var journal = new CatalogGenerationCaptureRepository(database, _budget);
                var capture = journal.Read(generationId) ?? throw new CatalogGenerationCaptureConflictException();
                AssertLocalGenerationScope(database, capture.Scope, token);
                if (read.PendingSigning.Count != 0 || read.PendingPublications.Any(id => proof.Publications.Objects.Single(item => item.Id == id).Scope != capture.Scope))
                    throw new CatalogGenerationInProgressException();
                var publication = journal.Publication(generationId);
                var sources = new LibraryAuthorityRepository(database).CurrentSourceConfiguration() ?? throw new CatalogPublicationNotAuthorizedException();
                if (publication is null)
                {
                    if (capture.Snapshot is null || capture.Phase != "frozen") throw new CatalogGenerationCaptureConflictException();
                    publication = journal.CapturePublicationBasis(generationId, new(capture.Snapshot, read.Parent, read.Sequence, sources.EnvelopeSha256, 0, 1), LocalAccessForScope(capture.Scope));
                }
                if (publication.Basis.SourcePolicySha256 != sources.EnvelopeSha256) throw new CatalogGenerationCaptureConflictException();
                if (capture.Phase == "publication-planned") _ = AssertGeneration(database, proof, capture.Scope, token);
                return (publication, sources);
            }, token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    internal async Task<CapturedCatalogPublication> SealCurrentDependenciesAsync(Guid generationId, CapturedCatalogCommitPlan plan, CancellationToken token)
    {
        await EnsureGenerationAccessAsync(generationId, token).ConfigureAwait(false);
        ReconciledLocalCatalog read;
        while (true)
        {
            try { read = await ReconcileAsync(plan, token).ConfigureAwait(false); break; }
            catch (WorkspaceHistoryReplayPendingException) { token.ThrowIfCancellationRequested(); }
        }
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proof = CurrentRead(read, token);
            return await _state.ExecuteAsync(database =>
            {
                if (proof.Prepared != plan) throw new WorkspaceObservationConflictException();
                new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                var generation = new CatalogGenerationCaptureRepository(database, _budget).Publication(generationId)
                    ?? throw new CatalogGenerationCaptureConflictException();
                _ = AssertGeneration(database, proof, generation.Capture.Scope, token, allowPrepared: true);
                return new CatalogGenerationCaptureRepository(database, _budget).SealDependencies(generationId, plan, LocalAccessForScope(generation.Capture.Scope));
            }, token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    private sealed partial class Scan
    {
        public async Task VerifyPreparedAsync(CapturedCatalogCommitPlan plan, CancellationToken token)
        {
            var (generation, approved) = await owner._state.ExecuteAsync(database =>
            {
                var active = new CatalogGenerationCaptureRepository(database, owner._budget).Active().SingleOrDefault()
                    ?? throw new CatalogGenerationCaptureConflictException();
                var publication = new CatalogGenerationCaptureRepository(database, owner._budget).Publication(active.Scope.GenerationId)
                    ?? throw new CatalogGenerationCaptureConflictException();
                owner.AssertLocalGenerationScope(database, active.Scope, token);
                if (!(active.Phase == "publication-planned" && publication.Plan is null || active.Phase is "dependencies-ready" or "commit-sealed" or "commit-verified" or "complete" && publication.Plan == plan) ||
                    publication.Basis != plan.Basis)
                    throw new CatalogGenerationCaptureConflictException();
                var sources = new LibraryAuthorityRepository(database).CurrentSourceConfiguration() ?? throw new CatalogPublicationNotAuthorizedException();
                if (sources.EnvelopeSha256 != plan.Basis.SourcePolicySha256) throw new StaleLibraryConfigurationException();
                return (active, sources);
            }, token).ConfigureAwait(false);
            if (!_sourcePolicies.TryGetValue(generation.Scope.ConfigurationRevision, out var policy) || policy.Sha256 != plan.Basis.SourcePolicySha256)
                throw new SigningHistoryRecoveryRequiredException();
            var candidate = new CatalogCommit(generation.Scope.GenerationId, plan.Basis.Parent, plan.RootIndex, plan.SourceCoverage,
                plan.Basis.SettingsRevision, plan.Basis.MinimumHistoryGeneration);
            var source = new PreparedObjectSource(this, generation.Scope, publications);
            long assets = 0;
            await foreach (var leaf in new CatalogFactsTraversal(context.LibraryId, new(context.AccountId, context.FolderId), source,
                WorkspaceObservationRepository.MaximumDocuments).ReadPreparedLeavesAsync(candidate, checked(plan.Basis.ParentSequence + 1), (coverage, ownedToken) =>
                {
                    ownedToken.ThrowIfCancellationRequested(); approved.Configuration.ValidateCoverage(coverage);
                    if (coverage.Root.ConfigurationRevision != plan.Basis.Snapshot.ConfigurationRevision || coverage.Root.DiscoveryRevision != plan.Basis.Snapshot.DiscoveryRevision)
                        throw new CatalogGenerationCaptureConflictException();
                    return Task.CompletedTask;
                }, token).ConfigureAwait(false)) assets = checked(assets + leaf.Facts.Assets.Count);
            if (assets != plan.Basis.Snapshot.Assets) throw new CatalogGenerationCaptureConflictException();
        }

        private sealed class PreparedObjectSource(Scan scan, WorkspacePublicationScope scope, WorkspacePublicationInventory inventory) : ICatalogObjectSource
        {
            public Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken)
            {
                var work = inventory.Objects.SingleOrDefault(item => item.FileId == reference.FileId);
                if (work is null || work.Scope != scope || work.Bytes != reference.Bytes || work.Sha256 != reference.Sha256 || work.VerifiedVersion is null ||
                    work.Role is not ("facts-index" or "facts-chunk" or "coverage-root" or "coverage-page") ||
                    !scan._observed.TryGetValue(reference.FileId, out var actual) || (actual.MimeType ?? "application/json") != work.MimeType)
                    throw new WorkspaceObservationConflictException();
                return scan.ReadAsync(reference, cancellationToken);
            }
        }
    }
}
