using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class CanonicalJsonTests
{
    private readonly CanonicalJson _codec = new();

    [TestMethod]
    public void SharedGoldenCasesHaveExactCanonicalBytes()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codec.json")));
        foreach (var test in fixture.RootElement.GetProperty("valid").EnumerateArray())
        {
            var input = Encoding.UTF8.GetBytes(test.GetProperty("input").GetString()!);
            var expected = Encoding.UTF8.GetBytes(test.GetProperty("canonical").GetString()!);
            CollectionAssert.AreEqual(expected, _codec.Encode(input), test.GetProperty("name").GetString());
            CollectionAssert.AreEqual(expected, _codec.Encode(expected), "Canonicalization must be idempotent.");
        }
        foreach (var test in fixture.RootElement.GetProperty("invalid").EnumerateArray())
            Assert.ThrowsExactly<ProtocolFormatException>(() => _codec.Encode(Encoding.UTF8.GetBytes(test.GetProperty("input").GetString()!)));
    }

    [TestMethod]
    public void ParserRejectsMalformedUtf8AndOversizedStructures()
    {
        Assert.ThrowsExactly<ProtocolFormatException>(() => _codec.Encode([0x22, 0xc3, 0x28, 0x22]));
        Assert.ThrowsExactly<ProtocolFormatException>(() => new CanonicalJson(4).Encode("[0,0]"u8));
        Assert.ThrowsExactly<ProtocolFormatException>(() => _codec.Encode(Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("0", 1001)) + "]")));
        Assert.ThrowsExactly<ProtocolFormatException>(() => _codec.Encode(Encoding.UTF8.GetBytes(new string('[', 33) + "0" + new string(']', 33))));
        Assert.ThrowsExactly<ProtocolFormatException>(() => _codec.Encode(Encoding.UTF8.GetBytes("{\"" + new string('a', 129) + "\":0}")));
        Assert.ThrowsExactly<ProtocolFormatException>(() => _codec.Encode(Encoding.UTF8.GetBytes("\"" + new string('a', 65537) + "\"")));
        var objectMembers = string.Join(',', Enumerable.Range(0, 257).Select(index => "\"k" + index + "\":0"));
        Assert.ThrowsExactly<ProtocolFormatException>(() => _codec.Encode(Encoding.UTF8.GetBytes("{" + objectMembers + "}")));
    }

    [TestMethod]
    public void ReorderedUnicodePropertiesUseUtf16OrdinalOrder()
    {
        var expected = "{\"😀\":1,\"\ue000\":2}";
        Assert.AreEqual(expected, Encoding.UTF8.GetString(_codec.Encode(Encoding.UTF8.GetBytes("{\"\ue000\":2,\"😀\":1}"))));
    }
}
