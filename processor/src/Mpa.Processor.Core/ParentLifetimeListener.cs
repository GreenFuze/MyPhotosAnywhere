using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Mpa.Processor.WorkerRuntime;

namespace Mpa.Processor.Core;

/// <summary>Owns the parent's loopback listener/connection. Keep it alive until the child actually
/// exits; disposing it early intentionally signals loss of the owning controller. Shutdown
/// joins the actual accept/handshake task before releasing its cancellation/key owner.</summary>
public sealed class ParentLifetimeListener : IDisposable, IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Socket _acceptor;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private TcpClient? _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _ownershipGate = new();
    private Task? _acceptance;
    private Task? _disposal;
    private int _disposed;
    public ParentLifetimeConfiguration Configuration { get; }

    public ParentLifetimeListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Start(1);
            _acceptor = _listener.Server;
            Configuration = new(((IPEndPoint)_listener.LocalEndpoint).Port, Convert.ToBase64String(_key));
        }
        catch { _listener.Stop(); CryptographicOperations.ZeroMemory(_key); _stop.Dispose(); throw; }
    }

    public Task AcceptAsync(CancellationToken cancellationToken)
    {
        lock (_ownershipGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_acceptance is not null) throw new InvalidOperationException("A parent-lifetime listener accepts one worker session only.");
            return _acceptance = AcceptCoreAsync(cancellationToken);
        }
    }
    private async Task AcceptCoreAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                // Own the destination socket before starting AcceptAsync. If the
                // accept completes/faults during shutdown, no runtime-created socket
                // can sit between completion and this owner's adoption/cleanup.
                var candidate = new TcpClient(AddressFamily.InterNetwork);
                var destination = candidate.Client;
                var owned = true;
                try
                {
                    lock (_ownershipGate)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    }
                    var accepted = await _acceptor.AcceptAsync(destination, deadline.Token).ConfigureAwait(false);
                    if (!ReferenceEquals(accepted, destination))
                    {
                        accepted.Dispose();
                        throw new InvalidOperationException("Parent accept returned a socket outside its pre-established ownership.");
                    }
                    NetworkStream stream;
                    lock (_ownershipGate)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                        candidate.NoDelay = true; stream = candidate.GetStream();
                    }
                    var key = new byte[32];
                    try
                    {
                        await stream.ReadExactlyAsync(key, deadline.Token).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(_key, key)) continue;
                    }
                    catch (EndOfStreamException) { continue; }
                    finally { CryptographicOperations.ZeroMemory(key); }
                    await stream.WriteAsync(new byte[] { 0xac }, deadline.Token).ConfigureAwait(false);
                    lock (_ownershipGate)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                        _client = candidate;
                        owned = false;
                    }
                    return;
                }
                finally
                {
                    if (owned) candidate.Dispose();
                }
            }
            throw new InvalidDataException("Parent-lifetime session rejected too many invalid local connections.");
        }
        finally { _listener.Stop(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        lock (_ownershipGate)
        {
            _disposed = 1;
            return new(_disposal ??= StopAsync(_acceptance));
        }
    }
    private async Task StopAsync(Task? acceptance)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        lock (_ownershipGate) _client?.Dispose();
        try
        {
            if (acceptance is not null)
            {
                try { await acceptance.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Joined canceled accept/partial handshake. */ }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { /* Closed acceptor or admitted stream. */ }
                catch (SocketException) when (_stop.IsCancellationRequested) { /* Closed native accept/read. */ }
                catch (IOException) when (_stop.IsCancellationRequested) { /* Partial stream aborted; the returned accept task retains the error. */ }
            }
        }
        finally { CryptographicOperations.ZeroMemory(_key); _stop.Dispose(); }
    }
}
