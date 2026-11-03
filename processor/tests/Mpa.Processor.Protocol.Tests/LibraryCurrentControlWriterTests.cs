using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class LibraryCurrentControlWriterTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","library-current-control.json")));
    private static LibraryConnectionDescriptor Connection(JsonElement root) => LibraryConnectionDescriptor.Decode(root.GetProperty("code").GetString()!);

    [TestMethod]
    public void OutgoingCanonicalDocumentsExactlyMatchIndependentPythonVectors()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);var writer=new LibraryCurrentControlWriter(connection);
        foreach(var row in root.GetProperty("valid").EnumerateArray())
        {
            var bytes=Encoding.UTF8.GetBytes(row.GetProperty("envelope").GetString()!);var control=LibraryCurrentControl.Read(bytes,connection);
            using var envelope=JsonDocument.Parse(bytes);var expected=Encoding.UTF8.GetBytes(envelope.RootElement.GetProperty("document").GetRawText());
            var actual=writer.EncodeDocument(control.Sequence,control.Publishers,control.Sources,control.Catalog);CollectionAssert.AreEqual(expected,actual);
            writer.ValidateUnsignedPayload(Encoding.UTF8.GetBytes(envelope.RootElement.GetProperty("document").GetProperty("payload").GetRawText()),control.Sequence);
            Assert.ThrowsExactly<ProtocolFormatException>(()=>LibraryCurrentControl.Read(actual,connection));
        }
    }

    [TestMethod]
    public void SerializerUsesTheSameStrictPayloadChecksAsTheSignedReader()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var writer=new LibraryCurrentControlWriter(Connection(root));
        var invalid=root.GetProperty("invalid");var payloadCases=0;
        foreach(var row in invalid.EnumerateArray())
        {
            var name=row.GetProperty("name").GetString()!;
            if(name is "wrong-kind" or "missing-capability" or "foreign-library" or "foreign-controller" or "foreign-epoch") continue;
            using var document=JsonDocument.Parse(row.GetProperty("envelope").GetString()!);
            var payload=Encoding.UTF8.GetBytes(document.RootElement.GetProperty("document").GetProperty("payload").GetRawText());
            Assert.ThrowsExactly<ProtocolFormatException>(()=>writer.ValidateUnsignedPayload(payload,11),name);payloadCases++;
        }
        Assert.AreEqual(34,payloadCases);
    }

    [TestMethod]
    public void LegacyPinsAndAliasedReferencesCannotProduceControlDocuments()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);
        var control=LibraryCurrentControl.Read(Encoding.UTF8.GetBytes(root.GetProperty("valid")[0].GetProperty("envelope").GetString()!),connection);
        var legacy=new LibraryCurrentControlWriter(LibraryConnectionDescriptor.Decode(root.GetProperty("legacyCode").GetString()!));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>legacy.EncodeDocument(11,control.Publishers,control.Sources,control.Catalog));
        var writer=new LibraryCurrentControlWriter(connection);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>writer.EncodeDocument(10,control.Publishers,control.Sources,control.Catalog));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>writer.EncodeDocument(11,control.Publishers,control.Publishers,control.Catalog));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>writer.EncodeDocument(11,control.Publishers,control.Sources,control.Publishers.Reference));
    }
}
