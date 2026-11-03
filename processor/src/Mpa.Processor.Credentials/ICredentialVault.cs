namespace Mpa.Processor.Credentials;

public interface ICredentialVault : IAsyncDisposable
{
    Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken);
    Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
