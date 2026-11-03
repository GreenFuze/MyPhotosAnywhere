using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CurrentCatalogStageTests
{
    [TestMethod]
    public void CurrentStageNavigationTraversesBothDirectionsAcrossEqualAndUnknownDatesWithoutDuplicates()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var assets=new long?[]{200,100,100,100,0,-1,null,null,null}.Select((time,i)=>fixture.Asset("navigation-"+i,time)).ToArray();
        foreach(var leaf in fixture.Leaves(assets)) index.Add(leaf);index.Seal(assets.Length);
        var expected=NavigationOrder(assets);var forward=new List<DriveCatalogAsset>();CatalogCursor? after=null;
        while(true)
        {
            var page=index.ReadPage(after,2);forward.AddRange(page.Assets);
            if(!page.HasMore) break;Assert.IsTrue(page.Assets.Count>0);after=NavigationCursor(page.Assets[^1]);
        }
        CollectionAssert.AreEqual(expected.Select(v=>v.Id).ToArray(),forward.Select(v=>v.Id).ToArray());
        var backward=new List<DriveCatalogAsset>{expected[^1]};var before=NavigationCursor(expected[^1]);
        while(true)
        {
            var page=index.ReadPreviousPage(before,2);backward.InsertRange(0,page.Assets);
            if(!page.HasMore) break;Assert.IsTrue(page.Assets.Count>0);before=NavigationCursor(page.Assets[0]);
        }
        CollectionAssert.AreEqual(expected.Select(v=>v.Id).ToArray(),backward.Select(v=>v.Id).ToArray());
        Assert.AreEqual(0,index.ReadPreviousPage(NavigationCursor(expected[0])).Assets.Count);
    }

    [TestMethod]
    public void CurrentStageNavigationSeeksInclusiveUtcBoundariesWithUnknownDatesLast()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var assets=new long?[]{253402300799999L,100,100,0,-62135596800000L,null}.Select((time,i)=>fixture.Asset("dates-"+i,time)).ToArray();
        foreach(var leaf in fixture.Leaves(assets)) index.Add(leaf);index.Seal(assets.Length);
        foreach(var instant in new[]{253402300799999L,100L,99L,0L,-62135596800000L})
        {
            var expected=NavigationOrder(assets.Where(v=>v.EffectiveUtc is null || v.EffectiveUtc<=instant));
            var page=index.ReadAtOrBefore(instant,90);Assert.IsFalse(page.HasMore);
            CollectionAssert.AreEqual(expected.Select(v=>v.Id).ToArray(),page.Assets.Select(v=>v.Id).ToArray());
        }
        var shortPage=index.ReadAtOrBefore(100,1);Assert.IsTrue(shortPage.HasMore);Assert.AreEqual(100L,shortPage.Assets.Single().EffectiveUtc);
    }

    [TestMethod]
    public void CurrentStageNavigationResolvesOnlyExactIdentityAndLeavesTheSealedFileUnchanged()
    {
        using var fixture=new Fixture();var known=fixture.Asset("exact-navigation",42);var unknown=fixture.Asset("unknown-navigation",null);
        CurrentCatalogStageDescriptor descriptor;
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
        {
            foreach(var leaf in fixture.Leaves([known,unknown])) index.Add(leaf);descriptor=index.Seal(2);
            Assert.AreEqual(known.Id,index.FindAsset(known.Id)!.Id);Assert.AreEqual(unknown.Id,index.FindAsset(unknown.Id)!.Id);
            Assert.IsNull(index.FindAsset(fixture.Asset("missing-navigation",42).Id));
        }
        var bytes=SHA256.HashData(File.ReadAllBytes(fixture.File));var claim=fixture.Claim with{Phase="sealed",Descriptor=descriptor};
        using(var area=fixture.Area(claim)) using(var reopened=area.Open(claim,fixture.Budget))
        {
            Assert.AreEqual(known.Id,reopened.FindAsset(known.Id)!.Id);Assert.AreEqual(unknown.Id,reopened.FindAsset(unknown.Id)!.Id);
            Assert.AreEqual(known.Name,reopened.FindAsset(known.Id)!.Name);Assert.IsNull(reopened.FindAsset(unknown.Id)!.EffectiveUtc);
            Assert.AreEqual(1,reopened.ReadPreviousPage(NavigationCursor(unknown)).Assets.Count);
            Assert.AreEqual(2,reopened.ReadAtOrBefore(42).Assets.Count);
        }
        CollectionAssert.AreEqual(bytes,SHA256.HashData(File.ReadAllBytes(fixture.File)));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CurrentStageNavigationKeepsAMissingIdentityAsAnExplicitCursorBoundaryWithoutRetargetingIt()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var assets=new[]{fixture.Asset("newer",100),fixture.Asset("older",0)};
        foreach(var leaf in fixture.Leaves(assets)) index.Add(leaf);index.Seal(2);
        var absent=fixture.Asset("gone",50);Assert.IsNull(index.FindAsset(absent.Id));
        Assert.AreEqual(assets[0].Id,index.ReadPreviousPage(NavigationCursor(absent)).Assets.Single().Id);
        Assert.AreEqual(assets[1].Id,index.ReadPage(NavigationCursor(absent)).Assets.Single().Id);
    }

    [TestMethod]
    public void CurrentStageNavigationPreservesSealLimitsCancellationAndPerRowIntegrityChecks()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var asset=fixture.Asset("validated-navigation",42);var cursor=NavigationCursor(asset);
        Assert.ThrowsExactly<InvalidOperationException>(()=>index.ReadPreviousPage(cursor));
        Assert.ThrowsExactly<InvalidOperationException>(()=>index.ReadAtOrBefore(42));
        Assert.ThrowsExactly<InvalidOperationException>(()=>index.FindAsset(asset.Id));
        foreach(var leaf in fixture.Leaves([asset])) index.Add(leaf);index.Seal(1);
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadPreviousPage(new(null,Guid.Empty)));
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadPreviousPage(cursor,91));
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadAtOrBefore(long.MinValue));
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadAtOrBefore(long.MaxValue));
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadAtOrBefore(42,0));
        Assert.ThrowsExactly<ArgumentException>(()=>index.FindAsset(Guid.Empty));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>index.ReadPreviousPage(cursor,token:canceled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(()=>index.ReadAtOrBefore(42,token:canceled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(()=>index.FindAsset(asset.Id,canceled.Token));
    }

    [TestMethod]
    public void CurrentStageNavigationRejectsInvalidRetainedRowsInEveryQueryDirection()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var asset=fixture.Asset("invalid-retained-navigation",42);
        foreach(var leaf in fixture.Leaves([asset])) index.Add(leaf);
        // The test changes private generated work solely to exercise all actual
        // query paths' existing payload/hash/sort guards. Do so before sealing,
        // while the physical immutable read lease does not yet protect this file.
        fixture.Sql("UPDATE fact SET date_sort=0");index.Seal(1);
        Assert.ThrowsExactly<InvalidDataException>(()=>index.FindAsset(asset.Id));
        Assert.ThrowsExactly<InvalidDataException>(()=>index.ReadAtOrBefore(42));
        Assert.ThrowsExactly<InvalidDataException>(()=>index.ReadPreviousPage(new(null,Guid.NewGuid())));
    }

    private static CatalogCursor NavigationCursor(DriveCatalogAsset asset)=>new(asset.EffectiveUtc,asset.Id);
    private static DriveCatalogAsset[] NavigationOrder(IEnumerable<DriveCatalogAsset> assets)=>assets.OrderBy(v=>v.EffectiveUtc is null)
        .ThenByDescending(v=>v.EffectiveUtc).ThenBy(v=>v.Id.ToString("D"),StringComparer.Ordinal).ToArray();
}
