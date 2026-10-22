using System.Globalization;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Bounded serialization of a source proof already validated by the authority
/// repository. No current settings, account credentials or display names are substituted.</summary>
internal static class InitialSourceConfigurationDocument
{
    public static byte[] Encode(LibrarySigningPublicIdentity identity, long sequence, LibraryBootstrapSources approved)
    {
        using var document = JsonDocument.Parse(approved.CanonicalSnapshot); var source = document.RootElement;
        return new CanonicalJson(ProtocolSigningJournal.MaximumBodyBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa", major = 1, minor = 0, libraryId = identity.LibraryId.ToString("D"), actorId = identity.ControllerId.ToString("D"),
            epoch = identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture), sequence = sequence.ToString(CultureInfo.InvariantCulture),
            kind = "library-source-configuration", requiredCapabilities = new[] { CatalogSourceConfiguration.Capability },
            payload = new { configurationRevision = source.GetProperty("configurationRevision"), workspace = source.GetProperty("workspace"),
                accounts = source.GetProperty("accounts"), sources = source.GetProperty("sources") }
        }));
    }
}
