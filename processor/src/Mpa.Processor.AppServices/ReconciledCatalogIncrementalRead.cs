using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCatalogReconciliationService
{
    // Run-owned, private and never persisted or returned. The original completed
    // read remains historical evidence after retirement, not a write admission.
    private sealed record IncrementalBaseline(CompletedRead Read, CapturedCatalogPublication Generation,
        WorkspacePublicationInventory Publications, WorkspacePublicationExpectation? Expected);

    private async Task<ReconciledLocalCatalog> ReadDependencyCurrentAsync(WorkspacePublicationScope scope, IncrementalBaseline? baseline, CancellationToken token)
    {
        if (baseline is null)
        {
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CompletedRead? read;
                lock (_gate) { Check(token); read = _completedRead is { Incremental: true, Prepared: null } actual ? actual : null; }
                if (read is not null)
                    baseline = await _state.ExecuteAsync<IncrementalBaseline?>(database =>
                    {
                        try
                        {
                            var generation = AssertGeneration(database, read, scope, token);
                            new WorkspacePublicationRepository(database, _budget).AssertInventoryUnchanged(read.Publications);
                            return new IncrementalBaseline(read, generation, read.Publications, null);
                        }
                        catch (Exception error) when (BaselineChanged(error)) { return null; }
                    }, token).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
        }
        while (true)
        {
            try { return baseline is null ? await ReconcileAsync(token).ConfigureAwait(false) :
                await ReconcileAsync(null, token, baseline: baseline).ConfigureAwait(false); }
            catch (WorkspaceIncrementalRefreshRequiredException)
            {
                // The failed attempt and actual page evidence stay durable. A new full
                // attempt must reconcile them; this is never a grant from stale bytes.
                baseline = null; token.ThrowIfCancellationRequested();
            }
            catch (WorkspaceHistoryReplayPendingException) { baseline = null; token.ThrowIfCancellationRequested(); }
            catch (Exception error) when (baseline is not null && (BaselineChanged(error) || error is WorkspaceInventoryIncompleteException))
            { baseline = null; token.ThrowIfCancellationRequested(); }
        }
    }

    private static bool BaselineChanged(Exception error) => error is WorkspaceObservationConflictException or WorkspacePublicationInventoryChangedException
        or ProtocolSigningInventoryChangedException or StaleLibraryConfigurationException;

    private Task<bool> AssertIncrementalBaselineAsync(IncrementalBaseline baseline, Captured captured, WorkspacePublicationInventory publications,
        Access access, CancellationToken token) => _state.ExecuteAsync(database =>
    {
        Check(token); var read = baseline.Read;
        if (captured.Recovery is not null || read.Prepared is not null || read.Captured.Recovery is not null || read.Inventory.MissingKnownIds.Count != 0 ||
            captured.Pin != read.Captured.Pin || captured.Authority != read.Captured.Authority || captured.Native != read.Captured.Native ||
            captured.AllowShared != read.Captured.AllowShared || captured.Signing.Sha256 != read.Captured.Signing.Sha256 || access != read.Access ||
            publications.Context != read.Result.Observation.Context || publications.Sha256 != baseline.Publications.Sha256)
            throw new WorkspaceIncrementalRefreshRequiredException();
        var observations = new WorkspaceObservationRepository(database, _budget);
        try { observations.AssertCompletedScan(read.Result.Observation, read.AttemptId); }
        catch (WorkspaceObservationConflictException) { throw new WorkspaceIncrementalRefreshRequiredException(); }
        AssertLocalGenerationScope(database, baseline.Generation.Capture.Scope, token);
        var actual = new CatalogGenerationCaptureRepository(database, _budget).Publication(baseline.Generation.Capture.Scope.GenerationId);
        if (actual != baseline.Generation || actual.Capture.Phase != "publication-planned" || actual.Plan is not null)
            throw new WorkspaceIncrementalRefreshRequiredException();
        return true;
    }, token);

    private Task<DriveWorkspaceObject?> ReadBaselineBytesAsync(IncrementalBaseline baseline, DriveWorkspaceDocumentCandidate candidate, CancellationToken token)
    {
        Check(token);
        var observation = baseline.Read.Known.SingleOrDefault(item => item.FileId == candidate.FileId);
        var expected = baseline.Publications.Objects.SingleOrDefault(item => item.FileId == candidate.FileId);
        // Unknown/nonlocal documents still go through the real account reader. No
        // receipt can stand in for an actual baseline version and fresh change feed.
        if (observation is null || expected is null || expected.VerifiedVersion is null || candidate.Version != observation.Version ||
            candidate.Bytes != observation.Bytes || candidate.MimeType != (observation.MimeType ?? "application/json") ||
            expected.Bytes != observation.Bytes || expected.Sha256 != observation.Sha256 || expected.MimeType != candidate.MimeType)
            return Task.FromResult<DriveWorkspaceObject?>(null);
        return _state.ExecuteAsync<DriveWorkspaceObject?>(database =>
        {
            Check(token); var actual = new WorkspacePublicationRepository(database, _budget).Read(expected.Id)
                ?? throw new WorkspacePublicationInventoryChangedException();
            if (actual.Scope != expected.Scope || actual.Role != expected.Role || actual.MimeType != expected.MimeType || actual.Sha256 != expected.Sha256 ||
                actual.Payload.Length != expected.Bytes || actual.FileId != expected.FileId || actual.VerifiedVersion != expected.VerifiedVersion)
                throw new WorkspacePublicationInventoryChangedException();
            Check(token);
            return new(baseline.Read.Result.Observation.Context.AccountId, candidate.FileId,
                baseline.Read.Result.Observation.Context.FolderId, candidate.Version, actual.Payload);
        }, token);
    }
}
