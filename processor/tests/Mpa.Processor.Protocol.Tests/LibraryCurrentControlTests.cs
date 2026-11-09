using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class LibraryCurrentControlTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","library-current-control.json")));
    private static byte[] Bytes(JsonElement value,string name) => Encoding.UTF8.GetBytes(value.GetProperty(name).GetString()!);
    private static LibraryConnectionDescriptor Connection(JsonElement value) => LibraryConnectionDescriptor.Decode(value.GetProperty("code").GetString()!);
    private static LibraryCurrentControl First(JsonElement value) => LibraryCurrentControl.Read(Bytes(value.GetProperty("valid")[0],"envelope"),Connection(value));

    [TestMethod]
    public void PublicV2ConnectionPinsOneActualControlIdentityAndPreservesExactWire()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);
        Assert.AreEqual("current-control",connection.CurrentFileId);Assert.AreEqual(root.GetProperty("code").GetString(),connection.Encode());
        Assert.AreEqual(root.GetProperty("connectionSha256").GetString(),connection.DocumentSha256);
        var legacy=LibraryConnectionDescriptor.Decode(root.GetProperty("legacyCode").GetString()!);
        Assert.IsNull(legacy.CurrentFileId);Assert.AreNotEqual(legacy.DocumentSha256,connection.DocumentSha256);
        var invalid=root.GetProperty("invalidCodes");Assert.AreEqual(8,invalid.GetArrayLength());
        foreach(var row in invalid.EnumerateArray()) Assert.ThrowsExactly<ProtocolFormatException>(()=>LibraryConnectionDescriptor.Decode(row.GetProperty("code").GetString()!),row.GetProperty("name").GetString());
    }

    [TestMethod]
    public void BothConsumersReadIndependentSignedSnapshotsAndFullCounterRange()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);var first=First(root);
        var valid=root.GetProperty("valid");Assert.AreEqual(5,valid.GetArrayLength());
        foreach(var row in valid.EnumerateArray())
        {
            var current=LibraryCurrentControl.Read(Bytes(row,"envelope"),connection);current.AssertSuccessorOf(first);
            Assert.AreEqual(connection.DocumentSha256,current.ConnectionSha256);Assert.AreEqual("publishers",current.Publishers.Reference.FileId);
        }
        Assert.AreEqual(long.MaxValue,LibraryCurrentControl.Read(Bytes(valid[4],"envelope"),connection).Sequence);
        Assert.AreEqual(11L,first.Sequence);Assert.AreEqual(9L,first.Publishers.Sequence);Assert.AreEqual(10L,first.Sources.Sequence);
    }

    [TestMethod]
    public void BothConsumersRejectSignedButMalformedAliasedRetargetedAndOverbroadSnapshots()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);var invalid=root.GetProperty("invalid");
        Assert.AreEqual(39,invalid.GetArrayLength());
        foreach(var row in invalid.EnumerateArray()) Assert.ThrowsExactly<ProtocolFormatException>(()=>LibraryCurrentControl.Read(Bytes(row,"envelope"),connection),row.GetProperty("name").GetString());
    }

    [TestMethod]
    public void DurableHeadComparisonRejectsRewindForkAndOlderUnpublishedPolicySelection()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);var first=First(root);
        var invalid=root.GetProperty("invalidTransitions");Assert.AreEqual(4,invalid.GetArrayLength());
        foreach(var row in invalid.EnumerateArray())
        {
            var candidate=LibraryCurrentControl.Read(Bytes(row,"envelope"),connection);
            Assert.ThrowsExactly<ProtocolFormatException>(()=>candidate.AssertSuccessorOf(first),row.GetProperty("name").GetString());
        }
        var retarget=LibraryCurrentControl.Read(Bytes(root,"retargetHead"),LibraryConnectionDescriptor.Decode(root.GetProperty("retargetCode").GetString()!));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>retarget.AssertSuccessorOf(first));
    }

    [TestMethod]
    public void LegacyPinsAndWrongControlCopiesCannotClaimFreshAuthority()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var bytes=Bytes(root.GetProperty("valid")[0],"envelope");
        Assert.ThrowsExactly<ProtocolFormatException>(()=>LibraryCurrentControl.Read(bytes,LibraryConnectionDescriptor.Decode(root.GetProperty("legacyCode").GetString()!)));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>LibraryCurrentControl.Read(bytes,LibraryConnectionDescriptor.Decode(root.GetProperty("retargetCode").GetString()!)));
        bytes[^2]^=1;Assert.ThrowsExactly<ProtocolFormatException>(()=>LibraryCurrentControl.Read(bytes,Connection(root)));
    }

    [TestMethod]
    public void SelectedPoliciesRequireExactBytesSequenceAndIndependentRootSignature()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);var current=First(root);
        var publisher=current.ReadPublishers(Bytes(root,"publisher"),connection);var sources=current.ReadSources(Bytes(root,"sources"),connection);
        Assert.AreEqual(9L,publisher.Sequence);Assert.IsNotNull(publisher.Publisher);Assert.AreEqual(7L,sources.Revision);Assert.AreEqual(3,sources.Sources.Count);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadSources(Bytes(root,"publisher"),connection));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadPublishers(Bytes(root,"sources"),connection));
        var bytes=Bytes(root,"publisher");bytes[^2]^=1;Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadPublishers(bytes,connection));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadSources(Bytes(root,"sources"),LibraryConnectionDescriptor.Decode(root.GetProperty("retargetCode").GetString()!)));
        var wrong=LibraryCurrentControl.Read(Bytes(root,"wrongPolicySequence"),connection);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>wrong.ReadPublishers(Bytes(root,"publisher"),connection));
    }

    [TestMethod]
    public void ExplicitRevocationCannotRetainACatalogFromTheOldPublisher()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);
        var revoked=LibraryCurrentControl.Read(Bytes(root,"revocation"),connection);revoked.AssertSuccessorOf(First(root));
        Assert.IsNull(revoked.Catalog);Assert.IsNull(revoked.ReadPublishers(Bytes(root,"revokedPublisher"),connection).Publisher);
        var invalid=LibraryCurrentControl.Read(Bytes(root,"invalidRevocation"),connection);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>invalid.ReadPublishers(Bytes(root,"revokedPublisher"),connection));
    }

    [TestMethod]
    public void ExactReferencedBytesCannotSubstituteAnotherRootSigningKey()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);
        var current=LibraryCurrentControl.Read(Bytes(root,"forgedPoliciesHead"),connection);
        Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadPublishers(Bytes(root,"forgedPublisher"),connection));
        Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadSources(Bytes(root,"forgedSources"),connection));
    }

    [TestMethod]
    public void AcceptedSnapshotOwnsItsIdentityAndRejectsOutOfBoundsBorrowedEnvelopes()
    {
        using var fixture=Fixture();var root=fixture.RootElement;var connection=Connection(root);
        var bytes=Bytes(root.GetProperty("valid")[0],"envelope");var current=LibraryCurrentControl.Read(bytes,connection);Array.Fill(bytes,(byte)0);
        Assert.AreEqual(First(root).EnvelopeSha256,current.EnvelopeSha256);current.AssertSuccessorOf(First(root));
        foreach(var length in new[]{0,ProtocolDocumentReader.MaximumDocumentBytes+1})
        {
            var invalid=new byte[length];
            Assert.ThrowsExactly<ProtocolFormatException>(()=>LibraryCurrentControl.Read(invalid,connection));
            Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadPublishers(invalid,connection));
            Assert.ThrowsExactly<ProtocolFormatException>(()=>current.ReadSources(invalid,connection));
        }
    }
}
