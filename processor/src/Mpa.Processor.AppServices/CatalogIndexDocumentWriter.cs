using System.Globalization;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Bounded canonical index serialization from already captured object references.
/// Revalidates the portable schema; bytes/hashes, actual Drive membership, signed ancestry
/// and publication admission remain the owning generation's separate responsibilities.</summary>
public sealed class CatalogIndexDocumentWriter(CatalogWorkspace workspace)
{
    private readonly CatalogPublicationReader _reader = new(workspace ?? throw new ArgumentNullException(nameof(workspace)));
    public byte[] Encode(string prefix, IEnumerable<CatalogIndexChild> source)
    {
        ArgumentNullException.ThrowIfNull(source); var children = source.Take(257).ToArray();
        if (children.Length > 256) throw new CatalogPartitionCapacityException();
        if (children.Any(child => child.Chunk is not null && child.Chunk.Encoded != child.Reference))
            throw new ProtocolFormatException("Chunk expansion metadata must describe the exact captured child object.");
        var bytes = new CanonicalJson(CatalogPublicationReader.MaximumIndexBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa-catalog-index", major = 1, prefix,
            children = children.OrderBy(child => child.Edge, StringComparer.Ordinal).Select(child => new
            {
                edge = child.Edge, kind = child.Chunk is null ? "index" : "chunk",
                @object = new { accountId = child.Reference.AccountId, fileId = child.Reference.FileId, workspaceId = child.Reference.WorkspaceId,
                    bytes = child.Reference.Bytes.ToString(CultureInfo.InvariantCulture), sha256 = child.Reference.Sha256 },
                expanded = child.Chunk is null ? null : new { bytes = child.Chunk.ExpandedBytes.ToString(CultureInfo.InvariantCulture), sha256 = child.Chunk.ExpandedSha256 }
            })
        }));
        _ = _reader.ReadIndex(bytes, prefix); return bytes;
    }
}
