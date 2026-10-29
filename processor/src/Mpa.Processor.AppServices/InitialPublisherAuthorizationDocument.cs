using System.Globalization;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Serializes the native publisher in the already validated initial plan.
/// It cannot choose another key, workspace or source approval and grants no upload rights.</summary>
internal static class InitialPublisherAuthorizationDocument
{
    private static readonly string[] Capabilities = [CatalogPublisherAuthorization.Capability];
    public static byte[] Encode(LibrarySigningPublicIdentity identity, long sequence, LibraryInitialPolicyPlan plan)
    {
        if (identity.LibraryId != plan.LibraryId || identity.ControllerId != plan.ControllerId || identity.ControllerEpoch != plan.ControllerEpoch ||
            identity.PublisherId != plan.PublisherId || identity.PublisherEpoch != plan.PublisherEpoch || identity.PublisherKeyId != plan.PublisherKeyId)
            throw new InvalidDataException("The native initial publisher changed after its plan was captured.");
        return new CanonicalJson(ProtocolSigningJournal.MaximumBodyBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa", major = 1, minor = 0, libraryId = plan.LibraryId.ToString("D"), actorId = plan.ControllerId.ToString("D"),
            epoch = plan.ControllerEpoch.ToString(CultureInfo.InvariantCulture), sequence = sequence.ToString(CultureInfo.InvariantCulture),
            kind = "library-publisher-authorization", requiredCapabilities = Capabilities,
            payload = new { workspace = new { accountId = plan.AccountId, folderId = plan.FolderId }, publisher = new { actorId = plan.PublisherId.ToString("D"),
                epoch = plan.PublisherEpoch.ToString(CultureInfo.InvariantCulture), publicKeySpki = identity.PublisherPublicKeySpki, keyId = plan.PublisherKeyId } }
        }));
    }
}
