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
        CatalogPublicationReader.Capability, DriveCatalogFacts.Capability];

    public Task<ProtocolSigningIntent> SignControllerAsync(Guid id, string kind,
        Func<LibrarySigningPublicIdentity, long, byte[]> body, CancellationToken cancellationToken) =>
        SignAsync(id, kind, true, body, cancellationToken);

    public Task<ProtocolSigningIntent> SignCatalogAsync(Guid id,
        Func<LibrarySigningPublicIdentity, long, byte[]> body, CancellationToken cancellationToken) =>
        SignAsync(id, "catalog-commit", false, body, cancellationToken);

    private Task<ProtocolSigningIntent> SignAsync(Guid id, string kind, bool controller,
        Func<LibrarySigningPublicIdentity, long, byte[]> body, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(body);
        var identity = _keys.PublicIdentity; // A native vault write/read-back must already have completed.
        return _state.ExecuteAsync(database =>
        {
            var pin = new LibraryAuthorityRepository(database, _budget).Controller()
                ?? throw new InvalidOperationException("Independently approve controller setup before signing library documents.");
            if (pin.LibraryId != identity.LibraryId || pin.ActorId != identity.ControllerId || pin.Epoch != identity.ControllerEpoch ||
                pin.PublicKeySpki != identity.ControllerPublicKeySpki || pin.KeyId != identity.ControllerKeyId)
                throw new InvalidDataException("The native signing owner does not match the pinned library controller.");
            var journal = new ProtocolSigningJournal(database, _budget);
            var intent = journal.Reserve(id, controller ? identity.ControllerId : identity.PublisherId,
                controller ? identity.ControllerEpoch : identity.PublisherEpoch, kind, sequence => body(identity, sequence));
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
                    controller ? ["library-source-configuration", "library-publisher-authorization"] : ["catalog-commit"]));
            return intent;
        }, token);
    }
}
