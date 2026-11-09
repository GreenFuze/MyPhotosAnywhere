using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CurrentCatalogStageTests
{
    [TestMethod]
    public void CanonicalViewerCaptureStreamsActualSealedIdentitiesInAccountTimelineOrderWithBoundedWindows()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);
        var assets=Enumerable.Range(0,650).Select(i=>AccountAsset(fixture,i%2==0?"account-A":"account-B","viewer-"+i,i>620?null:i/4)).ToArray();
        foreach(var leaf in fixture.Leaves(assets)) source.Add(leaf);source.Seal(assets.Length);
        var sourceBytes=File.ReadAllBytes(fixture.File);var path=Path.Combine(fixture.Root.Path,"canonical-viewer.sqlite");
        foreach(var account in new string?[]{null,"account-A","account-B"})
        {
            var expected=NavigationOrder(assets.Where(a=>account is null || a.AccountId==account));var selected=expected[100];
            using(var viewer=source.CaptureViewerIndex(path,selected.Id,account is null?null:new(account)))
            {
                Assert.AreEqual((long)expected.Length,viewer.Count);Assert.AreEqual(100L,viewer.SelectedPosition);
                var ids=new List<Guid>();
                for(long i=0;i<viewer.Count;i+=90) {var window=viewer.ReadWindow(i,0,89);Assert.IsTrue(window.Count<=90);ids.AddRange(window.Select(e=>e.AssetId));}
                CollectionAssert.AreEqual(expected.Select(a=>a.Id).ToArray(),ids.ToArray());Assert.IsTrue(fixture.Budget.Status().ReservedBytes>0);
            }
            Assert.IsFalse(File.Exists(path));Assert.IsFalse(File.Exists(path+"-journal"));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        }
        CollectionAssert.AreEqual(sourceBytes,File.ReadAllBytes(fixture.File));
    }

    [TestMethod]
    public void CanonicalViewerCaptureMissingOrForeignSelectionsNeverSubstituteAnIdentityAndPreserveUnownedFiles()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);
        var asset=fixture.Asset("viewer-selected",42);foreach(var leaf in fixture.Leaves([asset])) source.Add(leaf);source.Seal(1);
        var path=Path.Combine(fixture.Root.Path,"canonical-viewer.sqlite");
        Assert.ThrowsExactly<CatalogSessionSelectionUnavailableException>(()=>source.CaptureViewerIndex(path,Guid.NewGuid()));
        Assert.ThrowsExactly<CatalogSessionSelectionUnavailableException>(()=>source.CaptureViewerIndex(path,asset.Id,new("account-B")));
        Assert.IsFalse(File.Exists(path));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        File.WriteAllBytes(path,[1,2,3]);Assert.ThrowsExactly<IOException>(()=>source.CaptureViewerIndex(path,asset.Id));CollectionAssert.AreEqual(new byte[]{1,2,3},File.ReadAllBytes(path));File.Delete(path);
        File.WriteAllBytes(path+"-journal",[4,5]);Assert.ThrowsExactly<IOException>(()=>source.CaptureViewerIndex(path,asset.Id));CollectionAssert.AreEqual(new byte[]{4,5},File.ReadAllBytes(path+"-journal"));
        Assert.IsFalse(File.Exists(path));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CanonicalViewerCaptureCancellationDuringActualStreamingAndLowSpaceRetainOnlyTheCanonicalSource()
    {
        using var fixture=new Fixture();var space=new ViewerCaptureSpace();var budget=new StorageBudgetManager(space);
        using var area=fixture.Area();using var source=area.Open(fixture.Claim,budget);
        var assets=Enumerable.Range(0,600).Select(i=>fixture.Asset("viewer-cancel-"+i,i)).ToArray();foreach(var leaf in fixture.Leaves(assets)) source.Add(leaf);source.Seal(assets.Length);
        var path=Path.Combine(fixture.Root.Path,"canonical-viewer.sqlite");var bytes=File.ReadAllBytes(fixture.File);using var canceled=new CancellationTokenSource();var reads=0;
        space.Observed=()=>{if(++reads==3) canceled.Cancel();};
        Assert.ThrowsExactly<OperationCanceledException>(()=>source.CaptureViewerIndex(path,assets[0].Id,null,canceled.Token));space.Observed=null;
        Assert.IsFalse(File.Exists(path));Assert.IsFalse(File.Exists(path+"-journal"));Assert.AreEqual(0L,budget.Status().ReservedBytes);
        space.Available=0;Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>source.CaptureViewerIndex(path,assets[0].Id));space.Available=20*StorageBudgetManager.Gibibyte;
        CollectionAssert.AreEqual(bytes,File.ReadAllBytes(fixture.File));Assert.AreEqual(0L,budget.Status().ReservedBytes);
        using var viewer=source.CaptureViewerIndex(path,assets[0].Id);Assert.AreEqual(600L,viewer.Count);
    }

    [TestMethod]
    public void CanonicalViewerCaptureRejectsCorruptCanonicalRowsBeforePublishingItsDisposableIndex()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var source=area.Open(fixture.Claim,fixture.Budget);
        var asset=fixture.Asset("viewer-corrupt",1);foreach(var leaf in fixture.Leaves([asset])) source.Add(leaf);
        fixture.Sql("UPDATE fact SET account_id='account-B'");source.Seal(1);var path=Path.Combine(fixture.Root.Path,"canonical-viewer.sqlite");var bytes=File.ReadAllBytes(fixture.File);
        Assert.ThrowsExactly<InvalidDataException>(()=>source.CaptureViewerIndex(path,asset.Id,new("account-B")));
        Assert.IsFalse(File.Exists(path));Assert.IsFalse(File.Exists(path+"-journal"));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
        CollectionAssert.AreEqual(bytes,File.ReadAllBytes(fixture.File));
    }

    [TestMethod]
    public void CanonicalViewerCaptureGenuineFormatOneKeepsAllAccountIdentitiesButRefusesInventedAccountEnumeration()
    {
        using var fixture=new Fixture();var asset=fixture.Asset("viewer-genuine-one",42);var descriptor=CreateGenuineFormatOne(fixture,asset);var bytes=File.ReadAllBytes(fixture.File);
        var claim=fixture.Claim with {Phase="sealed",Descriptor=descriptor};using var area=fixture.Area(claim);using var source=area.Open(claim,fixture.Budget);
        var path=Path.Combine(fixture.Root.Path,"canonical-viewer.sqlite");
        using(var viewer=source.CaptureViewerIndex(path,asset.Id)) {Assert.AreEqual(1L,viewer.Count);Assert.AreEqual(asset.Id,viewer.ReadWindow(0).Single().AssetId);}
        Assert.ThrowsExactly<CurrentCatalogAccountPagingUnavailableException>(()=>source.CaptureViewerIndex(path,asset.Id,new("account-A")));
        Assert.IsFalse(File.Exists(path));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);CollectionAssert.AreEqual(bytes,File.ReadAllBytes(fixture.File));
    }

    private sealed class ViewerCaptureSpace : IStorageSpaceReader
    {
        internal long Available {get;set;}=20*StorageBudgetManager.Gibibyte;
        internal Action? Observed {get;set;}
        public StorageSpaceSnapshot Read() {Observed?.Invoke();return new(100*StorageBudgetManager.Gibibyte,Available,0);}
    }
}
