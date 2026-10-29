using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCatalogReconciliationService
{
    private sealed record OwnedLocalAccess(WorkspacePublicationScope Scope, CatalogGenerationLocalAccess Context);
    private sealed record PendingCapturedSignature(Guid IntentId, string BodySha256, string EnvelopeSha256, int EnvelopeBytes);
    private OwnedLocalAccess? _localAccess;

    internal CatalogGenerationLocalAccess? LocalAccessForScope(WorkspacePublicationScope scope)
    { lock (_gate) return _localAccess is { } local && local.Scope == scope ? local.Context : null; }

    internal void AssertLocalGenerationScope(ProcessorStateDatabase database, WorkspacePublicationScope scope, CancellationToken token)
    {
        Check(token); var authority = new LibraryAuthorityRepository(database, _budget);
        authority.AssertLocalPublicationScope(scope, LocalAccessForScope(scope));
        _keys.AssertPublicationIdentity(authority.Controller() ?? throw new CatalogPublicationNotAuthorizedException(), authority.CaptureLocalPublicationAuthority());
    }

    private async Task EnsureActiveGenerationAccessAsync(CancellationToken token)
    {
        var pending = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).Active().SingleOrDefault(), token).ConfigureAwait(false);
        if (pending is not null && pending.Phase is not ("complete" or "snapshot-retiring"))
            await EnsureGenerationAccessAsync(pending.Scope.GenerationId, token).ConfigureAwait(false);
    }

    private async Task EnsureGenerationAccessAsync(Guid generationId, CancellationToken token)
    {
        var renew = await _state.ExecuteAsync(database =>
        {
            Check(token); var capture = new CatalogGenerationCaptureRepository(database, _budget).Read(generationId)
                ?? throw new CatalogGenerationCaptureConflictException();
            var current = new LibraryAuthorityRepository(database, _budget).CaptureLocalPublicationAuthority();
            if (!SameSemanticScope(capture.Scope, current) || capture.Scope.AccountRevision > current.AccountRevision) throw new StaleLibraryConfigurationException();
            if (capture.Scope.AccountRevision == current.AccountRevision) return false;
            var local = LocalAccessForScope(capture.Scope);
            if (local?.AccountRevision != current.AccountRevision) return true;
            AssertLocalGenerationScope(database, capture.Scope, token); return false;
        }, token).ConfigureAwait(false);
        if (renew) _ = await ReconcileRenewedAccessAsync(generationId, token).ConfigureAwait(false);
    }

    private static bool SameSemanticScope(WorkspacePublicationScope original, LocalCatalogPublicationAuthority current) =>
        original.LibraryId == current.LibraryId && original.ActorId == current.ActorId && original.ActorEpoch == current.ActorEpoch &&
        original.AccountId == current.AccountId && original.FolderId == current.FolderId && original.ConfigurationRevision == current.ConfigurationRevision &&
        original.AuthoritySha256 == current.AuthoritySha256;

    // A lost local outbox checkpoint may leave this exact native-sealed captured
    // signature pending before renewed access can be admitted. This private READ-only
    // recovery path never grants an upload or silently adopts another signing intent.
    private PendingCapturedSignature? CapturePendingRenewedSignature(ProcessorStateDatabase database, Guid generationId,
        LocalCatalogPublicationAuthority current, ProtocolSigningInventory signing)
    {
        var journal = new CatalogGenerationCaptureRepository(database, _budget);
        var capture = journal.Active().SingleOrDefault() ?? throw new CatalogGenerationCaptureConflictException();
        if (capture.Scope.GenerationId != generationId || !SameSemanticScope(capture.Scope, current) || capture.Scope.AccountRevision >= current.AccountRevision)
            throw new StaleLibraryConfigurationException();
        var intent = new ProtocolSigningJournal(database, _budget).Read(capture.CommitIntentId);
        if (intent?.Envelope is not { } envelope || new WorkspacePublicationRepository(database, _budget).Read(intent.Id) is not null) return null;
        var publication = journal.Publication(generationId) ?? throw new CatalogGenerationCaptureConflictException();
        if (capture.Phase is not ("dependencies-ready" or "commit-sealed" or "commit-verified") || publication.Plan is not { } plan ||
            intent.ActorId != capture.Scope.ActorId || intent.Epoch != capture.Scope.ActorEpoch || intent.Kind != "catalog-commit" ||
            !plan.EncodeBody(capture.Scope, intent.Sequence).AsSpan().SequenceEqual(intent.Body.Span) ||
            !signing.Intents.Any(item => item.Id == intent.Id && item.BodySha256 == intent.BodySha256 && item.EnvelopeSha256 == intent.EnvelopeSha256 && item.EnvelopeBytes == envelope.Length))
            throw new CatalogGenerationCaptureConflictException();
        return new(intent.Id, intent.BodySha256, intent.EnvelopeSha256!, envelope.Length);
    }

    private async Task<ReconciledLocalCatalog> ReadRenewedCurrentAsync(Guid generationId, CancellationToken token)
    {
        while (true)
        {
            try { return await ReconcileAsync(null, token, generationId).ConfigureAwait(false); }
            catch (WorkspaceHistoryReplayPendingException) { token.ThrowIfCancellationRequested(); }
        }
    }
}
