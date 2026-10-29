using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

public sealed partial class CurrentCatalogStageTests
{
    [TestMethod]
    public void CurrentAccountIndexPagesBothDirectionsAcrossEqualAndUnknownDatesWithoutOtherAccounts()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var assets=Enumerable.Range(0,18).Select(i=>AccountAsset(fixture,i%2==0?"account-A":"account-B","account-page-"+i,i>=14?null:i/4)).ToArray();
        foreach(var leaf in fixture.Leaves(assets)) index.Add(leaf);index.Seal(assets.Length);
        Assert.IsTrue(index.SupportsAccountPaging);Assert.AreEqual(2L,fixture.Scalar("PRAGMA user_version"));
        foreach(var name in new[]{"account-A","account-B"})
        {
            var account=new CurrentCatalogAccountFilter(name);var expected=NavigationOrder(assets.Where(a=>a.AccountId==name));
            var forward=new List<DriveCatalogAsset>();CatalogCursor? after=null;
            while(true)
            {
                var page=index.ReadAccountPage(account,after,2);Assert.IsTrue(page.Assets.Count<=2);forward.AddRange(page.Assets);
                if(!page.HasMore) break;Assert.IsTrue(page.Assets.Count>0);after=NavigationCursor(page.Assets[^1]);
            }
            CollectionAssert.AreEqual(expected.Select(a=>a.Id).ToArray(),forward.Select(a=>a.Id).ToArray());
            var backward=new List<DriveCatalogAsset>{expected[^1]};var before=NavigationCursor(expected[^1]);
            while(true)
            {
                var page=index.ReadAccountPreviousPage(account,before,2);backward.InsertRange(0,page.Assets);
                if(!page.HasMore) break;Assert.IsTrue(page.Assets.Count>0);before=NavigationCursor(page.Assets[0]);
            }
            CollectionAssert.AreEqual(expected.Select(a=>a.Id).ToArray(),backward.Select(a=>a.Id).ToArray());
        }
        Assert.AreEqual(0,index.ReadAccountPage(new("absent-account")).Assets.Count);
        Assert.AreEqual(18,index.ReadPage(limit:90).Assets.Count);
    }

    [TestMethod]
    public void CurrentAccountIndexDateSeeksAndPrimaryKeyResolutionPreserveTheExactAccountAndIdentity()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var assets=new[]{AccountAsset(fixture,"account-A","date-A",100),AccountAsset(fixture,"account-B","date-B",100),AccountAsset(fixture,"account-A","date-null",null),AccountAsset(fixture,"account-A","date-old",-1)};
        foreach(var leaf in fixture.Leaves(assets)) index.Add(leaf);index.Seal(assets.Length);var account=new CurrentCatalogAccountFilter("account-A");
        foreach(var date in new[]{100L,99L,-1L,-62135596800000L})
            CollectionAssert.AreEqual(NavigationOrder(assets.Where(a=>a.AccountId==account.AccountId && (a.EffectiveUtc is null || a.EffectiveUtc<=date))).Select(a=>a.Id).ToArray(),
                index.ReadAccountAtOrBefore(account,date,90).Assets.Select(a=>a.Id).ToArray());
        Assert.AreEqual(assets[0].Id,index.FindAccountAsset(account,assets[0].Id)!.Id);
        Assert.IsNull(index.FindAccountAsset(account,assets[1].Id));Assert.IsNull(index.FindAccountAsset(account,Guid.NewGuid()));
        Assert.AreEqual(assets[1].Id,index.FindAsset(assets[1].Id)!.Id);
    }

    [TestMethod]
    public void CurrentAccountIndexActualNativePlanSeeksTheCompoundIndexAndNeverFallsBackWhenItIsMissing()
    {
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);
        var assets=Enumerable.Range(0,50).Select(i=>AccountAsset(fixture,i==49?"account-B":"account-A","native-plan-"+i,i)).ToArray();
        foreach(var leaf in fixture.Leaves(assets)) index.Add(leaf);
        // The same command builder used by the real paging method supplies the
        // native EXPLAIN evidence. No handwritten substitute query is measured.
        index.Seal(assets.Length);var account=new CurrentCatalogAccountFilter("account-B");
        foreach(var after in new CatalogCursor?[]{null,NavigationCursor(assets[^1])})
        {
            var plan=index.ExplainAccountPage(account,after);StringAssert.Contains(plan,"SEARCH fact");StringAssert.Contains(plan,"current_stage_account_date");
            Assert.IsFalse(plan.Contains("SCAN ",StringComparison.OrdinalIgnoreCase));Assert.IsFalse(plan.Contains("TEMP B-TREE",StringComparison.OrdinalIgnoreCase));
        }
        // Exercise missing-index rejection on another unsealed owned fixture,
        // before its immutable read lease protects the generated file.
        using var missing=new Fixture();using var secondArea=missing.Area();using var second=secondArea.Open(missing.Claim,missing.Budget);
        foreach(var leaf in missing.Leaves([missing.Asset("missing-index",1)])) second.Add(leaf);
        missing.Sql("DROP INDEX current_stage_account_date");second.Seal(1);
        Assert.ThrowsExactly<SqliteException>(()=>second.ReadAccountPage(new("account-A")));
        Assert.AreEqual(1,second.ReadPage().Assets.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CurrentAccountIndexRejectsAnAccountColumnThatDisagreesWithItsCanonicalPayload(bool replay)
    {
        using var fixture=new Fixture();var leaf=fixture.Leaves([fixture.Asset("corrupt-account",1)])[0];
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
        {
            index.Add(leaf);fixture.Sql("UPDATE fact SET account_id='account-B'");
            if(!replay)
            {
                index.Seal(1);Assert.ThrowsExactly<InvalidDataException>(()=>index.ReadAccountPage(new("account-B")));
                Assert.ThrowsExactly<InvalidDataException>(()=>index.ReadPage());Assert.ThrowsExactly<InvalidDataException>(()=>index.FindAsset(leaf.Facts.Assets[0].Id));
            }
        }
        if(replay)
        {
            var bytes=File.ReadAllBytes(fixture.File);
            using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget)) Assert.ThrowsExactly<InvalidDataException>(()=>index.Add(leaf));
            CollectionAssert.AreEqual(bytes,File.ReadAllBytes(fixture.File));
        }
    }

    [TestMethod]
    public void CurrentAccountIndexGenuineSealedFormatOneRemainsReadableAndByteIdenticalWithoutInventingAccountPaging()
    {
        using var fixture=new Fixture();var asset=fixture.Asset("genuine-old-sealed",42);var descriptor=CreateGenuineFormatOne(fixture,asset);
        var claim=fixture.Claim with {Phase="sealed",Descriptor=descriptor};var bytes=File.ReadAllBytes(fixture.File);
        using(var area=fixture.Area(claim)) using(var index=area.Open(claim,fixture.Budget))
        {
            Assert.IsFalse(index.SupportsAccountPaging);Assert.AreEqual(asset.Id,index.ReadPage().Assets.Single().Id);
            Assert.AreEqual(asset.Id,index.FindAccountAsset(new("account-A"),asset.Id)!.Id);Assert.IsNull(index.FindAccountAsset(new("account-B"),asset.Id));
            Assert.ThrowsExactly<CurrentCatalogAccountPagingUnavailableException>(()=>index.ReadAccountPage(new("account-A")));
            Assert.ThrowsExactly<CurrentCatalogAccountPagingUnavailableException>(()=>index.ReadAccountPreviousPage(new("account-A"),NavigationCursor(asset)));
            Assert.ThrowsExactly<CurrentCatalogAccountPagingUnavailableException>(()=>index.ReadAccountAtOrBefore(new("account-A"),42));
            foreach(var leaf in fixture.Leaves([asset])) index.Add(leaf);Assert.AreEqual(descriptor,index.Seal(1));
        }
        CollectionAssert.AreEqual(bytes,File.ReadAllBytes(fixture.File));Assert.AreEqual(1L,fixture.Scalar("PRAGMA user_version"));
        using(var area=fixture.Area(claim with {Phase="retiring"})) area.RemoveRetiring(claim with {Phase="retiring"});
        Assert.IsFalse(File.Exists(fixture.File));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public void CurrentAccountIndexGenuinePartialFormatOneResumesItsSameLayoutRatherThanMigratingImmutableWork()
    {
        using var fixture=new Fixture();var first=fixture.Asset("genuine-old-partial",42);_=CreateGenuineFormatOne(fixture,first);
        var next=AccountAsset(fixture,"account-B","genuine-old-next",0);
        for(var i=0;next.Id.ToString("N")[..2]==first.Id.ToString("N")[..2];i++)
        {if(i==256) throw new InvalidOperationException("A distinct legacy fixture leaf was not found.");next=AccountAsset(fixture,"account-B","genuine-old-next-"+i,0);}
        using(var area=fixture.Area()) using(var index=area.Open(fixture.Claim,fixture.Budget))
        {
            Assert.IsFalse(index.SupportsAccountPaging);foreach(var leaf in fixture.Leaves([first])) index.Add(leaf);
            foreach(var leaf in fixture.Leaves([next])) index.Add(leaf);index.Seal(2);
            Assert.AreEqual(2,index.ReadPage().Assets.Count);Assert.AreEqual(next.Id,index.FindAccountAsset(new("account-B"),next.Id)!.Id);
        }
        Assert.AreEqual(1L,fixture.Scalar("PRAGMA user_version"));Assert.AreEqual(0L,fixture.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    public void CurrentAccountIndexUnknownPhysicalFormatsArePreservedAndNeverRecreatedOrRetired(int version)
    {
        using var fixture=new Fixture();_=CreateGenuineFormatOne(fixture,fixture.Asset("unknown-format",1));fixture.Sql("PRAGMA user_version="+version);
        var bytes=File.ReadAllBytes(fixture.File);
        using(var area=fixture.Area())
        {
            Assert.ThrowsExactly<InvalidDataException>(()=>area.Open(fixture.Claim,fixture.Budget));
            Assert.ThrowsExactly<InvalidDataException>(()=>area.RemoveRetiring(fixture.Claim with {Phase="retiring"}));
        }
        CollectionAssert.AreEqual(bytes,File.ReadAllBytes(fixture.File));
    }

    [TestMethod]
    public void CurrentAccountIndexKeepsAccountBoundsSealAdmissionAndCancellationAheadOfNativeReads()
    {
        foreach(var bad in new[]{"","*","a@example.com","../account",new string('a',257)}) Assert.ThrowsExactly<ArgumentException>(()=>new CurrentCatalogAccountFilter(bad));
        Assert.AreEqual(new string('a',256),new CurrentCatalogAccountFilter(new string('a',256)).AccountId);
        using var fixture=new Fixture();using var area=fixture.Area();using var index=area.Open(fixture.Claim,fixture.Budget);var account=new CurrentCatalogAccountFilter("account-A");
        Assert.ThrowsExactly<InvalidOperationException>(()=>index.ReadAccountPage(account));var asset=fixture.Asset("bounded-account",0);
        foreach(var leaf in fixture.Leaves([asset])) index.Add(leaf);index.Seal(1);
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadAccountPage(account,limit:91));Assert.ThrowsExactly<ArgumentException>(()=>index.ReadAccountPage(account,limit:0));
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadAccountPreviousPage(account,new(null,Guid.Empty)));
        Assert.ThrowsExactly<ArgumentException>(()=>index.ReadAccountAtOrBefore(account,long.MinValue));Assert.ThrowsExactly<ArgumentException>(()=>index.FindAccountAsset(account,Guid.Empty));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(()=>index.ReadAccountPage(account,token:canceled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(()=>index.ReadAccountPreviousPage(account,NavigationCursor(asset),token:canceled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(()=>index.ReadAccountAtOrBefore(account,0,token:canceled.Token));
        Assert.ThrowsExactly<OperationCanceledException>(()=>index.FindAccountAsset(account,asset.Id,canceled.Token));
    }

    private static DriveCatalogAsset AccountAsset(Fixture fixture,string account,string file,long? date)=>fixture.Asset(file,date) with
    {AccountId=account,Id=new DriveAssetIdentity(LibraryConnectionDescriptor.Decode(fixture.Claim.ConnectionCode).LibraryId).Derive(account,file)};

    // Genuine original schema1 input, independent of the format2 production
    // writer. Never create format2 and relabel/downgrade it as a legacy fixture.
    private static CurrentCatalogStageDescriptor CreateGenuineFormatOne(Fixture fixture,DriveCatalogAsset asset)
    {
        using(var area=fixture.Area()) { }
        var leaf=fixture.Leaves([asset]).Single();var codec=new DriveCatalogFacts(LibraryConnectionDescriptor.Decode(fixture.Claim.ConnectionCode).LibraryId);
        var identity=CurrentCatalogStageJournal.Identity(fixture.Claim);var payload=codec.Encode(leaf.Prefix,[asset]);
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=fixture.File,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString()))
        {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="""
                PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;PRAGMA page_size=4096;
                PRAGMA application_id=1297105219;PRAGMA user_version=1;
                CREATE TABLE identity(payload BLOB NOT NULL,sha256 TEXT NOT NULL) STRICT;
                CREATE TABLE leaf(prefix TEXT PRIMARY KEY,sha256 TEXT NOT NULL,assets INTEGER NOT NULL CHECK(assets>0)) STRICT;
                CREATE TABLE fact(id TEXT PRIMARY KEY,prefix TEXT NOT NULL REFERENCES leaf(prefix),payload BLOB NOT NULL,sha256 TEXT NOT NULL,
                    date_unknown INTEGER NOT NULL CHECK(date_unknown IN(0,1)),date_sort INTEGER NOT NULL) STRICT;
                CREATE INDEX current_stage_date ON fact(date_unknown,date_sort,id);
                INSERT INTO identity VALUES($identity,$identityHash);
                INSERT INTO leaf VALUES($prefix,$leafHash,1);
                INSERT INTO fact VALUES($id,$prefix,$payload,$payloadHash,$unknown,$sort);
                """;
            command.Parameters.AddWithValue("$identity",identity);command.Parameters.AddWithValue("$identityHash",AccountHash(identity));
            command.Parameters.AddWithValue("$prefix",leaf.Prefix);command.Parameters.AddWithValue("$leafHash",AccountHash(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(leaf.Reference))));
            command.Parameters.AddWithValue("$id",asset.Id.ToString("D"));command.Parameters.AddWithValue("$payload",payload);command.Parameters.AddWithValue("$payloadHash",AccountHash(payload));
            command.Parameters.AddWithValue("$unknown",asset.EffectiveUtc is null);command.Parameters.AddWithValue("$sort",asset.EffectiveUtc is null?0L:-asset.EffectiveUtc.Value);command.ExecuteNonQuery();
        }
        var bytes=File.ReadAllBytes(fixture.File);return new(fixture.Claim.Id,bytes.LongLength,AccountHash(bytes),1,1);
    }
    private static string AccountHash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
