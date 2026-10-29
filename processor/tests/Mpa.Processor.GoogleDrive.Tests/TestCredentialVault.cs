using Mpa.Processor.Credentials;

namespace Mpa.Processor.GoogleDrive.Tests;

internal sealed class TestCredentialVault : ICredentialVault
{
    public Dictionary<string, byte[]> Records { get; } = new(StringComparer.Ordinal);
    public Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Records.TryGetValue(key, out var bytes) ? SecretLease.CopyFrom(bytes) : null);
    }
    public Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Records[key] = bytes.ToArray();
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Records.Remove(key);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
