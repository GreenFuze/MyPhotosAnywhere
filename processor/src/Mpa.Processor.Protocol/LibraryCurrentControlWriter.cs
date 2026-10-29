using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>Outgoing canonical bytes only. The caller must durably reserve the actual
/// Drive control identity and root signing intent first. Serialization never creates a
/// verified document, signs, publishes, proves freshness or grants original writes.</summary>
public sealed class LibraryCurrentControlWriter(LibraryConnectionDescriptor connection)
{
    private readonly LibraryConnectionDescriptor _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public byte[] EncodePayload(long sequence,LibraryPolicyReference publishers,LibraryPolicyReference sources,CatalogObjectReference? catalog)
    {
        ArgumentNullException.ThrowIfNull(publishers);ArgumentNullException.ThrowIfNull(sources);
        var bytes = new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            workspace = new { accountId = _connection.Workspace.AccountId,folderId = _connection.Workspace.FolderId },
            currentFileId = _connection.CurrentFileId,publishers = Policy(publishers),sources = Policy(sources),catalog = catalog is null ? null : Reference(catalog)
        }));
        var selected = LibraryCurrentControl.ValidatePayload(bytes,sequence,_connection);
        if (selected.Publishers != publishers || selected.Sources != sources || selected.Catalog != catalog)
            throw new InvalidDataException("Outgoing current-control references changed during canonical serialization.");
        return bytes;
    }

    /// <summary>Validates unsigned shape/scope only, never signature or publication authority.</summary>
    public void ValidateUnsignedPayload(ReadOnlySpan<byte> payload,long sequence) => LibraryCurrentControl.ValidatePayload(payload,sequence,_connection);

    public byte[] EncodeDocument(long sequence,LibraryPolicyReference publishers,LibraryPolicyReference sources,CatalogObjectReference? catalog)
    {
        using var payload = JsonDocument.Parse(EncodePayload(sequence,publishers,sources,catalog));
        return new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa",major = 1,minor = 0,libraryId = _connection.LibraryId.ToString("D"),actorId = _connection.ControllerId.ToString("D"),
            epoch = _connection.ControllerEpoch.ToString(CultureInfo.InvariantCulture),sequence = sequence.ToString(CultureInfo.InvariantCulture),
            kind = LibraryCurrentControl.Kind,requiredCapabilities = new[] { LibraryCurrentControl.Capability },payload = payload.RootElement
        }));
    }

    private static object Policy(LibraryPolicyReference value) => new { @object = Reference(value.Reference),sequence = value.Sequence.ToString(CultureInfo.InvariantCulture) };
    private static object Reference(CatalogObjectReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new { accountId = value.AccountId,fileId = value.FileId,workspaceId = value.WorkspaceId,bytes = value.Bytes.ToString(CultureInfo.InvariantCulture),sha256 = value.Sha256 };
    }
}
