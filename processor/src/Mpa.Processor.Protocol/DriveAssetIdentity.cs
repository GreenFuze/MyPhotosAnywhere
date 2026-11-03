using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mpa.Processor.Protocol;

/// <summary>Identity derives from a representation, never a name/path/content duplicate.</summary>
public sealed class DriveAssetIdentity
{
    private static readonly byte[] Domain = "mpa:drive-representation:1\0"u8.ToArray();
    private readonly Guid _library;

    public DriveAssetIdentity(Guid library)
    {
        if (library == Guid.Empty) throw new ArgumentException("Library identity is required.", nameof(library));
        _library = library;
    }

    public Guid Derive(string accountPermissionId, string fileId)
    {
        CheckOpaqueId(accountPermissionId);
        CheckOpaqueId(fileId);
        var account = Encoding.UTF8.GetBytes(accountPermissionId);
        var file = Encoding.UTF8.GetBytes(fileId);
        using var source = new MemoryStream();
        source.Write(Domain);
        source.Write(_library.ToByteArray(bigEndian: true));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, account.Length);
        source.Write(length);
        source.Write(account);
        BinaryPrimitives.WriteInt32BigEndian(length, file.Length);
        source.Write(length);
        source.Write(file);
        var digest = SHA256.HashData(source.GetBuffer().AsSpan(0, checked((int)source.Length)));
        digest[6] = (byte)((digest[6] & 15) | 0x80); // RFC 9562 UUIDv8.
        digest[8] = (byte)((digest[8] & 63) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    private static void CheckOpaqueId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 256 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ProtocolFormatException("Account/file identity must be an opaque Drive identity, not an email or path.");
    }
}
