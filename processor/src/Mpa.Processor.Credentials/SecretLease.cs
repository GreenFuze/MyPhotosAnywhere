using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Mpa.Processor.Credentials;

/// <summary>Owns a caller-visible secret buffer. Dispose after use; never serialize or log this object.</summary>
public sealed class SecretLease : IDisposable
{
    private byte[]? _bytes;
    internal SecretLease(byte[] bytes) => _bytes = bytes;
    public static SecretLease CopyFrom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > NativeCredentialVault.MaximumSecretBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        return new(bytes.ToArray());
    }
    [JsonIgnore]
    public ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(SecretLease));
    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
    public override string ToString() => "[protected credential]";
}

public sealed class CredentialVaultException(string message) : Exception(message);

internal interface ISecretBackend : IDisposable
{
    byte[]? Read(string address, CancellationToken cancellationToken);
    void Write(string address, byte[] bytes, CancellationToken cancellationToken);
    void Delete(string address, CancellationToken cancellationToken);
}
