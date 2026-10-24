namespace Mpa.Processor.Core;

public sealed record CatalogViewerRequest(Guid Id, Guid SelectedId, string? AccountId);
public sealed record CatalogViewerWindow(Guid Id, long Count, long Position, IReadOnlyList<CatalogSessionEntry> Entries);
public sealed class CatalogViewerUnavailableException() : IOException("This photo viewer session is unavailable. Return to the gallery to reopen the same photo.");
public sealed class CatalogViewerCapacityException() : IOException("Close another photo viewer on this library, then try again. Existing viewers and progress were retained.");

/// <summary>One serial owner for bounded, browser-scoped disposable identity indexes. It
/// borrows the state coordinator and storage budget, joins capture before Exit, and never
/// permits another browser to read, close or overwrite an existing session.</summary>
public sealed class CatalogViewerSessions(ProcessorStateCoordinator state, string stateDirectory, Guid installationId, StorageBudgetManager storage, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Guid _installationId = installationId != Guid.Empty ? installationId : throw new ArgumentException("Installation identity is required.", nameof(installationId));
    private readonly string _stateDirectory = Path.GetFullPath(stateDirectory);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Owner, Guid Id), Session> _sessions = [];
    private readonly List<CatalogViewerSessionIndex> _retained = [];
    private readonly List<ProcessorStateReadSnapshot> _snapshots = [];
    private CatalogSessionDirectory? _directory;
    private StorageReservation? _directoryReservation;
    private bool _stopping;
    private bool _exitConfirmed;
    private Task? _disposal;
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Task<CatalogViewerWindow> OpenAsync(Guid browser, CatalogViewerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (browser == Guid.Empty || request.Id == Guid.Empty || request.SelectedId == Guid.Empty) throw new ArgumentException("Captured browser, session and media identities are required.");
        return ExecuteAsync(async token =>
        {
            Prune();
            if (_sessions.TryGetValue((browser, request.Id), out var current))
            {
                if (current.Request != request) throw new CatalogViewerUnavailableException();
                return await WindowAsync(current, current.Index.SelectedPosition).ConfigureAwait(false);
            }
            if (_sessions.Count + _retained.Count >= 8 || _sessions.Keys.Count(key => key.Owner == browser) >= 2) throw new CatalogViewerCapacityException();
            if (_directory is null)
            {
                _directoryReservation ??= _storage.Reserve(8192); _directoryReservation.RecheckBeforeWrite(_directoryReservation.Bytes);
                try { _directory = new(_stateDirectory, _installationId); }
                catch (CatalogSessionDirectoryStartException error) { _directory = error.RetainedOwner; throw; }
            }
            CatalogViewerSessionIndex index;
            try
            {
                var path = _directory.NewIndexPath();
                var snapshot = await _state.ExecuteAsync(database => database.OpenReadSnapshot(), token).ConfigureAwait(false);
                _snapshots.Add(snapshot);
                try
                {
                    index = await Task.Run(() => CatalogViewerSessionIndex.Capture(snapshot, path, request.SelectedId, request.AccountId, _storage, token), CancellationToken.None).ConfigureAwait(false);
                    _retained.Add(index);
                }
                finally { snapshot.Dispose(); _snapshots.Remove(snapshot); }
            }
            catch (CatalogSessionCaptureException error) { _retained.Add(error.RetainedOwner); throw; }
            var session = new Session(browser, request, index, _time.GetTimestamp());
            _sessions.Add((browser, request.Id), session); _retained.Remove(index);
            // The snapshot may have finished just as the HTTP caller canceled. Retain
            // ownership until actual cleanup; never orphan a completed private index.
            if (token.IsCancellationRequested)
            { Close(session); token.ThrowIfCancellationRequested(); }
            return await WindowAsync(session, index.SelectedPosition).ConfigureAwait(false);
        }, cancellationToken);
    }
    public Task<CatalogViewerWindow> ReadAsync(Guid browser, Guid id, long position, CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        Prune();
        if (!_sessions.TryGetValue((browser, id), out var session) || position < 0 || position >= session.Index.Count) throw new CatalogViewerUnavailableException();
        return await WindowAsync(session, position).ConfigureAwait(false);
    }, cancellationToken);
    public Task CloseAsync(Guid browser, Guid id, CancellationToken cancellationToken) => ExecuteAsync(_ =>
    {
        if (_sessions.TryGetValue((browser, id), out var session)) Close(session);
        return Task.FromResult(true);
    }, cancellationToken);
    public Task CloseBrowserAsync(Guid browser, CancellationToken cancellationToken) => ExecuteAsync(_ =>
    {
        foreach (var session in _sessions.Values.Where(session => session.Browser == browser).ToArray()) Close(session);
        return Task.FromResult(true);
    }, cancellationToken);
    private async Task<CatalogViewerWindow> WindowAsync(Session session, long position)
    {
        var entries = await Task.Run(() => session.Index.ReadWindow(position), CancellationToken.None).ConfigureAwait(false);
        session.Touched = _time.GetTimestamp(); return new(session.Request.Id, session.Index.Count, position, entries);
    }
    private void Prune()
    {
        var now = _time.GetTimestamp();
        foreach (var session in _sessions.Values.Where(session => _time.GetElapsedTime(session.Touched, now) >= TimeSpan.FromMinutes(15)).ToArray()) Close(session);
    }
    private void Close(Session session)
    { session.Index.Dispose(); _sessions.Remove((session.Browser, session.Request.Id)); }
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        CancellationToken stopping;
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping, this); stopping = _stop.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this); linked.Token.ThrowIfCancellationRequested(); return await operation(linked.Token).ConfigureAwait(false); }
        finally { _serial.Release(); }
    }
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
        await _stop.CancelAsync().ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            foreach (var session in _sessions.Values.ToArray()) { try { Close(session); } catch (Exception error) { errors.Add(error); } }
            foreach (var retained in _retained.ToArray()) { try { retained.Dispose(); _retained.Remove(retained); } catch (Exception error) { errors.Add(error); } }
            foreach (var snapshot in _snapshots.ToArray()) { try { snapshot.Dispose(); _snapshots.Remove(snapshot); } catch (Exception error) { errors.Add(error); } }
            if (errors.Count != 0) throw new AggregateException("Private viewer indexes have not closed; ownership is retained.", errors);
            _directory?.Dispose(); _directory = null; _directoryReservation?.Dispose(); _directoryReservation = null;
            _stop.Dispose(); lock (_gate) _exitConfirmed = true;
        }
        finally { _serial.Release(); }
    }
    private sealed class Session(Guid browser, CatalogViewerRequest request, CatalogViewerSessionIndex index, long touched)
    {
        public Guid Browser { get; } = browser;
        public CatalogViewerRequest Request { get; } = request;
        public CatalogViewerSessionIndex Index { get; } = index;
        public long Touched { get; set; } = touched;
    }
}
