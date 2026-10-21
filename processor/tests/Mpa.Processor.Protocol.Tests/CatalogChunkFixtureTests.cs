using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CatalogChunkFixtureTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog-chunks.json")));
    private static CatalogChunkReference Reference(JsonElement item)
    {
        var value = item.GetProperty("expected"); var encoded = value.GetProperty("encoded");
        static string Text(JsonElement root, string name) => root.GetProperty(name).GetString()!;
        static long Count(JsonElement root, string name) => long.Parse(Text(root, name), CultureInfo.InvariantCulture);
        return new(new(Text(encoded, "accountId"), Text(encoded, "fileId"), Text(encoded, "workspaceId"), Count(encoded, "bytes"), Text(encoded, "sha256")),
            Count(value, "expandedBytes"), Text(value, "expandedSha256"));
    }
    [TestMethod]
    public async Task BothConsumersDecodeTheIndependentPythonGzipFixture()
    {
        using var fixture = Fixture(); var value = fixture.RootElement.GetProperty("valid");
        var encoded = Convert.FromBase64String(value.GetProperty("compressed").GetString()!); var before = encoded.ToArray();
        var decoded = await new CatalogChunkDecoder().DecodeAsync(encoded, Reference(value), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(value.GetProperty("canonical").GetString(), Encoding.UTF8.GetString(decoded));
        CollectionAssert.AreEqual(before, encoded);
    }
    [TestMethod]
    public async Task BothConsumersRejectEveryCorruptNoncanonicalOrOversizedSharedFixture()
    {
        using var fixture = Fixture(); var cases = fixture.RootElement.GetProperty("invalid"); Assert.AreEqual(16, cases.GetArrayLength());
        foreach (var value in cases.EnumerateArray())
        {
            var encoded = Convert.FromBase64String(value.GetProperty("compressed").GetString()!);
            await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => new CatalogChunkDecoder().DecodeAsync(encoded, Reference(value), CancellationToken.None), value.GetProperty("name").GetString()).ConfigureAwait(false);
        }
    }
    [TestMethod]
    public async Task CancellationAndConsumerSpecificBudgetsRemainBlocking()
    {
        using var fixture = Fixture(); var value = fixture.RootElement.GetProperty("valid"); var reference = Reference(value);
        var encoded = Convert.FromBase64String(value.GetProperty("compressed").GetString()!);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new CatalogChunkDecoder().DecodeAsync(encoded, reference, canceled.Token)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => new CatalogChunkDecoder(compressedLimit: encoded.Length - 1).DecodeAsync(encoded, reference, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(() => new CatalogChunkDecoder(expandedLimit: (int)reference.ExpandedBytes - 1).DecodeAsync(encoded, reference, CancellationToken.None)).ConfigureAwait(false);
    }
}
