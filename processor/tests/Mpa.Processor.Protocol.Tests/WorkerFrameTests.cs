using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class WorkerFrameTests
{
    [TestMethod]
    public async Task ConcurrentWritersRemainWholeAndCleanEofDoesNotOwnTheStream()
    {
        using var stream = new MemoryStream();
        using (var writer = new WorkerFrameWriter(stream))
            await Task.WhenAll(Enumerable.Range(0, 64).Select(index => writer.WriteAsync(Encoding.UTF8.GetBytes($"{{\"index\":{index}}}"), CancellationToken.None))).ConfigureAwait(false);
        Assert.IsTrue(stream.CanRead);
        stream.Position = 0;
        var reader = new WorkerFrameReader(stream);
        var received = new HashSet<string>();
        byte[]? frame;
        while ((frame = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
            Assert.IsTrue(received.Add(Encoding.UTF8.GetString(frame)));
        Assert.AreEqual(64, received.Count);
        Assert.IsNull(await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InvalidLengthTruncationAndNoncanonicalBodiesPoisonTheChannel()
    {
        foreach (var input in new byte[][] { [0], [0, 0, 0, 0], [127, 255, 255, 255], [0, 0, 0, 3, 123, 125],
            [0, 0, 0, 3, 32, 123, 125] })
        {
            using var stream = new MemoryStream(input);
            var reader = new WorkerFrameReader(stream);
            await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => reader.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CanceledBeforeReadingDoesNotConsumeOrPoisonACompleteFrame()
    {
        var bytes = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 2);
        bytes[4] = (byte)'{'; bytes[5] = (byte)'}';
        using var stream = new MemoryStream(bytes);
        var reader = new WorkerFrameReader(stream);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(0L, stream.Position);
        CollectionAssert.AreEqual("{}"u8.ToArray(), await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false));
    }
}
