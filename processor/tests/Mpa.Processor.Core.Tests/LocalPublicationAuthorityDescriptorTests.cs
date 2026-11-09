using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class LocalPublicationAuthorityDescriptorTests
{
    [TestMethod]
    [DataRow(1L,"e1f7afee4f80c2ee21dc416c16d189ccad50be37b5732d9c445fb0d98f30bf79")]
    [DataRow(9007199254740993L,"c6dc84eed0304fd3597630537061c06cc2b35772cc8844c7f9e759da4c6f8de2")]
    [DataRow(9223372036854775807L,"ea14a3d4e949162b0fbe1a00c809b3942a38060de871753ead3fc26296fa1341")]
    public void OriginalV1DigestPreservesFixedIndependentCanonicalVectors(long counter,string expected)
    {
        var descriptor=Descriptor(counter);
        Assert.AreEqual(expected,descriptor.Sha256());
    }

    [TestMethod]
    public void EveryOriginalAuthorityFieldRemainsBoundToTheDigest()
    {
        var descriptor=Descriptor(1);var hash=descriptor.Sha256();
        foreach(var changed in new[]{descriptor with{LibraryId=Guid.Empty},descriptor with{ControllerId=Guid.Empty},
            descriptor with{ControllerEpoch=2},descriptor with{ControllerKeyId=new string('e',64)},
            descriptor with{PublisherId=Guid.Empty},descriptor with{PublisherEpoch=2},descriptor with{PublisherKeyId=new string('e',64)},
            descriptor with{PublisherSequence=2},descriptor with{PublisherPolicySha256=new string('e',64)},
            descriptor with{SourceSequence=2},descriptor with{SourcePolicySha256=new string('e',64)},
            descriptor with{AccountId="other"},descriptor with{FolderId="other"},descriptor with{ConfigurationRevision=2}})
            Assert.AreNotEqual(hash,changed.Sha256());
    }

    private static LocalPublicationAuthorityDescriptor Descriptor(long counter)=>new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),Guid.Parse("22222222-2222-4222-8222-222222222222"),counter,new string('a',64),
        Guid.Parse("33333333-3333-4333-8333-333333333333"),counter,new string('b',64),counter,new string('c',64),
        counter,new string('d',64),"permission-A","metadata",counter);
}
