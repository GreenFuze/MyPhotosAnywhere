using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Mpa.Processor.WorkerRuntime;

/// <summary>Private one-launch loopback IPC, not LAN pairing or Google authorization. Do not log/export its key.</summary>
public sealed class ParentLifetimeConfiguration
{
    public int Port { get; }
    [JsonIgnore]
    public string SharedKey { get; }
    public ParentLifetimeConfiguration(int port, string sharedKey)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (sharedKey is null || sharedKey.Length != 44) throw new ArgumentException("Invalid parent-lifetime key length.", nameof(sharedKey));
        var bytes = Convert.FromBase64String(sharedKey);
        try
        {
            if (bytes.Length != 32 || Convert.ToBase64String(bytes) != sharedKey) throw new ArgumentException("Non-canonical parent-lifetime key.", nameof(sharedKey));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Port = port; SharedKey = sharedKey;
    }
    public override string ToString() => "Private parent-lifetime configuration (key omitted)";
}

public sealed record ParentLifetimeEnd(bool ParentLost, string Reason);

/// <summary>Owns its loopback socket and monitor. The worker host must observe Completion even
/// during native work, cancel on parent loss and enforce its own hard-exit deadline.</summary>
public sealed class ParentLifetimeGuard : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposalGate = new();
    private Task? _disposal;
    public Task<ParentLifetimeEnd> Completion { get; }

    private ParentLifetimeGuard(TcpClient client)
    {
        _client = client;
        Completion = MonitorAsync();
    }

    public static async Task<ParentLifetimeGuard> ConnectAsync(ParentLifetimeConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        var key = Convert.FromBase64String(configuration.SharedKey);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, configuration.Port, deadline.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(key, deadline.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await stream.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
            if (acknowledgement[0] != 0xac) throw new InvalidDataException("Parent-lifetime ownership handshake failed.");
            return new(client);
        }
        catch { client.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private async Task<ParentLifetimeEnd> MonitorAsync()
    {
        try
        {
            var probe = new byte[1];
            var count = await _client.GetStream().ReadAsync(probe, _stop.Token).ConfigureAwait(false);
            return _stop.IsCancellationRequested ? new(false, "disposed") : new(true, count == 0 ? "parent-closed" : "unexpected-parent-data");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return new(false, "disposed"); }
        catch (IOException) { return new(!_stop.IsCancellationRequested, _stop.IsCancellationRequested ? "disposed" : "parent-connection-lost"); }
        catch (SocketException) { return new(!_stop.IsCancellationRequested, _stop.IsCancellationRequested ? "disposed" : "parent-connection-lost"); }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { return new(false, "disposed"); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _client.Dispose();
        try { await Completion.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
