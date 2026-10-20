using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class ArtifactIntegrityTests
{
    private static ArtifactDescriptor Descriptor(byte[] bytes, string path = "chunks/test.json") =>
        new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), path);

    [TestMethod]
    public async Task VerificationRejectsTruncationMutationAndExtraBytesWithoutTakingTheLease()
    {
        var original = new byte[] { 1, 2, 3 };
        var expected = Descriptor(original);
        using var good = new MemoryStream(original);
        await new ArtifactVerifier().VerifyAsync(good, expected, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(good.CanRead);
        foreach (var invalid in new byte[][] { [1, 2], [1, 2, 4], [1, 2, 3, 4] })
        {
            using var input = new MemoryStream(invalid);
            await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => new ArtifactVerifier().VerifyAsync(input, expected, CancellationToken.None)).ConfigureAwait(false);
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        good.Position = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ArtifactVerifier().VerifyAsync(good, expected, canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(0L, good.Position);
    }

    [TestMethod]
    public async Task CompressedChunksRequireBothHashesCanonicalBytesAndExpandedBound()
    {
        static byte[] Compress(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
            return output.ToArray();
        }
        var original = "{\"entries\":[{\"name\":\"שלום\"}]}"u8.ToArray();
        var compressed = Compress(original);
        var decoder = new CatalogChunkDecoder();
        CollectionAssert.AreEqual(original, await decoder.DecodeAsync(compressed, Descriptor(compressed), Descriptor(original), CancellationToken.None).ConfigureAwait(false));
        var declaredShort = new ArtifactDescriptor(original.Length - 1, Descriptor(original).Sha256, "short.json");
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => decoder.DecodeAsync(compressed, Descriptor(compressed), declaredShort, CancellationToken.None)).ConfigureAwait(false);
        var bomb = Compress(new byte[CatalogChunkDecoder.MaximumExpandedBytes + 1]);
        var bounded = new ArtifactDescriptor(CatalogChunkDecoder.MaximumExpandedBytes, Descriptor(original).Sha256, "bomb.json");
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => decoder.DecodeAsync(bomb, Descriptor(bomb), bounded, CancellationToken.None)).ConfigureAwait(false);
        var noncanonical = "{ \"z\":1,\"a\":2 }"u8.ToArray();
        var encoded = Compress(noncanonical);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => decoder.DecodeAsync(encoded, Descriptor(encoded), Descriptor(noncanonical), CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public void WorkerPathsCannotBecomeUrlsAbsolutePathsOrPortableDeviceNames()
    {
        foreach (var path in new[] { "../original.jpg", "/tmp/photo", "C:/photo", "a\\b", "https://example.com", "a//b", "a/./b", "a/CON.jpg", "a/lpt1", "a/trailing.", "%2e%2e/escape" })
            Assert.ThrowsExactly<ProtocolFormatException>(() => Descriptor([1], path));
    }
}
