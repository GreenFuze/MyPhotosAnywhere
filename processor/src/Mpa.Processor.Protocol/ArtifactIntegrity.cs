using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Mpa.Processor.Protocol;

public sealed class ArtifactDescriptor
{
    public long Bytes { get; }
    public string Sha256 { get; }
    public string RelativePath { get; }

    public ArtifactDescriptor(long bytes, string sha256, string relativePath)
    {
        if (bytes is < 1 or > 1024L * 1024 * 1024 || sha256 is null || sha256.Length != 64 ||
            sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolFormatException("Invalid artifact byte bound/digest.");
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > 240 || relativePath.Split('/').Length > 8 ||
            relativePath.Split('/').Any(segment => segment.Length is < 1 or > 80 || segment is "." or ".." ||
                segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
                segment.EndsWith('.') || IsReservedWindowsName(segment)))
            throw new ProtocolFormatException("Artifact path must be a portable relative path inside assigned staging.");
        Bytes = bytes; Sha256 = sha256; RelativePath = relativePath;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9');
    }
}

/// <summary>Borrowed stream: the lease owner disposes it. Cancellation propagates; no unverified
/// success marker or promoted file is produced by this verifier.</summary>
public sealed class ArtifactVerifier
{
    private readonly long _maximumBytes;

    public ArtifactVerifier(long maximumBytes = 1024L * 1024 * 1024)
    {
        if (maximumBytes is < 1 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    public async Task VerifyAsync(Stream input, ArtifactDescriptor expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        if (expected.Bytes > _maximumBytes) throw new ProtocolFormatException("Artifact exceeds this consumer's configured byte budget.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected.Bytes - total + 1)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                if (total > expected.Bytes) throw new ProtocolFormatException("Artifact exceeds its declared byte length.");
                digest.AppendData(buffer, 0, count);
            }
            if (total != expected.Bytes || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(expected.Sha256)))
                throw new ProtocolFormatException("Artifact is truncated or its digest changed.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

/// <summary>A compressed catalog is never parsed until compressed AND expanded bytes are bounded
/// and verified. Chunk entry/field schemas and manifest authority are subsequent checks.</summary>
public sealed class CatalogChunkDecoder
{
    public const int MaximumCompressedBytes = 4 * 1024 * 1024;
    public const int MaximumExpandedBytes = 16 * 1024 * 1024;
    private readonly int _compressedLimit;
    private readonly int _expandedLimit;
    private readonly ArtifactVerifier _verifier;

    public CatalogChunkDecoder(int compressedLimit = MaximumCompressedBytes, int expandedLimit = MaximumExpandedBytes)
    {
        if (compressedLimit is < 1 or > MaximumCompressedBytes) throw new ArgumentOutOfRangeException(nameof(compressedLimit));
        if (expandedLimit is < 1 or > MaximumExpandedBytes) throw new ArgumentOutOfRangeException(nameof(expandedLimit));
        _compressedLimit = compressedLimit;
        _expandedLimit = expandedLimit;
        _verifier = new ArtifactVerifier(Math.Max(compressedLimit, expandedLimit));
    }

    public Task<byte[]> DecodeAsync(ReadOnlyMemory<byte> compressed, CatalogChunkReference expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return DecodeAsync(compressed, new ArtifactDescriptor(expected.Encoded.Bytes, expected.Encoded.Sha256, "chunk.json.gz"),
            new ArtifactDescriptor(expected.ExpandedBytes, expected.ExpandedSha256, "chunk.json"), cancellationToken);
    }

    public async Task<byte[]> DecodeAsync(ReadOnlyMemory<byte> compressed, ArtifactDescriptor encoded,
        ArtifactDescriptor expanded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(expanded);
        cancellationToken.ThrowIfCancellationRequested();
        if (compressed.IsEmpty || compressed.Length > _compressedLimit || encoded.Bytes != compressed.Length || expanded.Bytes > _expandedLimit)
            throw new ProtocolFormatException("Catalog chunk exceeds its transport/expanded bound.");
        // Copy the borrowed memory once: its caller cannot change bytes between verification and decoding.
        var snapshot = compressed.ToArray();
        using var source = new MemoryStream(snapshot, writable: false);
        await _verifier.VerifyAsync(source, encoded, cancellationToken).ConfigureAwait(false);
        source.Position = 0;
        using var inflater = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream((int)expanded.Bytes);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var count = await inflater.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expanded.Bytes - output.Length + 1)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > expanded.Bytes) throw new ProtocolFormatException("Catalog decompression exceeds its declared bound.");
                output.Write(buffer, 0, count);
            }
        }
        catch (InvalidDataException error) { throw new ProtocolFormatException("Malformed compressed catalog.", error); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        output.Position = 0;
        await _verifier.VerifyAsync(output, expanded, cancellationToken).ConfigureAwait(false);
        var bytes = output.ToArray();
        CatalogGzipFooter.Validate(snapshot, bytes, cancellationToken);
        if (!bytes.AsSpan().SequenceEqual(new CanonicalJson().Encode(bytes)))
            throw new ProtocolFormatException("Expanded catalog bytes must already be canonical JSON.");
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }
}
