using System.Buffers.Binary;

namespace Mpa.Processor.Protocol;

/// <summary>Serial reader borrowing a pipe owned by the worker process lease. A partial/faulted
/// read poisons this channel; restart the worker rather than interpreting the remaining bytes.</summary>
public sealed class WorkerFrameReader(Stream input, int maximumFrameBytes = 1024 * 1024)
{
    private readonly Stream _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly CanonicalJson _codec = maximumFrameBytes is >= 1 and <= 1024 * 1024 ? new(maximumFrameBytes) :
        throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
    private readonly int _maximumFrameBytes = maximumFrameBytes;
    private int _reading;
    private bool _poisoned;
    private bool _ended;

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Worker pipe has concurrent readers.");
        try
        {
            if (_poisoned) throw new InvalidOperationException("Worker channel is faulted and must be replaced.");
            if (_ended) return null;
            var header = new byte[4];
            if (await _input.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
            {
                _ended = true;
                return null;
            }
            await _input.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length == 0 || length > _maximumFrameBytes) throw new ProtocolFormatException("Worker frame exceeds its bound.");
            var body = new byte[(int)length];
            await _input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            if (!body.AsSpan().SequenceEqual(_codec.Encode(body))) throw new ProtocolFormatException("Worker frame must contain canonical JSON.");
            return body;
        }
        catch (EndOfStreamException error)
        {
            _poisoned = true;
            throw new ProtocolFormatException("Truncated worker frame.", error);
        }
        catch
        {
            _poisoned = true;
            throw;
        }
        finally { Volatile.Write(ref _reading, 0); }
    }
}

/// <summary>Owns its write gate, borrows the output pipe. The process lease must join all writes
/// before disposing the gate/pipes. Cancellation after framing begins invalidates the channel.</summary>
public sealed class WorkerFrameWriter : IDisposable
{
    private readonly Stream _output;
    private readonly CanonicalJson _codec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _poisoned;
    private bool _disposed;

    public WorkerFrameWriter(Stream output, int maximumFrameBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maximumFrameBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        _output = output;
        _codec = new(maximumFrameBytes);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = _codec.Encode(document.Span);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_poisoned) throw new InvalidOperationException("Worker output channel is faulted and must be replaced.");
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)bytes.Length));
            await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { _poisoned = true; throw; }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
