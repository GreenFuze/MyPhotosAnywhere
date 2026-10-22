using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Owns native signing keys under the Processor instance lease. The vault is
/// borrowed; joining initialization/disposal precedes vault shutdown. Setup alone may
/// use its public identity to bootstrap a controller after independent owner approval.
/// Signing callers must reserve durable sequences and journal exact signed envelopes.</summary>
public sealed class LibrarySigningIdentityOwner : IAsyncDisposable
{
    private readonly ICredentialVault _vault;
    private readonly Guid _library;
    private readonly Guid _installation;
    private readonly LibraryControllerPin? _expectedController;
    private readonly string _address;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _initialization;
    private Task? _disposal;
    private LibrarySigningKeys? _keys;
    private bool _stopping;
    public Exception? Failure { get; private set; }

    public LibrarySigningIdentityOwner(ICredentialVault vault, Guid libraryId, Guid installationId, LibraryControllerPin? expectedController)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (libraryId == Guid.Empty || installationId == Guid.Empty || expectedController is not null && expectedController.LibraryId != libraryId)
            throw new ArgumentException("Captured library/installation and approved controller context are required.");
        _vault = vault; _library = libraryId; _installation = installationId; _expectedController = expectedController;
        _address = "library-signing-v1:" + installationId.ToString("N") + ":" + libraryId.ToString("N");
    }
    public LibrarySigningPublicIdentity PublicIdentity { get { lock (_gate) return Ready().Public; } }
    public DocumentSignature SignControllerDocument(ReadOnlySpan<byte> canonicalDocument) { lock (_gate) return Ready().Sign(true, canonicalDocument); }
    public DocumentSignature SignCatalogDocument(ReadOnlySpan<byte> canonicalDocument) { lock (_gate) return Ready().Sign(false, canonicalDocument); }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_initialization is not null) throw new InvalidOperationException("This native signing owner already initialized.");
            return _initialization = Task.Run(() => InitializeCoreAsync(cancellationToken), CancellationToken.None);
        }
    }
    private LibrarySigningKeys Ready()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        return _initialization?.IsCompletedSuccessfully == true && _keys is not null ? _keys : throw new InvalidOperationException("Native signing identity is not ready.");
    }
    private async Task InitializeCoreAsync(CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, caller); LibrarySigningKeys? candidate = null;
        try
        {
            ThrowIfCanceled(caller);
            using var record = await _vault.ReadAsync(_address, linked.Token).ConfigureAwait(false);
            ThrowIfCanceled(caller);
            if (record is null && _expectedController is not null) throw new InvalidDataException("The pinned controller's private signing keys are missing. Preserve state and recover them; never replace the root.");
            candidate = record is null ? LibrarySigningKeys.Create(_library, _installation) : LibrarySigningKeys.Decode(record.Bytes.Span, _library, _installation);
            if (_expectedController is not null && (_expectedController.ActorId != candidate.Public.ControllerId || _expectedController.Epoch != candidate.Public.ControllerEpoch ||
                _expectedController.KeyId != candidate.Public.ControllerKeyId || _expectedController.PublicKeySpki != candidate.Public.ControllerPublicKeySpki))
                throw new InvalidDataException("Native signing identity no longer matches the independently pinned controller.");
            if (record is null)
            {
                var bytes = candidate.EncodePrivateRecord();
                try
                {
                    ThrowIfCanceled(caller); await _vault.WriteAsync(_address, bytes, linked.Token).ConfigureAwait(false);
                    ThrowIfCanceled(caller);
                    using var saved = await _vault.ReadAsync(_address, linked.Token).ConfigureAwait(false);
                    ThrowIfCanceled(caller);
                    if (saved is null || !CryptographicOperations.FixedTimeEquals(saved.Bytes.Span, bytes))
                        throw new InvalidDataException("Native signing keys could not be read back exactly; do not enroll or replace the controller.");
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            lock (_gate) { ThrowIfCanceled(caller); ObjectDisposedException.ThrowIf(_stopping, this); _keys = candidate; candidate = null; }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || caller.IsCancellationRequested) { throw; }
        catch (Exception error) { Failure = error; throw; }
        finally { candidate?.Dispose(); }
    }
    private void ThrowIfCanceled(CancellationToken caller) { _stop.Token.ThrowIfCancellationRequested(); caller.ThrowIfCancellationRequested(); }
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
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Joined initialization; canceled keys never become available. */ }
                catch when (Failure is not null) { /* Returned initialization task and Failure retain the actual startup error. */ }
            }
        }
        finally { try { _keys?.Dispose(); } finally { _stop.Dispose(); } }
    }
}
