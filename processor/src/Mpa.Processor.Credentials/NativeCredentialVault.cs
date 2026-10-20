using System.Security.Cryptography;
using System.Text;

namespace Mpa.Processor.Credentials;

/// <summary>One owner serializes and joins native secret-store calls. No plaintext fallback.
/// Cancellation does not imply a write was rolled back: reread before retrying an uncertain write.</summary>
public sealed class NativeCredentialVault : IAsyncDisposable
{
    public const int MaximumSecretBytes = 128 * 1024;
    private readonly ISecretBackend _backend;
    private readonly string _prefix;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private Task? _disposal;
    private bool _stopping;

    public NativeCredentialVault(Guid installationId, string protectedDirectory)
    {
        if (installationId == Guid.Empty) throw new ArgumentException("An installation identity is required.", nameof(installationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedDirectory);
        _prefix = installationId.ToString("N") + ":";
        try
        {
            _backend = OperatingSystem.IsWindows() ? new WindowsSecretBackend(protectedDirectory, installationId)
                : OperatingSystem.IsMacOS() ? new MacKeychainBackend()
                : OperatingSystem.IsLinux() ? new LinuxSecretBackend()
                : throw new PlatformNotSupportedException("No supported OS credential store is available.");
        }
        catch (DllNotFoundException) { throw MissingNativeStore(); }
    }

    internal NativeCredentialVault(Guid installationId, ISecretBackend backend)
    {
        _prefix = installationId.ToString("N") + ":";
        _backend = backend;
    }

    public async Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var address = Address(key);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await RunNativeAsync(() => _backend.Read(address, cancellationToken)).ConfigureAwait(false);
            if (bytes is null) return null;
            if (bytes.Length is <= 0 or > MaximumSecretBytes || cancellationToken.IsCancellationRequested)
            {
                CryptographicOperations.ZeroMemory(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidDataException("Credential record exceeds its supported bounds.");
            }
            return new SecretLease(bytes);
        }
        finally { _serial.Release(); }
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var address = Address(key);
        if (bytes.Length is <= 0 or > MaximumSecretBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        byte[]? owned = null;
        try
        {
            owned = bytes.ToArray();
            await RunNativeAsync(() => { _backend.Write(address, owned, cancellationToken); return true; }).ConfigureAwait(false);
        }
        finally
        {
            if (owned is not null) CryptographicOperations.ZeroMemory(owned);
            _serial.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var address = Address(key);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { await RunNativeAsync(() => { _backend.Delete(address, cancellationToken); return true; }).ConfigureAwait(false); }
        finally { _serial.Release(); }
    }

    private static async Task<T> RunNativeAsync<T>(Func<T> operation)
    {
        try { return await Task.Run(operation, CancellationToken.None).ConfigureAwait(false); }
        catch (DllNotFoundException) { throw MissingNativeStore(); }
        catch (EntryPointNotFoundException) { throw MissingNativeStore(); }
    }

    private static CredentialVaultException MissingNativeStore() => new("The OS credential store is unavailable. On Linux, install libsecret and enable the desktop login keyring Secret Service. No credential was stored in plaintext.");

    private string Address(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 1024 || key.Any(char.IsControl)) throw new ArgumentException("Invalid credential identity.", nameof(key));
        return _prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_stopping) return;
            _serial.Release();
            throw new ObjectDisposedException(nameof(NativeCredentialVault));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return new(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _backend.Dispose(); }
        finally { _serial.Release(); }
        // Do not dispose the gate: already queued entrants must wake, observe stopping and fail.
    }
}
