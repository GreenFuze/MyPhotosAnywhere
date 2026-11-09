using System.Buffers.Binary;

namespace Mpa.Processor.Protocol;

/// <summary>Require the RFC 1952 footer to cover the complete bounded body. GZipStream can
/// otherwise return all decoded bytes when the footer is truncated. SHA-256 remains the
/// authenticity/integrity check; CRC32 here checks the compressed format's framing.</summary>
internal static class CatalogGzipFooter
{
    private static readonly uint[] Table = BuildTable();
    public static void Validate(ReadOnlySpan<byte> compressed, ReadOnlySpan<byte> expanded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (compressed.Length < 18 || compressed[0] != 0x1f || compressed[1] != 0x8b || compressed[2] != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(compressed[^4..]) != (uint)expanded.Length)
            throw new ProtocolFormatException("Catalog gzip footer is missing or has an invalid expanded length.");
        var crc = uint.MaxValue;
        for (var index = 0; index < expanded.Length; index++)
        {
            if ((index & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            crc = (crc >> 8) ^ Table[(byte)(crc ^ expanded[index])];
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(compressed[^8..]) != ~crc)
            throw new ProtocolFormatException("Catalog gzip footer does not match the complete expanded body.");
    }
    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320U);
            table[index] = value;
        }
        return table;
    }
}
