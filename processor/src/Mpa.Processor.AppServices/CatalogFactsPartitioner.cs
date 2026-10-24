using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed record CatalogFactsChunkDraft(string Prefix, int Assets, long ExpandedBytes, string ExpandedSha256,
    ReadOnlyMemory<byte> Compressed, string CompressedSha256);
public sealed class CatalogPartitionCapacityException() : IOException("This identity partition cannot fit a supported catalog chunk. No photos were truncated.");

/// <summary>Serial borrowed frozen reader. Emits bounded ordinal identity partitions,
/// splitting before memory/chunk limits. Normal full enumeration is required before a
/// generation may claim preparation completed; partial output is not a published head.</summary>
public sealed class CatalogFactsPartitioner
{
    private readonly FrozenCatalogSnapshot _source;
    private readonly int _assets;
    private readonly int _expanded;
    private readonly int _compressed;
    private readonly int _objects;
    public CatalogFactsPartitioner(FrozenCatalogSnapshot source, int maximumAssets = DriveCatalogFacts.MaximumAssets,
        int expandedLimit = CatalogChunkDecoder.MaximumExpandedBytes, int compressedLimit = CatalogChunkDecoder.MaximumCompressedBytes,
        int objectBudget = CatalogFactsTraversal.MaximumObjectBudget)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (maximumAssets is < 1 or > DriveCatalogFacts.MaximumAssets || expandedLimit is < 256 or > CatalogChunkDecoder.MaximumExpandedBytes ||
            compressedLimit is < 64 or > CatalogChunkDecoder.MaximumCompressedBytes || objectBudget is < 1 or > CatalogFactsTraversal.MaximumObjectBudget)
            throw new ArgumentOutOfRangeException(nameof(maximumAssets), "Catalog preparation limits must remain within consumer bounds.");
        _assets = maximumAssets; _expanded = expandedLimit; _compressed = compressedLimit; _objects = objectBudget;
    }

    public IEnumerable<CatalogFactsChunkDraft> ReadChunks(CancellationToken cancellationToken = default)
    {
        // Reserve the root, coverage root/pages and a possible predecessor. Actual
        // allocation/role alias checks and signed authority remain generation work.
        var nodes = 3 + (_source.Coverage.Count + CatalogPublicationReader.MaximumCoveragePageSources - 1) / CatalogPublicationReader.MaximumCoveragePageSources;
        if (nodes > _objects) throw new CatalogTraversalLimitException(); long observed = 0;
        foreach (var chunk in Children("")) { observed = checked(observed + chunk.Assets); yield return chunk; }
        cancellationToken.ThrowIfCancellationRequested();
        if (observed != _source.Count) throw new InvalidDataException("Partition preparation did not retain every captured photo.");

        IEnumerable<CatalogFactsChunkDraft> Children(string parent)
        {
            for (var part = 0; part < 256; part++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var prefix = parent + part.ToString("x2", CultureInfo.InvariantCulture);
                var size = _source.PartitionSize(prefix, cancellationToken); if (size.Count == 0) continue;
                if (++nodes > _objects) throw new CatalogTraversalLimitException();
                var chunk = size.Count <= _assets && size.EncodedUpperBound <= _expanded ? TryChunk(prefix, size, cancellationToken) : null;
                if (chunk is not null) yield return chunk;
                else
                {
                    if (prefix.Length == 8) throw new CatalogPartitionCapacityException();
                    foreach (var descendant in Children(prefix)) yield return descendant;
                }
            }
        }
    }

    private CatalogFactsChunkDraft? TryChunk(string prefix, FrozenCatalogPartitionSize size, CancellationToken token)
    {
        var assets = new List<DriveCatalogAsset>((int)size.Count); Guid? after = null;
        while (true)
        {
            var page = _source.ReadPage(prefix, after, 250, token); assets.AddRange(page.Assets);
            if (assets.Count > size.Count || assets.Count > _assets) throw new InvalidDataException("Captured partition changed during preparation.");
            if (!page.HasMore) break;
            if (page.Assets.Count == 0) throw new InvalidDataException("Captured partition continuation did not advance."); after = page.Assets[^1].Id;
        }
        if (assets.Count != size.Count) throw new InvalidDataException("Captured partition lost photos during preparation.");
        var expanded = new DriveCatalogFacts(_source.LibraryId).Encode(prefix, assets);
        if (expanded.Length > _expanded) return null;
        token.ThrowIfCancellationRequested(); var compressed = Compress(expanded); token.ThrowIfCancellationRequested();
        return compressed is null ? null : new(prefix, assets.Count, expanded.LongLength, Hash(expanded), compressed, Hash(compressed));
    }

    private byte[]? Compress(byte[] expanded)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_compressed);
        try
        {
            // Fixed capacity makes the compressed bound apply while encoding, including
            // the gzip footer. An overflowing candidate is split, never truncated.
            using var output = new MemoryStream(buffer, 0, _compressed, writable: true, publiclyVisible: false); output.SetLength(0);
            try { using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true); gzip.Write(expanded); }
            catch (NotSupportedException) { return null; } // Fixed MemoryStream cannot expand.
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
