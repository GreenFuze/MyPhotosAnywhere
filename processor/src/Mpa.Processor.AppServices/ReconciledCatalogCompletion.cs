using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCatalogReconciliationService
{
    /// <summary>Only this actual full current head/graph read may advance the two
    /// transactional publisher-completion checkpoints. Public read summaries and old
    /// verification receipts remain evidence, never transferable completion grants.</summary>
    public async Task<PublishedCatalogHead> CompleteCapturedGenerationAsync(Guid generationId, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token); }
        using (linked)
        {
            var token = linked.Token;
            try
            {
                await EnsureGenerationAccessAsync(generationId, token).ConfigureAwait(false);
                var captured = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).Publication(generationId)
                    ?? throw new CatalogGenerationCaptureConflictException(), token).ConfigureAwait(false);
                if (captured.Capture.Phase is not ("commit-sealed" or "commit-verified" or "complete") || captured.Plan is null)
                    throw new CatalogGenerationCaptureConflictException();
                var read = await ReadPreparedCurrentAsync(captured.Plan, token).ConfigureAwait(false);
                await _serial.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var proof = CurrentRead(read, token);
                    var committed = await _state.ExecuteAsync(database =>
                    {
                        _ = AssertCommitGeneration(database, proof, captured.Capture.Scope, token, requireVisible: true);
                        new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                        var work = new WorkspacePublicationRepository(database, _budget).Read(captured.Capture.CommitIntentId)
                            ?? throw new CatalogGenerationCaptureConflictException();
                        var reference = new Mpa.Processor.Protocol.CatalogObjectReference(work.Scope.AccountId,
                            work.FileId ?? throw new WorkspacePublicationConflictException(), work.Scope.FolderId, work.Payload.Length, work.Sha256);
                        _ = new CatalogGenerationCaptureRepository(database, _budget).ConfirmCurrentCommitVerified(generationId, proof.Result.Observation, reference, LocalAccessForScope(captured.Capture.Scope));
                        return reference;
                    }, token).ConfigureAwait(false);
                    var result = await _state.ExecuteAsync(database =>
                    {
                        _ = AssertCommitGeneration(database, proof, captured.Capture.Scope, token, requireVisible: true);
                        new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(proof.Publications);
                        return new CatalogGenerationCaptureRepository(database, _budget).CompleteCurrentGeneration(generationId, proof.Result.Observation, committed, LocalAccessForScope(captured.Capture.Scope));
                    }, token).ConfigureAwait(false);
                    Check(token); return result;
                }
                finally { _serial.Release(); }
            }
            catch (Exception error) { lock (_gate) { _failure = error; _current = null; _completedRead = null; } throw; }
        }
    }
}
