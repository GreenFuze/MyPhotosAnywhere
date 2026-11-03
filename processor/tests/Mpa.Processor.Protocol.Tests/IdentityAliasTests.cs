using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Protocol.Tests;

[TestClass]
public sealed class IdentityAliasTests
{
    [TestMethod]
    public void UploadDiscoveryRaceAndChainsResolveIdenticallyInEitherOrder()
    {
        var provisional = Guid.NewGuid(); var preferred = Guid.NewGuid(); var final = Guid.NewGuid();
        foreach (var reverse in new[] { false, true })
        {
            var index = new IdentityAliasIndex();
            var links = new[] { new IdentityAliasLink(provisional, preferred), new IdentityAliasLink(preferred, final) };
            foreach (var link in reverse ? links.Reverse() : links) index.ApplyValidatedBatch([link]);
            Assert.AreEqual(final, index.Resolve(provisional));
            Assert.AreEqual(final, index.Resolve(preferred));
            Assert.AreEqual(final, index.Resolve(final));
            Assert.IsFalse(index.ApplyValidatedBatch(links));
            var unrelated = Guid.NewGuid();
            Assert.AreEqual(unrelated, index.Resolve(unrelated));
        }
    }

    [TestMethod]
    public void ConflictsCyclesAndBudgetFailuresRetainEveryAcceptedIdentity()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var index = new IdentityAliasIndex(2);
        index.ApplyValidatedBatch([new(a, b)]);
        Assert.ThrowsExactly<ProtocolFormatException>(() => index.ApplyValidatedBatch([new(a, c)]));
        Assert.ThrowsExactly<ProtocolFormatException>(() => index.ApplyValidatedBatch([new(b, a)]));
        Assert.AreEqual(b, index.Resolve(a));
        Assert.AreEqual(c, index.Resolve(c));
        index.ApplyValidatedBatch([new(b, c)]);
        Assert.ThrowsExactly<ProtocolFormatException>(() => index.ApplyValidatedBatch([new(Guid.NewGuid(), c)]));
        Assert.AreEqual(c, index.Resolve(a));
    }
}
