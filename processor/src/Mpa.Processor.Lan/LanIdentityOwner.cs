using System.Security.Cryptography;
using Mpa.Processor.Credentials;

namespace Mpa.Processor.Lan;

/// <summary>One startup owner beneath the durable Processor instance lease. It borrows the vault,
/// owns both native private keys/certificate, and must outlive all LAN connections using them.</summary>
public sealed class LanIdentityOwner : IAsyncDisposable
{
    private const string Address = "processor-lan-identity-v1";
    private readonly ICredentialVault _vault;
    private readonly Guid _libraryId;
    private readonly Guid _processorId;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _initialization;
    private Task? _disposal;
    private ProcessorLanIdentity? _identity;
    private bool _stopping;
    public Exception? Failure { get; private set; }

    public LanIdentityOwner(ICredentialVault vault, Guid libraryId, Guid processorId, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (libraryId == Guid.Empty || processorId == Guid.Empty) throw new ArgumentException("Stable library and Processor IDs are required.");
        _vault = vault; _libraryId = libraryId; _processorId = processorId; _time = time ?? TimeProvider.System;
    }
    public ProcessorLanIdentity Identity
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                return _initialization?.IsCompletedSuccessfully == true && _identity is not null ? _identity
                    : throw new InvalidOperationException("LAN identity is not ready; do not start a listener or advertise it.");
            }
        }
    }
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_initialization is not null) throw new InvalidOperationException("This LAN identity startup owner has already initialized.");
            return _initialization = Task.Run(() => InitializeCoreAsync(cancellationToken), CancellationToken.None);
        }
    }
    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        ProcessorLanIdentity? candidate = null;
        try
        {
            using var record = await _vault.ReadAsync(Address, linked.Token).ConfigureAwait(false);
            ThrowIfCanceled(cancellationToken);
            var save = record is null;
            candidate = record is null ? ProcessorLanIdentity.Create(_libraryId, _processorId, _time.GetUtcNow())
                : ProcessorLanIdentity.Decode(record.Bytes.Span, _libraryId, _processorId, _time.GetUtcNow(), out save);
            if (save)
            {
                var bytes = candidate.EncodePrivateRecord();
                try
                {
                    ThrowIfCanceled(cancellationToken);
                    await _vault.WriteAsync(Address, bytes, linked.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            lock (_gate)
            {
                ThrowIfCanceled(cancellationToken);
                if (_stopping) throw new OperationCanceledException(_stop.Token);
                _identity = candidate; candidate = null;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { Failure = error; throw; }
        finally { candidate?.Dispose(); }
    }
    private void ThrowIfCanceled(CancellationToken caller)
    {
        // CancelAsync marks the originating source immediately, but dispatches linked
        // callbacks asynchronously. A borrowed vault may also ignore cancellation.
        _stop.Token.ThrowIfCancellationRequested(); caller.ThrowIfCancellationRequested();
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) { _stopping = true; return new(_disposal ??= StopAsync()); }
    }
    private async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_initialization is not null)
            {
                try { await _initialization.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Joined owner cancellation; no identity was made available. */ }
                catch when (Failure is not null) { /* Initialization already failed and released its partial keys; Failure and its returned task retain that exact error. */ }
            }
        }
        finally { try { _identity?.Dispose(); } finally { _stop.Dispose(); } }
    }
}
