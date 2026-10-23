using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCatalogReconciliationService
{
    /// <summary>Publishes only the original captured signed commit. The return records
    /// a current verified cloud read, not completed snapshot retirement or activation.
    /// No caller-supplied body, scope, file ID or public read record grants this write.</summary>
    public async Task<CatalogObjectReference> PublishCapturedCommitAsync(Guid generationId, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            try { return await PublishCapturedCommitCoreAsync(generationId, linked.Token).ConfigureAwait(false); }
            catch (Exception error) { lock (_gate) { _failure = error; _current = null; _completedRead = null; } throw; }
        }
    }

    private async Task<CatalogObjectReference> PublishCapturedCommitCoreAsync(Guid generationId, CancellationToken token)
    {
        var captured = await RecoverSealedCommitOutboxAsync(generationId, token).ConfigureAwait(false);
        var plan = captured.Plan ?? throw new CatalogGenerationCaptureConflictException();
        var read = await ReadPreparedCurrentAsync(plan, token).ConfigureAwait(false);
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proof = CurrentRead(read, token);
            await _state.ExecuteAsync(database =>
            {
                _ = AssertCommitGeneration(database, proof, captured.Capture.Scope, token);
                new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications); return true;
            }, token).ConfigureAwait(false);
            _ = await new JournaledProtocolSigner(_state, _keys, _budget).SignCapturedCatalogAsync(generationId, token).ConfigureAwait(false);
            captured = await PrepareSealedCommitOutboxAsync(generationId, token).ConfigureAwait(false);
        }
        finally { lock (_gate) { _current = null; _completedRead = null; } _serial.Release(); }

        read = await ReadPreparedCurrentAsync(plan, token).ConfigureAwait(false);
        await _serial.WaitAsync(token).ConfigureAwait(false);
        SingleUploadAdmission? grant = null; CatalogObjectReference reference;
        try
        {
            var proof = CurrentRead(read, token);
            var prepared = await _state.ExecuteAsync(database =>
            {
                var generation = AssertCommitGeneration(database, proof, captured.Capture.Scope, token);
                new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                var work = new WorkspacePublicationRepository(database, _budget).Read(generation.Capture.CommitIntentId)
                    ?? throw new CatalogGenerationCaptureConflictException();
                return (Generation: generation, Work: work);
            }, token).ConfigureAwait(false);
            var work = prepared.Work;
            grant = new(this, proof, prepared.Generation, new(work.Id, work.Scope, work.Role, work.MimeType, work.Payload.Length, work.Sha256, work.FileId, work.VerifiedVersion));
            var uploaded = await new WorkspacePublicationUploader(_state, _accounts, _budget, grant).UploadAsync(work.Id, token).ConfigureAwait(false);
            if (uploaded.FileId is null || uploaded.VerifiedVersion is null) throw new WorkspacePublicationConflictException();
            reference = new(uploaded.Scope.AccountId, uploaded.FileId, uploaded.Scope.FolderId, uploaded.Payload.Length, uploaded.Sha256);
            var actual = await new CatalogWorkspaceObjectSource(_state, _accounts, new(uploaded.Scope.AccountId, uploaded.Scope.FolderId), uploaded.Scope.AccountRevision)
                .ReadAsync(reference, token).ConfigureAwait(false);
            if (actual.Length != reference.Bytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual.Span), Convert.FromHexString(reference.Sha256)))
                throw new WorkspaceObservationConflictException();
            await grant.ValidateAsync(work.Scope, token).ConfigureAwait(false); Check(token);
        }
        finally { grant?.Retire(); lock (_gate) { _current = null; _completedRead = null; } _serial.Release(); }

        read = await ReadPreparedCurrentAsync(plan, token).ConfigureAwait(false);
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proof = CurrentRead(read, token);
            await _state.ExecuteAsync(database =>
            {
                _ = AssertCommitGeneration(database, proof, captured.Capture.Scope, token, requireVisible: true);
                new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                var actual = new WorkspaceObservationRepository(database, _budget).Known(proof.Result.Observation.Context)
                    .SingleOrDefault(item => item.FileId == reference.FileId);
                if (actual is null || actual.Bytes != reference.Bytes || actual.Sha256 != reference.Sha256 || (actual.MimeType ?? "application/json") != "application/json")
                    throw new WorkspaceObservationConflictException();
                return true;
            }, token).ConfigureAwait(false);
            Check(token); return reference;
        }
        finally { _serial.Release(); }
    }

    private async Task<ReconciledLocalCatalog> ReadPreparedCurrentAsync(CapturedCatalogCommitPlan plan, CancellationToken token)
    {
        while (true)
        {
            try { return await ReconcileAsync(plan, token).ConfigureAwait(false); }
            catch (WorkspaceHistoryReplayPendingException) { token.ThrowIfCancellationRequested(); }
        }
    }

    private async Task<CapturedCatalogPublication> RecoverSealedCommitOutboxAsync(Guid generationId, CancellationToken token)
    {
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var sealedIntent = await _state.ExecuteAsync(database =>
            {
                Check(token); var publication = new CatalogGenerationCaptureRepository(database, _budget).Publication(generationId)
                    ?? throw new CatalogGenerationCaptureConflictException();
                if (publication.Plan is null || publication.Capture.Phase is not ("dependencies-ready" or "commit-sealed")) throw new CatalogGenerationCaptureConflictException();
                var authority = new LibraryAuthorityRepository(database, _budget); authority.AssertLocalPublicationScope(publication.Capture.Scope);
                _keys.AssertPublicationIdentity(authority.Controller() ?? throw new CatalogPublicationNotAuthorizedException(), authority.CaptureLocalPublicationAuthority());
                var intent = new ProtocolSigningJournal(database, _budget).Read(publication.Capture.CommitIntentId);
                return (Publication: publication, Sealed: intent?.Envelope is not null);
            }, token).ConfigureAwait(false);
            return sealedIntent.Sealed ? await PrepareSealedCommitOutboxAsync(generationId, token).ConfigureAwait(false) : sealedIntent.Publication;
        }
        finally { lock (_gate) { _current = null; _completedRead = null; } _serial.Release(); }
    }

    private Task<CapturedCatalogPublication> PrepareSealedCommitOutboxAsync(Guid generationId, CancellationToken token) => _state.ExecuteAsync(database =>
    {
        Check(token); var publication = new CatalogGenerationCaptureRepository(database, _budget).ConfirmCommitSealed(generationId);
        var scope = publication.Capture.Scope; var authority = new LibraryAuthorityRepository(database, _budget); authority.AssertLocalPublicationScope(scope);
        _keys.AssertPublicationIdentity(authority.Controller() ?? throw new CatalogPublicationNotAuthorizedException(), authority.CaptureLocalPublicationAuthority());
        var intent = new ProtocolSigningJournal(database, _budget).Read(publication.Capture.CommitIntentId) ?? throw new CatalogGenerationCaptureConflictException();
        // The same explicitly fixed ID is used in both journals. Neither a retry nor
        // a lost phase checkpoint allocates a new signing or metadata-object identity.
        var outbox = new WorkspacePublicationRepository(database, _budget); var identities = outbox.Identities(WorkspaceObservationRepository.MaximumDocuments);
        if (!identities.Contains(intent.Id) && identities.Count == WorkspaceObservationRepository.MaximumDocuments) throw new CatalogTraversalLimitException();
        _ = outbox.Prepare(intent.Id, scope, "catalog-commit", "application/json", intent.Envelope!.Value.Span);
        return publication;
    }, token);

    private CapturedCatalogPublication AssertCommitGeneration(ProcessorStateDatabase database, CompletedRead proof, WorkspacePublicationScope scope,
        CancellationToken token, bool requireVisible = false)
    {
        AssertReadProof(database, proof, token); new LibraryAuthorityRepository(database, _budget).AssertLocalPublicationScope(scope);
        var publication = new CatalogGenerationCaptureRepository(database, _budget).Publication(scope.GenerationId)
            ?? throw new CatalogGenerationCaptureConflictException();
        var capture = publication.Capture; var plan = publication.Plan ?? throw new CatalogGenerationCaptureConflictException();
        if (capture.Scope != scope || capture.Phase is not ("dependencies-ready" or "commit-sealed") || proof.Prepared != plan ||
            proof.Result.PendingSigning.Any(id => id != capture.CommitIntentId) || proof.Result.PendingPublications.Any(id => id != capture.CommitIntentId))
            throw new CatalogGenerationCaptureConflictException();
        var intent = new ProtocolSigningJournal(database, _budget).Read(capture.CommitIntentId);
        var selected = proof.Result.Parent; var parent = publication.Basis.Parent;
        var ownVisible = intent?.Envelope is { } envelope && selected is not null && selected.Bytes == envelope.Length && selected.Sha256 == intent.EnvelopeSha256 &&
            proof.Result.Sequence == checked(publication.Basis.ParentSequence + 1);
        if (!ownVisible && (requireVisible || proof.Result.Sequence != publication.Basis.ParentSequence ||
            (parent is null ? selected is not null : selected is null || parent.AccountId != selected.AccountId || parent.WorkspaceId != selected.WorkspaceId ||
                parent.Bytes != selected.Bytes || parent.Sha256 != selected.Sha256))) throw new CatalogGenerationCaptureConflictException();
        var observations = new WorkspaceObservationRepository(database, _budget).Known(proof.Result.Observation.Context);
        if (parent is not null && !observations.Any(item => item.FileId == parent.FileId && item.Bytes == parent.Bytes && item.Sha256 == parent.Sha256 &&
            (item.MimeType ?? "application/json") == "application/json")) throw new WorkspaceObservationConflictException();
        var work = new WorkspacePublicationRepository(database, _budget).Read(capture.CommitIntentId);
        if (work is null && proof.Publications.Objects.Count == proof.Publications.Maximum) throw new CatalogTraversalLimitException();
        if (intent?.Envelope is { } sealedEnvelope)
        {
            if (capture.Phase != "commit-sealed" || work is null || work.Scope != scope || work.Role != "catalog-commit" || work.MimeType != "application/json" ||
                work.Sha256 != intent.EnvelopeSha256 || !work.Payload.Span.SequenceEqual(sealedEnvelope.Span)) throw new WorkspacePublicationConflictException();
            if (ownVisible && (work.FileId is null || !observations.Any(item => item.FileId == work.FileId && item.Bytes == work.Payload.Length && item.Sha256 == work.Sha256 &&
                (item.MimeType ?? "application/json") == "application/json"))) throw new WorkspaceObservationConflictException();
        }
        else if (capture.Phase != "dependencies-ready" || work is not null || requireVisible) throw new CatalogGenerationCaptureConflictException();
        return publication;
    }
}
