using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public enum LocalProcessorCommand { Show, Status, Pause, Resume, Stop }
public sealed record LocalProcessorReply(bool Ok, string State, string? Error, int ProcessId = 0);

/// <summary>Current-OS-user control only. No Google credentials, media, arbitrary paths or jobs
/// cross this endpoint. Phone/LAN pairing is a different authenticated protocol.</summary>
public sealed class LocalProcessorControl : IAsyncDisposable
{
    private const int FrameLimit = 8 * 1024;
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<LocalProcessorCommand, CancellationToken, Task<LocalProcessorReply>> _handler;
    private readonly Action? _stopAcknowledged;
    private readonly object _gate = new();
    private Task? _disposal;
    public Task Completion { get; }
    public int RejectedConnections => Volatile.Read(ref _rejected);
    private int _rejected;

    public LocalProcessorControl(string stateDirectory, Func<LocalProcessorCommand, CancellationToken, Task<LocalProcessorReply>> handler,
        Action? stopAcknowledged = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler; _stopAcknowledged = stopAcknowledged;
        // Protocol framing owns the 8 KiB limit. Leave transport buffers at OS defaults: .NET
        // sets explicit Unix socket buffers after accept, which can fail on macOS when a peer
        // disconnects immediately. A closed client must not terminate the command listener.
        _pipe = new NamedPipeServerStream(PipeName(stateDirectory), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 0, 0);
        Completion = ServeAsync();
    }

    public static string PipeName(string stateDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        // macOS per-user TMPDIR already consumes much of its 104-byte Unix socket path limit.
        // Keep the full 128-bit state digest; shorten only the nonsecret protocol label.
        return "mpac1-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32];
    }

    public static async Task<LocalProcessorReply?> TrySendAsync(string stateDirectory, LocalProcessorCommand command, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
        using var connection = new NamedPipeClientStream(".", PipeName(stateDirectory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectDeadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        try { await connection.ConnectAsync(connectDeadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var id = Guid.NewGuid().ToString("D");
        using var writer = new WorkerFrameWriter(connection, FrameLimit);
        await writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-control", major = 1, requestId = id, command = command.ToString().ToLowerInvariant() }), deadline.Token).ConfigureAwait(false);
        var body = await new WorkerFrameReader(connection, FrameLimit).ReadAsync(deadline.Token).ConfigureAwait(false)
            ?? throw new IOException("Processor disconnected before acknowledging the command.");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        ValidateRoot(root, 8);
        if (Text(root, "requestId") != id || Text(root, "kind") != "reply" || !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ProtocolFormatException("Local control response identity/outcome is invalid.");
        var state = Text(root, "state");
        if (state.Length is < 1 or > 64 || !root.TryGetProperty("error", out var error) || error.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new ProtocolFormatException("Local control response state/error is invalid.");
        if (!root.TryGetProperty("processId", out var process) || process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ProtocolFormatException("Invalid local Processor process identity.");
        await writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-control", major = 1, requestId = id, kind = "ack" }), deadline.Token).ConfigureAwait(false);
        return new(ok.GetBoolean(), state, error.GetString(), processId);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var acknowledgedStop = false;
                var executingHandler = false;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                try
                {
                    var bytes = await new WorkerFrameReader(_pipe, FrameLimit).ReadAsync(deadline.Token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Local control client disconnected before a command.");
                    using var document = JsonDocument.Parse(bytes);
                    var root = document.RootElement;
                    ValidateRoot(root, 4);
                    var id = Text(root, "requestId");
                    if (!Guid.TryParseExact(id, "D", out var request) || request == Guid.Empty || request.ToString("D") != id)
                        throw new ProtocolFormatException("Invalid local control command/identity.");
                    var command = Text(root, "command") switch
                    {
                        "show" => LocalProcessorCommand.Show, "status" => LocalProcessorCommand.Status, "pause" => LocalProcessorCommand.Pause,
                        "resume" => LocalProcessorCommand.Resume, "stop" => LocalProcessorCommand.Stop,
                        _ => throw new ProtocolFormatException("Unknown local control command.")
                    };
                    executingHandler = true;
                    var result = await _handler(command, deadline.Token).ConfigureAwait(false);
                    executingHandler = false;
                    acknowledgedStop = command == LocalProcessorCommand.Stop && result.Ok;
                    using var writer = new WorkerFrameWriter(_pipe, FrameLimit);
                    await writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-control", major = 1, requestId = id,
                        ok = result.Ok, state = result.State, error = result.Error, kind = "reply", processId = Environment.ProcessId }), deadline.Token).ConfigureAwait(false);
                    // Windows Disconnect can discard unread pipe buffers. Keep the connection
                    // until the client confirms receipt, with a bound if that client disappears.
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var acknowledgement = await new WorkerFrameReader(_pipe, FrameLimit).ReadAsync(deadline.Token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Local control client did not acknowledge its response.");
                    using var ackDocument = JsonDocument.Parse(acknowledgement);
                    ValidateRoot(ackDocument.RootElement, 4);
                    if (Text(ackDocument.RootElement, "requestId") != id || Text(ackDocument.RootElement, "kind") != "ack")
                        throw new ProtocolFormatException("Local control acknowledgement does not match its command.");
                }
                catch (Exception error) when (!executingHandler && (error is ProtocolFormatException or EndOfStreamException || error is IOException && !_stop.IsCancellationRequested ||
                    error is OperationCanceledException && !_stop.IsCancellationRequested))
                { Interlocked.Increment(ref _rejected); } // Invalid/disconnected local client; never execute an unparsed command.
                // A peer that closes immediately can mark a Windows pipe Broken. IsConnected
                // is then false, but Disconnect is still required before the next accept.
                finally { if (!_stop.IsCancellationRequested) _pipe.Disconnect(); }
                if (acknowledgedStop) _stopAcknowledged?.Invoke(); // After reply, never dispose/join this server from inside its handler.
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Owner joined its listener. */ }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { /* Owner closed a partial connection. */ }
        catch (IOException) when (_stop.IsCancellationRequested) { /* Owner interrupted pending pipe I/O. */ }
    }

    private static void ValidateRoot(JsonElement root, int fields)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields || Text(root, "format") != "mpa-control" ||
            !root.TryGetProperty("major", out var major) || major.ValueKind != JsonValueKind.Number || !major.TryGetInt32(out var version) || version != 1)
            throw new ProtocolFormatException("Invalid local control protocol envelope.");
    }
    private static string Text(JsonElement root, string field) => root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ProtocolFormatException("Missing local control field: " + field);

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _pipe.Dispose();
        try { await Completion.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
