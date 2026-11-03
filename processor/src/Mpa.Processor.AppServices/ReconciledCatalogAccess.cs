using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCatalogReconciliationService
{
    /// <summary>Records a separate renewed-credential attempt after this concrete owner
    /// completes actual access/history reconciliation. The returned record is historical
    /// evidence only. It cannot relax an original-scope write/signing/completion fence.</summary>
    public async Task<CatalogGenerationAccessAttempt> ReconcileRenewedAccessAsync(Guid generationId, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            var token = linked.Token;
            try
            {
                var read = await ReadRenewedCurrentAsync(generationId, token).ConfigureAwait(false);
                await _serial.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var proof = CurrentRead(read, token);
                    return await _state.ExecuteAsync(database =>
                    {
                        AssertReadProof(database, proof, token, allowPendingSignatureRecovery: true);
                        new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                        var journal = new CatalogGenerationCaptureRepository(database, _budget);
                        var capture = journal.Active().SingleOrDefault() ?? throw new CatalogGenerationCaptureConflictException();
                        if (capture.Scope.GenerationId != generationId || capture.Phase is "complete" or "snapshot-retiring" ||
                            read.PendingSigning.Count != 0 && capture.Phase is not ("dependencies-ready" or "commit-sealed" or "commit-verified") ||
                            read.PendingSigning.Any(id => id != capture.CommitIntentId) ||
                            read.PendingPublications.Any(id => proof.Publications.Objects.Single(item => item.Id == id).Scope != capture.Scope))
                            throw new CatalogGenerationCaptureConflictException();
                        var publication = journal.Publication(generationId);
                        var known = new WorkspaceObservationRepository(database, _budget).Known(read.Observation.Context).ToDictionary(item => item.FileId, StringComparer.Ordinal);
                        foreach (var work in proof.Publications.Objects.Where(item => item.Scope.GenerationId == generationId))
                        {
                            if (work.Scope != capture.Scope) throw new WorkspacePublicationConflictException();
                            if (work.FileId is null) { if (work.VerifiedVersion is not null) throw new WorkspacePublicationConflictException(); continue; }
                            if (!known.TryGetValue(work.FileId, out var observed))
                            { if (work.VerifiedVersion is not null) throw new WorkspaceObservationConflictException(); continue; }
                            if (observed.Bytes != work.Bytes || observed.Sha256 != work.Sha256 || (observed.MimeType ?? "application/json") != work.MimeType ||
                                work.VerifiedVersion is not null && observed.Version < work.VerifiedVersion) throw new WorkspaceObservationConflictException();
                        }
                        if (publication is not null)
                        {
                            var basis = publication.Basis; var selected = read.Parent;
                            var intent = new ProtocolSigningJournal(database, _budget).Read(capture.CommitIntentId);
                            var own = intent?.Envelope is { } envelope && selected is not null && selected.Bytes == envelope.Length &&
                                selected.Sha256 == intent.EnvelopeSha256 && read.Sequence == checked(basis.ParentSequence + 1);
                            if (!own && (read.Sequence != basis.ParentSequence || (basis.Parent is null ? selected is not null : selected is null ||
                                basis.Parent.AccountId != selected.AccountId || basis.Parent.WorkspaceId != selected.WorkspaceId ||
                                basis.Parent.Bytes != selected.Bytes || basis.Parent.Sha256 != selected.Sha256))) throw new CatalogGenerationCaptureConflictException();
                            if (basis.Parent is { } parent && (!known.TryGetValue(parent.FileId, out var observed) || observed.Bytes != parent.Bytes ||
                                observed.Sha256 != parent.Sha256 || (observed.MimeType ?? "application/json") != "application/json")) throw new WorkspaceObservationConflictException();
                            if (own)
                            {
                                var original = proof.Publications.Objects.SingleOrDefault(item => item.Id == capture.CommitIntentId);
                                if (original?.FileId is null || !known.ContainsKey(original.FileId)) throw new WorkspaceObservationConflictException();
                            }
                        }
                        var result = journal.RecordRenewedAccess(generationId, proof.Captured.Authority, read.Observation, proof.Captured.Signing, proof.Publications);
                        lock (_gate)
                        {
                            Check(token);
                            if (_localAccess is { } retained && retained.Scope == result.Scope && retained.Context.AccountRevision == result.AccountRevision)
                            {
                                new LibraryAuthorityRepository(database, _budget).AssertLocalPublicationScope(result.Scope, retained.Context);
                                return result;
                            }
                        }
                        var local = journal.OpenLocalAccess(generationId, result.AccountRevision, read.Observation, proof.Captured.Signing, proof.Publications, _stop.Token);
                        try
                        {
                            lock (_gate) { Check(token); var old = _localAccess; _localAccess = new(result.Scope, local); old?.Context.Dispose(); }
                        }
                        catch { local.Dispose(); throw; }
                        Check(token); return result;
                    }, token).ConfigureAwait(false);
                }
                finally { lock (_gate) { _current = null; _completedRead = null; } _serial.Release(); }
            }
            catch (Exception error) { lock (_gate) { _failure = error; _current = null; _completedRead = null; } throw; }
        }
    }
}
