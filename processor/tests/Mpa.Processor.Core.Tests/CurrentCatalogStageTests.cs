using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class CurrentCatalogStageTests
{
    [TestMethod]
    public void CurrentStageSealsKnownDatesBeforeUnknownAndSeeksWithoutDuplicates()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var assets=Enumerable.Range(0,5).Select(i=>fixture.Asset("file-"+i,i==4 ? null : i%2==0 ? 1000L : -1L)).ToArray();
        foreach(var leaf in fixture.Leaves(assets)) index.Add(leaf);
        var descriptor=index.Seal(5);Assert.AreEqual(5L,descriptor.Assets);Assert.IsTrue(descriptor.Bytes>=4096);
        Assert.AreEqual(descriptor,index.Seal(5));
        var first=index.ReadPage(limit:2);var second=index.ReadPage(new(first.Assets[^1].EffectiveUtc,first.Assets[^1].Id),2);
        var third=index.ReadPage(new(second.Assets[^1].EffectiveUtc,second.Assets[^1].Id),2);
        Assert.IsTrue(first.HasMore);Assert.IsTrue(second.HasMore);Assert.IsFalse(third.HasMore);
        var expected=assets.OrderBy(a=>a.EffectiveUtc is null).ThenByDescending(a=>a.EffectiveUtc).ThenBy(a=>a.Id.ToString("D"),StringComparer.Ordinal).Select(a=>a.Id).ToArray();
        CollectionAssert.AreEqual(expected,first.Assets.Concat(second.Assets).Concat(third.Assets).Select(a=>a.Id).ToArray());
        Assert.IsNull(third.Assets[^1].EffectiveUtc);
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadPage(limit:91));
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadPage(new(null,Guid.Empty)));
    }

    [TestMethod]
    public void CurrentStageReopensOnlyItsExactClaimReplaysEveryRowAndRetainsSealedBytes()
    {
        using var fixture=new Fixture();var leaves=fixture.Leaves([fixture.Asset("retained",42)]);CurrentCatalogStageDescriptor descriptor;
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget)) {foreach(var leaf in leaves) index.Add(leaf);descriptor=index.Seal(1);}
        var sealedClaim=fixture.Claim with {Phase="sealed",Descriptor=descriptor};var bytes=File.ReadAllBytes(fixture.File);
        using(var area=fixture.Area(sealedClaim))
        {
            Assert.ThrowsExactly<InvalidOperationException>(()=>area.Open(sealedClaim with {PreparedAttempt=Guid.NewGuid()},fixture.Budget));
            using var index=area.Open(sealedClaim,fixture.Budget);Assert.AreEqual(1,index.ReadPage().Assets.Count);
            Assert.ThrowsExactly<InvalidDataException>(()=>index.Seal(1)); // Recovery still needs a complete fresh graph enumeration.
            foreach(var leaf in leaves) index.Add(leaf);Assert.AreEqual(descriptor,index.Seal(1));
            Assert.ThrowsExactly<InvalidDataException>(()=>index.Add(leaves[0]));
            Assert.ThrowsExactly<InvalidOperationException>(()=>area.Dispose());
        }
        CollectionAssert.AreEqual(bytes,File.ReadAllBytes(fixture.File));
    }

    [TestMethod]
    public void CurrentStagePartialRecoveryCannotSealAnUnvisitedRetainedPartitionOrChangeItsDates()
    {
        using var fixture=new Fixture();var leaf=fixture.Leaves([fixture.Asset("retained",10)])[0];
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget)) index.Add(leaf);
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
        {Assert.ThrowsExactly<InvalidDataException>(()=>index.Seal(0));index.Add(leaf);Assert.AreEqual(1L,index.Seal(1).Assets);}
        // Simulate damaged private work with no sealed descriptor yet. Exact replay
        // must verify sort columns as well as payload SHA, before accepting a seal.
        fixture.Sql("UPDATE fact SET date_sort=0");
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
            Assert.ThrowsExactly<InvalidDataException>(()=>index.Add(leaf));
    }

    [TestMethod]
    public void CurrentStageLeafTransactionRollsBackAllRowsAndRetriesTheSameReference()
    {
        using var fixture=new Fixture();var leaf=fixture.Leaves([fixture.Asset("atomic",1)])[0];
        using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        fixture.Sql("CREATE TRIGGER reject_fact BEFORE INSERT ON fact BEGIN SELECT RAISE(ABORT,'injected storage failure'); END");
        Assert.ThrowsExactly<SqliteException>(()=>index.Add(leaf));Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM fact"));Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM leaf"));
        fixture.Sql("DROP TRIGGER reject_fact");index.Add(leaf);Assert.AreEqual(1L,index.Seal(1).Assets);Assert.AreEqual(leaf.Facts.Assets[0].Id,index.ReadPage().Assets[0].Id);
    }

    [TestMethod]
    public void CurrentStageRejectsForeignReferencesAndChangedRetainedBytesWithoutMutation()
    {
        using var fixture=new Fixture();var leaf=fixture.Leaves([fixture.Asset("exact",1)])[0];
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
        {
            Assert.ThrowsExactly<InvalidDataException>(()=>index.Add(leaf with {Reference=leaf.Reference with {ExpandedSha256=new string('b',64)}}));
            Assert.ThrowsExactly<InvalidDataException>(()=>index.Add(leaf with {Reference=leaf.Reference with {Encoded=leaf.Reference.Encoded with {AccountId="foreign"}}}));
            Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM leaf"));index.Add(leaf);
        }
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
        {
            var changed=fixture.Leaves([leaf.Facts.Assets[0] with {Name="Changed"}])[0];
            Assert.ThrowsExactly<InvalidDataException>(()=>index.Add(changed));index.Add(leaf);Assert.AreEqual(1L,index.Seal(1).Assets);
        }
    }

    [TestMethod]
    public void CurrentStageLowStorageAndCancellationKeepTheOriginalClaimAndPartialFileForRetry()
    {
        using var fixture=new Fixture();var leaf=fixture.Leaves([fixture.Asset("low-space",1)])[0];
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
        {
            fixture.Space.Available=1;Assert.ThrowsExactly<InsufficientStorageBudgetException>(()=>index.Add(leaf));
            Assert.AreEqual(0L,fixture.Scalar("SELECT count(*) FROM leaf"));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
            fixture.Space.Available=20*StorageBudgetManager.Gibibyte;using var canceled=new CancellationTokenSource();canceled.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(()=>index.Add(leaf,canceled.Token));index.Add(leaf);
        }
        Assert.IsTrue(File.Exists(fixture.File));using var resumed=fixture.Area();using var recovered=resumed.Open(fixture.Claim,fixture.Budget);recovered.Add(leaf);Assert.AreEqual(1L,recovered.Seal(1).Assets);
    }

    [TestMethod]
    public void CurrentStageMissingOrCorruptSealedFileCannotBeRecreatedOrDeleted()
    {
        using var fixture=new Fixture();CurrentCatalogStageDescriptor descriptor;
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget)) descriptor=index.Seal(0);
        var claim=fixture.Claim with {Phase="sealed",Descriptor=descriptor};var bytes=File.ReadAllBytes(fixture.File);var changed=bytes.ToArray();changed[^1]^=1;File.WriteAllBytes(fixture.File,changed);
        using(var area=fixture.Area(claim))
        {
            Assert.ThrowsExactly<InvalidDataException>(()=>area.Open(claim,fixture.Budget));
            Assert.ThrowsExactly<InvalidDataException>(()=>area.RemoveRetiring(claim with {Phase="retiring"}));
        }
        CollectionAssert.AreEqual(changed,File.ReadAllBytes(fixture.File));File.Delete(fixture.File);
        Assert.ThrowsExactly<InvalidDataException>(()=>fixture.Area(claim));Assert.IsFalse(File.Exists(fixture.File));
    }

    [TestMethod]
    public void CurrentStageAreaRejectsUnknownFilesAndUnjoinedOrRetargetedRetirement()
    {
        using var fixture=new Fixture();using(var area=fixture.Area())
        {
            using(var index=area.Open(fixture.Claim,fixture.Budget))
            {Assert.ThrowsExactly<InvalidOperationException>(()=>area.RemoveRetiring(fixture.Claim with {Phase="retiring"}));Assert.ThrowsExactly<InvalidOperationException>(()=>area.Open(fixture.Claim,fixture.Budget));}
            Assert.ThrowsExactly<InvalidOperationException>(()=>area.RemoveRetiring(fixture.Claim));
            Assert.ThrowsExactly<InvalidOperationException>(()=>area.RemoveRetiring(fixture.Claim with {Phase="retiring",PreparedAttempt=Guid.NewGuid()}));
            area.RemoveRetiring(fixture.Claim with {Phase="retiring"});Assert.IsFalse(File.Exists(fixture.File));
        }
        var foreign=Path.Combine(fixture.Root.Path,"current-catalog-stages","original.jpg");File.WriteAllBytes(foreign,[1,2,3]);
        Assert.ThrowsExactly<InvalidDataException>(()=>fixture.Area());Assert.ThrowsExactly<InvalidDataException>(()=>CurrentCatalogStageArea.MeasureOwnedBytes(fixture.Root.Path));
        CollectionAssert.AreEqual(new byte[]{1,2,3},File.ReadAllBytes(foreign));
    }

    private sealed class Fixture : IDisposable
    {
        internal WorkerProcessTests.WorkerTestDirectory Root {get;}=new();
        internal ProcessorStateDatabase Database {get;}
        internal Space Space {get;}=new();
        internal StorageBudgetManager Budget {get;}
        internal CurrentCatalogStageClaim Claim {get;}
        private readonly Guid _library=Guid.NewGuid();
        internal string File=>Path.Combine(Root.Path,"current-catalog-stages","stage-"+Claim.Id.ToString("N")+".sqlite");
        internal Fixture()
        {
            Database=new(Root.Path);Budget=new(Space);using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pin=new LibraryConnectionDescriptor(_library,Guid.NewGuid(),1,new("account-A","metadata"),key.ExportSubjectPublicKeyInfo(),"current");
            Claim=new(Guid.NewGuid(),Guid.NewGuid(),pin.Encode(),new string('a',64),long.MaxValue,long.MaxValue,null,"preparing",null);
        }
        internal CurrentCatalogStageArea Area(CurrentCatalogStageClaim? claim=null)=>new(Root.Path,Database.InstallationId,[claim ?? Claim],Budget);
        internal DriveCatalogAsset Asset(string file,long? date)=>new(new DriveAssetIdentity(_library).Derive("account-A",file),"account-A",file,long.MaxValue,"Literal שלום.jpg","image/jpeg",null,null,date,null,null,Array.AsReadOnly(new[]{new DriveCatalogSource(Guid.Parse("00000000-0000-4000-8000-000000000001"),long.MaxValue,1)}));
        internal CatalogFactsLeaf[] Leaves(IEnumerable<DriveCatalogAsset> assets)=>assets.GroupBy(asset=>asset.Id.ToString("N")[..2]).OrderBy(group=>group.Key,StringComparer.Ordinal).Select(group=>
        {
            var codec=new DriveCatalogFacts(_library);var bytes=codec.Encode(group.Key,group);var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new CatalogFactsLeaf(group.Key,new(new("account-A","chunk-"+group.Key,"metadata",100,new string('c',64)),bytes.Length,hash),codec.Read(bytes,group.Key));
        }).ToArray();
        internal void Sql(string sql) {using var connection=Connection();using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        internal long Scalar(string sql) {using var connection=Connection();using var command=connection.CreateCommand();command.CommandText=sql;return (long)command.ExecuteScalar()!;}
        private SqliteConnection Connection() {var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=File,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());connection.Open();return connection;}
        public void Dispose() {Database.Dispose();Root.Dispose();}
    }
    private sealed class Space : IStorageSpaceReader
    {
        internal long Available {get;set;}=20*StorageBudgetManager.Gibibyte;
        public StorageSpaceSnapshot Read()=>new(100*StorageBudgetManager.Gibibyte,Available,0);
    }
}
