using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed record PreparedInitialLibraryPublication(LibraryInitialPublicationPlan Plan, WorkspacePublicationRecord Publisher, WorkspacePublicationRecord Sources);

/// <summary>Prepares exact native-signed initial policies and their durable create-only
/// outbox identities, without network writes or catalog activation. The production owner
/// still must establish complete current cloud/history/pending admission before uploading.
/// Callers join preparation before retiring the borrowed native key/state owners.</summary>
public sealed class InitialLibraryPublicationPreparation
{
    private readonly ProcessorStateCoordinator _state;
    private readonly LibrarySigningIdentityOwner _keys;
    private readonly StorageBudgetManager _budget;
    private readonly JournaledProtocolSigner _signer;
    public InitialLibraryPublicationPreparation(ProcessorStateCoordinator state, LibrarySigningIdentityOwner keys, StorageBudgetManager budget)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state)); _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget)); _signer = new(state, keys, budget);
    }
    public async Task<PreparedInitialLibraryPublication> PrepareAsync(CancellationToken token)
    {
        var signing = await _signer.PrepareInitialPolicyPlanAsync(token).ConfigureAwait(false);
        var publisher = await _signer.SignInitialPublisherAuthorizationAsync(signing.PublisherIntentId, token).ConfigureAwait(false);
        var sources = await _signer.SignInitialSourceConfigurationAsync(signing.SourceIntentId, token).ConfigureAwait(false);
        var result = await _state.ExecuteAsync(database =>
        {
            token.ThrowIfCancellationRequested();
            if (publisher.Envelope is null || sources.Envelope is null) throw new InvalidDataException("Prepared initial policy signing did not retain both sealed envelopes.");
            var authority = new LibraryAuthorityRepository(database, _budget);
            // Local selection is explicitly not a fabricated remote observation.
            // A partial failure remains journaled and resumes these same exact policies.
            authority.SelectPublisherPolicy(publisher.Envelope.Value.Span); authority.SelectSourceConfiguration(sources.Envelope.Value.Span);
            var current = authority.CaptureLocalPublicationAuthority();
            _keys.AssertPublicationIdentity(authority.Controller() ?? throw new InvalidDataException("Initial policy preparation lost its controller."), current);
            var plan = authority.PrepareInitialPublication(); authority.AssertLocalPublicationScope(plan.Scope);
            if (publisher.Id != plan.PublisherIntentId || sources.Id != plan.SourceIntentId ||
                publisher.EnvelopeSha256 != plan.PublisherEnvelopeSha256 || sources.EnvelopeSha256 != plan.SourceEnvelopeSha256)
                throw new LibraryInitialPublicationConflictException();
            var outbox = new WorkspacePublicationRepository(database, _budget);
            var publisherObject = outbox.Prepare(plan.PublisherObjectId, plan.Scope, "publisher-authorization", "application/json", publisher.Envelope.Value.Span);
            var sourceObject = outbox.Prepare(plan.SourceObjectId, plan.Scope, "source-configuration", "application/json", sources.Envelope.Value.Span);
            if (publisherObject.Sha256 != plan.PublisherEnvelopeSha256 || sourceObject.Sha256 != plan.SourceEnvelopeSha256) throw new InvalidDataException("Initial outbox work changed its exact sealed policies.");
            return new PreparedInitialLibraryPublication(plan, publisherObject, sourceObject);
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return result;
    }
}
