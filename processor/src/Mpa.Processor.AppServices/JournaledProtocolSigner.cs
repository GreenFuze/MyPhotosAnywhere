using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Serial outgoing signing boundary borrowing the state and native key owners.
/// Reserves identity/body/sequence before signing; reuses a sealed envelope byte for byte.
/// Cancellation or a failed seal can leave durable work: resume with the same intent ID.
/// Factories must be synchronous bounded serialization, without I/O or user interaction.
/// This service grants no source permission, cloud reconciliation or publication authority.</summary>
public sealed class JournaledProtocolSigner(ProcessorStateCoordinator state, LibrarySigningIdentityOwner keys, StorageBudgetManager budget)
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly LibrarySigningIdentityOwner _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private static readonly string[] Capabilities = [CatalogPublisherAuthorization.Capability, CatalogSourceConfiguration.Capability,
        CatalogPublicationReader.Capability, DriveCatalogFacts.Capability,LibraryCurrentControl.Capability];

    /// <summary>Seal the fixed initial no-catalog snapshot from its exact retained
    /// policy receipts. The slot's immutable ID is also its signing intent ID. Actual
    /// cloud reconciliation/CAS remains separate; signing proves no fresh availability.
    /// Superseded policies cannot be replayed as initial setup, even with old receipts.</summary>
    public async Task<ProtocolSigningIntent> SignInitialCurrentControlAsync(CancellationToken token)
    {
        var id=await _state.ExecuteAsync(database=>
        {
            var slot=new LibraryCurrentControlSlotRepository(database,_budget).Prepare();
            if (slot.FileId is null) throw new InvalidOperationException("Capture the actual control-file allocation before signing current setup.");
            return slot.Id;
        },token).ConfigureAwait(false);
        return await SignAsync(id,LibraryCurrentControl.Kind,true,(database,identity)=>
        {
            var authority=new LibraryAuthorityRepository(database,_budget);
            var completion=authority.InitialPublicationCompletion() ?? throw new InvalidDataException("Current setup lost its initial policy receipt.");
            var slot=new LibraryCurrentControlSlotRepository(database,_budget).Prepare();
            if (slot.Id!=id || slot.FileId is null) throw new ProtocolSigningConflictException();
            _keys.AssertPublicationIdentity(authority.Controller() ?? throw new InvalidDataException("Current setup lost its Root pin."),authority.CaptureLocalPublicationAuthority());
            var publisher=Publisher(identity);
            var policyPlan=authority.AssertInitialPolicyIntentCurrent(completion.Plan.PublisherIntentId,"library-publisher-authorization",publisher);
            if (authority.AssertInitialPolicyIntentCurrent(completion.Plan.SourceIntentId,"library-source-configuration",publisher)!=policyPlan)
                throw new LibraryInitialPolicyConflictException();
            var journal=new ProtocolSigningJournal(database,_budget);
            var published=journal.Read(policyPlan.PublisherIntentId) ?? throw new InvalidDataException("The initial publisher signature is missing.");
            var sources=journal.Read(policyPlan.SourceIntentId) ?? throw new InvalidDataException("The initial source signature is missing.");
            var connection=slot.ConnectionDescriptor();var writer=new LibraryCurrentControlWriter(connection);
            var publisherReference=new LibraryPolicyReference(new(connection.Workspace.AccountId,completion.Publisher.FileId,connection.Workspace.FolderId,
                completion.Publisher.Bytes,completion.Publisher.Sha256),published.Sequence);
            var sourceReference=new LibraryPolicyReference(new(connection.Workspace.AccountId,completion.Sources.FileId,connection.Workspace.FolderId,
                completion.Sources.Bytes,completion.Sources.Sha256),sources.Sequence);
            return sequence=>writer.EncodeDocument(sequence,publisherReference,sourceReference,null);
        },token).ConfigureAwait(false);
    }

    public Task<LibraryInitialPolicyPlan> PrepareInitialPolicyPlanAsync(CancellationToken token) =>
        _state.ExecuteAsync(database => new LibraryAuthorityRepository(database, _budget).PrepareInitialPolicyPlan(Publisher(_keys.PublicIdentity)), token);

    /// <summary>Sign only the fixed initial publisher intent. Superseding policies or
    /// source changes retire this setup path; it cannot revive a revoked publisher.</summary>
    public Task<ProtocolSigningIntent> SignInitialPublisherAuthorizationAsync(Guid id, CancellationToken token) =>
        SignAsync(id, "library-publisher-authorization", true, (database, identity) =>
        {
            var plan = new LibraryAuthorityRepository(database, _budget).AssertInitialPolicyIntentCurrent(id, "library-publisher-authorization", Publisher(identity));
            return sequence => InitialPublisherAuthorizationDocument.Encode(identity, sequence, plan);
        }, token);

    public Task<ProtocolSigningIntent> SignControllerAsync(Guid id, string kind,
        Func<LibrarySigningPublicIdentity, long, byte[]> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SignAsync(id, kind, true, (_, identity) => sequence => body(identity, sequence), cancellationToken);
    }

    public Task<ProtocolSigningIntent> SignCatalogAsync(Guid id,
        Func<LibrarySigningPublicIdentity, long, byte[]> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SignAsync(id, "catalog-commit", false, (_, identity) => sequence => body(identity, sequence), cancellationToken);
    }

    /// <summary>Generation signing always reconstructs its body from the durable fixed
    /// plan under the serial state owner. A lost phase checkpoint replays the exact sealed
    /// intent. This grants no cloud upload or activation authority.</summary>
    public async Task<ProtocolSigningIntent> SignCapturedCatalogAsync(Guid generationId, CancellationToken cancellationToken,
        CatalogGenerationLocalAccess? localAccess = null)
    {
        var id = await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).Read(generationId)?.CommitIntentId
            ?? throw new InvalidOperationException("The generation capture is missing."), cancellationToken).ConfigureAwait(false);
        var intent = await SignAsync(id, "catalog-commit", false, (database, identity) =>
        {
            var publication = new CatalogGenerationCaptureRepository(database, _budget).Publication(generationId)
                ?? throw new InvalidOperationException("Capture the immutable publication plan before signing.");
            var capture = publication.Capture; var plan = publication.Plan ?? throw new InvalidOperationException("Seal dependencies before signing.");
            if (capture.CommitIntentId != id || capture.Phase is not ("dependencies-ready" or "commit-sealed") ||
                capture.Scope.LibraryId != identity.LibraryId || capture.Scope.ActorId != identity.PublisherId || capture.Scope.ActorEpoch != identity.PublisherEpoch)
                throw new CatalogGenerationCaptureConflictException();
            new LibraryAuthorityRepository(database).AssertLocalPublicationScope(capture.Scope, localAccess);
            return sequence => plan.EncodeBody(capture.Scope, sequence);
        }, cancellationToken).ConfigureAwait(false);
        await _state.ExecuteAsync(database => new CatalogGenerationCaptureRepository(database, _budget).ConfirmCommitSealed(generationId), cancellationToken).ConfigureAwait(false);
        return intent;
    }

    /// <summary>Only the exact initial human-approved source association may become the
    /// first outgoing source policy. Rechecks happen under the serial state owner before
    /// reserving or replaying a sequence; later settings cannot silently replace consent.</summary>
    public Task<ProtocolSigningIntent> SignInitialSourceConfigurationAsync(Guid id, CancellationToken cancellationToken) =>
        SignAsync(id, "library-source-configuration", true, (database, identity) =>
        {
            var authority = new LibraryAuthorityRepository(database, _budget);
            if (authority.InitialPolicyPlan() is not null) authority.AssertInitialPolicyIntentCurrent(id, "library-source-configuration", Publisher(identity));
            var approved = authority.AssertBootstrapSourcesCurrent();
            return sequence => InitialSourceConfigurationDocument.Encode(identity, sequence, approved);
        }, cancellationToken);

    private static PublisherSigningRegistration Publisher(LibrarySigningPublicIdentity identity) => new(identity.PublisherId, identity.PublisherEpoch, identity.PublisherPublicKeySpki, identity.PublisherKeyId);

    private Task<ProtocolSigningIntent> SignAsync(Guid id, string kind, bool controller,
        Func<ProcessorStateDatabase, LibrarySigningPublicIdentity, Func<long, byte[]>> prepare, CancellationToken token)
    {
        var identity = _keys.PublicIdentity; // A native vault write/read-back must already have completed.
        return _state.ExecuteAsync(database =>
        {
            var pin = new LibraryAuthorityRepository(database, _budget).Controller()
                ?? throw new InvalidOperationException("Independently approve controller setup before signing library documents.");
            if (pin.LibraryId != identity.LibraryId || pin.ActorId != identity.ControllerId || pin.Epoch != identity.ControllerEpoch ||
                pin.PublicKeySpki != identity.ControllerPublicKeySpki || pin.KeyId != identity.ControllerKeyId)
                throw new InvalidDataException("The native signing owner does not match the pinned library controller.");
            var body = prepare(database, identity); var journal = new ProtocolSigningJournal(database, _budget);
            var intent = journal.Reserve(id, controller ? identity.ControllerId : identity.PublisherId,
                controller ? identity.ControllerEpoch : identity.PublisherEpoch, kind, body);
            token.ThrowIfCancellationRequested();
            if (intent.Envelope is null)
            {
                var signature = controller ? _keys.SignControllerDocument(intent.Body.Span) : _keys.SignCatalogDocument(intent.Body.Span);
                token.ThrowIfCancellationRequested();
                intent = journal.Seal(intent.Id, intent.BodySha256, signature);
            }
            // Independently bind even a retained sealed replay to the actual native public
            // identity. A changed local actor row must not replace the enrolled signer.
            _ = new SignedProtocolDocuments(Capabilities).VerifyAndRead(intent.Envelope!.Value.Span,
                new(identity.LibraryId, controller ? identity.ControllerId : identity.PublisherId,
                    controller ? identity.ControllerEpoch : identity.PublisherEpoch,
                    Convert.FromBase64String(controller ? identity.ControllerPublicKeySpki : identity.PublisherPublicKeySpki),
                    controller ? ["library-source-configuration", "library-publisher-authorization",LibraryCurrentControl.Kind] : ["catalog-commit"]));
            return intent;
        }, token);
    }
}
