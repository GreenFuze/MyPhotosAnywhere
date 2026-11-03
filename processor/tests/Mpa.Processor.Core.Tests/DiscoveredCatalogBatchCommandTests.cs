using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Core.Tests;

public sealed partial class DiscoveredCatalogTests
{
    [TestMethod]
    public void ReusedBatchStatementsResetNullableMetadataVersionsAndMembershipsAcrossItemsAndBatches()
    {
        using var fixture=new Fixture();
        DiscoveredDriveAsset[] first=[
            Fact("full",100) with {DriveVersion=2,RawCapture="2026:10:25 10:11:12",ModifiedUtc=90,Depth=2},
            Fact("empty",null) with {DriveVersion=2,Width=null,Height=null},
            Fact("modified",null) with {DriveVersion=2,MimeType="video/mp4",ModifiedUtc=0},
            Fact("negative",-100) with {DriveVersion=2,Width=null,RawCapture="unknown"}];
        Assert.AreEqual(4,fixture.Catalog.ApplyBatch(fixture.Source,1,1,first));
        foreach(var item in first) Assert.AreEqual(item,fixture.Catalog.ReadPage().Assets.Single(asset=>asset.Facts.FileId==item.FileId).Facts);
        DiscoveredDriveAsset[] second=[
            first[0] with {DriveVersion=3,CapturedUtc=null,Width=null,Height=null,RawCapture=null,ModifiedUtc=null,Depth=0},
            first[1] with {DriveVersion=3,CapturedUtc=200,Width=900,Height=600,RawCapture="new",ModifiedUtc=100,Depth=3},
            first[2],
            Fact("brand-new",0) with {ModifiedUtc=-10,Width=null}];
        Assert.AreEqual(3,fixture.Catalog.ApplyBatch(fixture.Source,1,1,second));
        var page=fixture.Catalog.ReadPage();Assert.AreEqual(5,page.Assets.Count);
        foreach(var item in second) Assert.AreEqual(item,page.Assets.Single(asset=>asset.Facts.FileId==item.FileId).Facts);
        Assert.AreEqual(0,fixture.Catalog.ApplyBatch(fixture.Source,1,1,second));Assert.AreEqual(page.DiscoveryRevision,fixture.Catalog.ReadPage().DiscoveryRevision);
        var overlapping=fixture.AddSource("account-A","overlap");
        var older=second[1] with {DriveVersion=1,Name="Old.jpg",CapturedUtc=-1,Depth=1};
        Assert.AreEqual(1,fixture.Catalog.ApplyBatch(overlapping,1,1,[older]));
        var preserved=fixture.Catalog.ReadPage().Assets.Single(asset=>asset.Facts.FileId==older.FileId);
        Assert.AreEqual(second[1] with {Depth=1},preserved.Facts);
        var other=fixture.AddSource("account-B","other");Assert.AreEqual(1,fixture.Catalog.ApplyBatch(other,1,1,[first[0] with {Depth=0}]));
        Assert.AreEqual(1,fixture.Catalog.ReadPage(accountId:"account-B").Assets.Count);Assert.AreEqual(5,fixture.Catalog.ReadPage(accountId:"account-A").Assets.Count);
    }

    [TestMethod]
    public void ALastMembershipSqlFailureRollsBackAllReusedWritesAndLeavesTheSameBatchRetryable()
    {
        using var fixture=new Fixture();var original=Fact("retained",100);fixture.Catalog.ApplyBatch(fixture.Source,1,1,[original]);
        var before=fixture.Catalog.ReadPage();DiscoveredDriveAsset[] pending=[original with {DriveVersion=2,Name="Updated.jpg"},Fact("new-before-failure",200),Fact("fail-last",300)];
        using(var trigger=fixture.Database.Connection.CreateCommand())
        {
            trigger.CommandText="CREATE TRIGGER fail_last_membership BEFORE INSERT ON source_asset WHEN NEW.asset_id=(SELECT id FROM discovered_asset WHERE file_id='fail-last') BEGIN SELECT RAISE(ABORT,'synthetic late membership failure'); END";
            trigger.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<SqliteException>(()=>fixture.Catalog.ApplyBatch(fixture.Source,1,1,pending));
        var failed=fixture.Catalog.ReadPage();Assert.AreEqual(before.DiscoveryRevision,failed.DiscoveryRevision);
        Assert.AreEqual(1,failed.Assets.Count);Assert.AreEqual(before.Assets.Single(),failed.Assets.Single());
        using(var drop=fixture.Database.Connection.CreateCommand()){drop.CommandText="DROP TRIGGER fail_last_membership";drop.ExecuteNonQuery();}
        Assert.AreEqual(3,fixture.Catalog.ApplyBatch(fixture.Source,1,1,pending));var after=fixture.Catalog.ReadPage();
        Assert.AreEqual(3,after.Assets.Count);Assert.AreEqual(before.DiscoveryRevision+1,after.DiscoveryRevision);
        Assert.AreEqual(before.Assets.Single().Id,after.Assets.Single(asset=>asset.Facts.FileId=="retained").Id);
        foreach(var item in pending) Assert.AreEqual(item,after.Assets.Single(asset=>asset.Facts.FileId==item.FileId).Facts);
    }
}
