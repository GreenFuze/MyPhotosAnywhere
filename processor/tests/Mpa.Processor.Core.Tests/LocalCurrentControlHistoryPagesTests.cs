using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class LocalCurrentControlHistoryPagesTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(1000)]
    [DataRow(1001)]
    [DataRow(4096)]
    public void CurrentHistoryPagesRoundTripEveryIdentityThroughUnchangedCanonicalBounds(int count)
    {
        var ids=Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToArray();
        var history=LocalCurrentControlHistoryPages.FromHeads(ids);
        var pages=history.Pages;
        var codec=new CanonicalJson(262144);
        var canonical=codec.Encode(JsonSerializer.SerializeToUtf8Bytes(pages));
        var decoded=JsonSerializer.Deserialize<Guid[][]>(canonical)!;
        CollectionAssert.AreEqual(ids,new LocalCurrentControlHistoryPages(decoded).Heads);
        Assert.AreEqual((count+999)/1000,pages.Length);
        Assert.IsTrue(pages.All(page=>page.Length is >=1 and <=1000));
        Assert.IsTrue(pages.Take(pages.Length-1).All(page=>page.Length==1000));
        // The old one-array encoding actually fails at1001; paging must not change
        // CanonicalJson's shared cap to manufacture a passing large-history test.
        if(count>1000) Assert.ThrowsExactly<ProtocolFormatException>(()=>codec.Encode(JsonSerializer.SerializeToUtf8Bytes(ids)));
        var copy=history.Heads;copy[0]=Guid.Empty;pages[0][0]=Guid.Empty;ids[0]=Guid.Empty;
        Assert.AreNotEqual(Guid.Empty,history.Heads[0]);
    }

    [TestMethod]
    public void CurrentHistoryPagesRejectMalformedSplitsAndDuplicatesAcrossPageBoundaries()
    {
        var first=Enumerable.Range(0,1000).Select(_=>Guid.NewGuid()).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages(null!));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages([]));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages([null!]));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages([Array.Empty<Guid>()]));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages([[Guid.Empty]]));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages([[Guid.NewGuid()],[Guid.NewGuid()]]));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages([first,[first[0]]]));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages([first.Append(Guid.NewGuid()).ToArray()]));
        var excessive=Enumerable.Range(0,4097).Select(_=>Guid.NewGuid()).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(()=>LocalCurrentControlHistoryPages.FromHeads(excessive));
        Assert.ThrowsExactly<InvalidDataException>(()=>new LocalCurrentControlHistoryPages(excessive.Chunk(1000).ToArray()));
        Assert.ThrowsExactly<InvalidDataException>(()=>LocalCurrentControlHistoryPages.FromHeads([]));
        Assert.ThrowsExactly<InvalidDataException>(()=>LocalCurrentControlHistoryPages.FromHeads([first[0],first[0]]));
    }
}
