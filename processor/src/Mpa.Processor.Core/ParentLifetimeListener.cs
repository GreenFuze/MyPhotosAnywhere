using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Mpa.Processor.WorkerRuntime;

namespace Mpa.Processor.Core;

/// <summary>Owns the parent's loopback listener/connection. Keep it alive until the child actually
/// exits; disposing it early intentionally signals loss of the owning controller.</summary>
public sealed class ParentLifetimeListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private TcpClient? _client;
    private TcpClient? _pending;
    private readonly object _ownershipGate = new();
    private int _accepting;
    private int _disposed;
    public ParentLifetimeConfiguration Configuration { get; }

    public ParentLifetimeListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Start(1);
            Configuration = new(((IPEndPoint)_listener.LocalEndpoint).Port, Convert.ToBase64String(_key));
        }
        catch { _listener.Stop(); CryptographicOperations.ZeroMemory(_key); throw; }
    }

    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _accepting, 1) != 0) throw new InvalidOperationException("A parent-lifetime listener accepts one worker session only.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = await _listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
                var owned = true;
                try
                {
                    lock (_ownershipGate)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                        _pending = candidate;
                    }
                    candidate.NoDelay = true;
                    var key = new byte[32];
                    try
                    {
                        await candidate.GetStream().ReadExactlyAsync(key, deadline.Token).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(_key, key)) continue;
                    }
                    catch (EndOfStreamException) { continue; }
                    finally { CryptographicOperations.ZeroMemory(key); }
                    await candidate.GetStream().WriteAsync(new byte[] { 0xac }, deadline.Token).ConfigureAwait(false);
                    lock (_ownershipGate)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                        _client = candidate;
                        _pending = null;
                        owned = false;
                    }
                    return;
                }
                finally
                {
                    lock (_ownershipGate) { if (ReferenceEquals(_pending, candidate)) _pending = null; }
                    if (owned) candidate.Dispose();
                }
            }
            throw new InvalidDataException("Parent-lifetime session rejected too many invalid local connections.");
        }
        finally { _listener.Stop(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _listener.Stop();
        lock (_ownershipGate)
        {
            _pending?.Dispose();
            _client?.Dispose();
        }
        CryptographicOperations.ZeroMemory(_key);
    }
}
