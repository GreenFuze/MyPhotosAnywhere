using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Serial owner of private catalog preparation. The awaited consumer borrows
/// its frozen copy only for this operation; it must join any compression/network work.
/// Exit cancels and joins actual work before deleting generated files or releasing state.
/// Scheduler admission, signed authority and publication remain the consumer's duties.</summary>
public sealed class CatalogPreparationService(ProcessorStateCoordinator state, string stateDirectory, Guid installationId, StorageBudgetManager storage) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly string _stateDirectory = Path.GetFullPath(stateDirectory);
    private readonly Guid _installation = installationId != Guid.Empty ? installationId : throw new ArgumentException("Installation identity is required.", nameof(installationId));
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<FrozenCatalogSnapshot> _captures = [];
    private readonly HashSet<CatalogFactsSnapshot> _sources = [];
    private CatalogPreparationDirectory? _directory;
    private StorageReservation? _directoryReservation;
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock (_gate) return _failure; } }

    public async Task<T> RunAsync<T>(Func<FrozenCatalogSnapshot, CancellationToken, Task<T>> consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer); CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_failure is not null) throw new InvalidOperationException("Catalog preparation cleanup is pending; resources remain owned.", _failure);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        }
        using (linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_stopping, this);
                    if (_failure is not null) throw new InvalidOperationException("Catalog preparation resources still need cleanup.", _failure);
                }
                linked.Token.ThrowIfCancellationRequested(); EnsureDirectory();
                var live = await _state.ExecuteAsync(CatalogFactsSnapshot.Open, linked.Token).ConfigureAwait(false); _sources.Add(live);
                FrozenCatalogSnapshot frozen;
                try
                {
                    frozen = await Task.Run(() => FrozenCatalogSnapshot.CaptureAndRelease(live, _directory!, _storage, linked.Token), CancellationToken.None).ConfigureAwait(false);
                    _captures.Add(frozen);
                }
                catch (Exception error)
                {
                    if (error is FrozenCatalogCaptureException retained) { _captures.Add(retained.RetainedOwner); Retain(error); }
                    try { Close(live); }
                    catch (Exception cleanup) { throw new AggregateException("Catalog capture and source cleanup failed.", error, cleanup); }
                    throw;
                }
                Close(live); T result;
                try { linked.Token.ThrowIfCancellationRequested(); result = await consumer(frozen, linked.Token).ConfigureAwait(false); }
                catch (Exception error)
                {
                    try { Close(frozen); }
                    catch (Exception cleanup) { throw new AggregateException("Catalog operation and generated cleanup failed.", error, cleanup); }
                    throw;
                }
                Close(frozen); return result;
            }
            finally { _serial.Release(); }
        }
    }
    private void Close(CatalogFactsSnapshot live)
    {
        try { live.Dispose(); _sources.Remove(live); }
        catch (Exception error) { Retain(error); throw; }
    }
    private void Close(FrozenCatalogSnapshot frozen)
    {
        try { frozen.Dispose(); _captures.Remove(frozen); }
        catch (Exception error) { Retain(error); throw; }
    }
    private void EnsureDirectory()
    {
        if (_directory is not null) return;
        _directoryReservation ??= _storage.Reserve(65536); _directoryReservation.RecheckBeforeWrite(_directoryReservation.Bytes);
        try { _directory = new(_stateDirectory, _installation); }
        catch (CatalogPreparationDirectoryStartException error) { _directory = error.RetainedOwner; Retain(error); throw; }
    }
    private void Retain(Exception error) { lock (_gate) _failure = _failure is null ? error : new AggregateException(_failure, error); }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_disposal is { IsFaulted: true } && !_exitConfirmed) _disposal = null;
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        var errors = new List<Exception>();
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var capture in _captures.ToArray())
                try { capture.Dispose(); _captures.Remove(capture); } catch (Exception error) { errors.Add(error); }
            foreach (var source in _sources.ToArray())
                try { source.Dispose(); _sources.Remove(source); } catch (Exception error) { errors.Add(error); }
            if (_captures.Count == 0 && _sources.Count == 0)
                try { _directory?.Dispose(); _directory = null; } catch (Exception error) { errors.Add(error); }
            if (_directory is null && _captures.Count == 0 && _sources.Count == 0)
            {
                _directoryReservation?.Dispose(); _directoryReservation = null; _stop.Dispose(); lock (_gate) _exitConfirmed = true;
            }
            if (errors.Count != 0) { var error = new AggregateException("Catalog preparation shutdown needs attention; unclosed resources remain owned.", errors); Retain(error); throw error; }
        }
        finally { _serial.Release(); }
    }
}
