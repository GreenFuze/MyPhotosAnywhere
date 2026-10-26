using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CurrentCatalogStageTests
{
    [TestMethod]
    public void CurrentStageAreaAddsASecondDurableClaimWithoutDisturbingTheFirstSealedReader()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var first=area.Open(fixture.Claim,fixture.Budget);
        foreach(var leaf in fixture.Leaves([fixture.Asset("first",1)])) first.Add(leaf);
        var sealedFirst=fixture.Claim with {Phase="sealed",Descriptor=first.Seal(1)};var second=fixture.Claim with {Id=Guid.NewGuid()};
        Assert.ThrowsExactly<InvalidOperationException>(()=>area.Open(second,fixture.Budget));
        area.SyncClaims([sealedFirst,second]);using var next=area.Open(second,fixture.Budget);Assert.AreEqual(0L,next.Seal(0).Assets);
        Assert.AreEqual("first",first.ReadPage().Assets[0].FileId);
        Assert.ThrowsExactly<InvalidOperationException>(()=>area.SyncClaims([second]));
        Assert.AreEqual("first",first.ReadPage().Assets[0].FileId);
    }

    [TestMethod]
    public void CurrentStageAreaRetirementProofExpiresAndStalePreparingRecordsCannotReopenDeletedWork()
    {
        using var fixture=new Fixture();using var area=fixture.Area();
        using(var first=area.Open(fixture.Claim,fixture.Budget)) first.Seal(0);
        var second=fixture.Claim with {Id=Guid.NewGuid()};area.SyncClaims([fixture.Claim,second]);
        using(var active=area.Open(second,fixture.Budget))
        {
            var retiring=fixture.Claim with {Phase="retiring"};var receipt=area.RemoveRetiring(retiring);
            receipt.AssertFor(fixture.Root.Path,fixture.Database.InstallationId,retiring);
            Assert.ThrowsExactly<InvalidOperationException>(()=>area.Open(fixture.Claim,fixture.Budget));
            Assert.ThrowsExactly<InvalidOperationException>(()=>receipt.AssertFor(fixture.Root.Path,fixture.Database.InstallationId,retiring with {PreparedAttempt=Guid.NewGuid()}));
            area.SyncClaims([second]);Assert.AreEqual(0L,active.Seal(0).Assets);
            Assert.ThrowsExactly<InvalidOperationException>(()=>receipt.AssertFor(fixture.Root.Path,fixture.Database.InstallationId,retiring));
        }
        area.Dispose();Assert.ThrowsExactly<ObjectDisposedException>(()=>area.SyncClaims([second]));
    }

    [TestMethod]
    public void CurrentStageAreaClaimSynchronizationCannotRetargetForgetFilesOrAdoptUnknownEntries()
    {
        using var fixture=new Fixture();using var area=fixture.Area();
        Assert.ThrowsExactly<InvalidDataException>(()=>area.SyncClaims([fixture.Claim with {PreparedAttempt=Guid.NewGuid()}]));
        using(var first=area.Open(fixture.Claim,fixture.Budget)) first.Seal(0);
        Assert.ThrowsExactly<InvalidDataException>(()=>area.SyncClaims([]));
        var file=Path.Combine(fixture.Root.Path,"current-catalog-stages","original.jpg");File.WriteAllBytes(file,[42]);
        Assert.ThrowsExactly<InvalidDataException>(()=>area.SyncClaims([fixture.Claim,fixture.Claim with {Id=Guid.NewGuid()}]));
        Assert.AreEqual(42,File.ReadAllBytes(file)[0]);
    }
}
