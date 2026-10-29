using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Web.Tests;

[TestClass]
public sealed class DesktopLibraryViewerReaderTests
{
    [TestMethod]
    public async Task CanonicalViewerMetadataKeepsFrozenStageAccountPositionsAndMissingIdentities()
    {
        var selected=Selection();var photo=Photo();var missing=Guid.NewGuid();var calls=new List<Guid>();
        var query=new ExactQueries((stage,account,id)=>
        {
            Assert.AreEqual(selected.StageId,stage);Assert.AreEqual("account-A",account);calls.Add(id);return new(selected,id==photo.Id?photo:null);
        });
        var input=new CatalogViewerWindow(Guid.NewGuid(),10,4,[new(4,photo.Id),new(5,missing)],selected.StageId,"account-A");
        var result=await new DesktopLibraryViewerReader(query).ReadAsync(input,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(input.Id,result.Id);Assert.AreEqual(input.Count,result.Count);Assert.AreEqual(input.Position,result.Position);Assert.AreEqual(selected.StageId,result.StageId);Assert.AreEqual("account-A",result.AccountId);
        CollectionAssert.AreEqual(new[]{photo.Id,missing},calls.ToArray());Assert.AreEqual(photo.Id,result.Entries[0].Photo!.Id);Assert.AreEqual(photo.Name,result.Entries[0].Photo!.Name);
        Assert.AreEqual(5L,result.Entries[1].Position);Assert.AreEqual(missing,result.Entries[1].AssetId);Assert.IsNull(result.Entries[1].Photo);
    }

    [TestMethod]
    public async Task CanonicalViewerMetadataRejectsReplacementOrForeignPhotosInsteadOfResolvingDiscoveryNeighbors()
    {
        var selected=Selection();var photo=Photo();var other=Guid.NewGuid();var frame=new CatalogViewerWindow(Guid.NewGuid(),2,0,[new(0,photo.Id),new(1,other)],selected.StageId,"account-A");
        var replaced=new ExactQueries((_,_,id)=>new(id==photo.Id?selected:Selection(),id==photo.Id?photo:null));
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>new DesktopLibraryViewerReader(replaced).ReadAsync(frame,CancellationToken.None)).ConfigureAwait(false);
        var foreign=new ExactQueries((_,_,_)=>new(selected,photo with {AccountId="account-B"}));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>new DesktopLibraryViewerReader(foreign).ReadAsync(frame,CancellationToken.None)).ConfigureAwait(false);
        var neighbor=new ExactQueries((_,_,_)=>new(selected,Photo()));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>new DesktopLibraryViewerReader(neighbor).ReadAsync(frame,CancellationToken.None)).ConfigureAwait(false);
        var io=new ExactQueries((_,_,_)=>throw new IOException("Synthetic historical failure."));
        await Assert.ThrowsExactlyAsync<IOException>(()=>new DesktopLibraryViewerReader(io).ReadAsync(frame,CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CanonicalViewerMetadataValidatesFrozenWindowsAndCancellationBeforeAnyExactQuery()
    {
        var selected=Selection();var photo=Photo();var calls=0;var query=new ExactQueries((_,_,_)=>{calls++;return new(selected,photo);});var reader=new DesktopLibraryViewerReader(query);
        var valid=new CatalogViewerWindow(Guid.NewGuid(),3,1,[new(0,Guid.NewGuid()),new(1,photo.Id)],selected.StageId);
        foreach(var frame in new[]{valid with {StageId=null},valid with {StageId=Guid.Empty},valid with {Count=0},valid with {Entries=[]},valid with {Entries=[new(1,photo.Id),new(2,photo.Id)]},
            valid with {Entries=[new(0,Guid.NewGuid()),new(2,photo.Id)]},valid with {Entries=[new(0,Guid.Empty),new(1,photo.Id)]},valid with {Entries=Enumerable.Range(0,91).Select(i=>new CatalogSessionEntry(i,Guid.NewGuid())).ToArray(),Count=91}})
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>reader.ReadAsync(frame,CancellationToken.None)).ConfigureAwait(false);
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>reader.ReadAsync(valid,canceled.Token)).ConfigureAwait(false);Assert.AreEqual(0,calls);
    }

    private static CurrentCatalogActivation Selection()
    {
        var stage=Guid.NewGuid();return new(stage,"synthetic historical context",new string('a',64),7,Guid.NewGuid(),1,1,null,1,1,new string('b',64),1,new string('c',64),
            Guid.NewGuid(),1,new string('d',64),null,1,1,new(stage,4096,new string('e',64),10,1));
    }
    private static DriveCatalogAsset Photo()=>new(Guid.NewGuid(),"account-A","synthetic-file",1,"Literal שלום.jpg","image/jpeg",40,30,1,null,null,Array.AsReadOnly(new[]{new DriveCatalogSource(Guid.NewGuid(),1,0)}));
    private sealed class ExactQueries(Func<Guid,string?,Guid,CurrentCanonicalReadAsset> find) : ICurrentCanonicalCatalogQueries
    {
        public Task<CurrentCanonicalReadAsset?> FindAssetAsync(Guid stage,Guid id,CancellationToken caller)=>Task.FromResult<CurrentCanonicalReadAsset?>(find(stage,null,id));
        public Task<CurrentCanonicalReadAsset?> FindAccountAssetAsync(Guid stage,CurrentCatalogAccountFilter account,Guid id,CancellationToken caller)=>Task.FromResult<CurrentCanonicalReadAsset?>(find(stage,account.AccountId,id));
        public Task<CurrentCanonicalReadPage?> ReadPageAsync(Guid? stage,CatalogCursor? after,int limit,CancellationToken caller)=>throw new InvalidOperationException("A frozen viewer must not page another corpus.");
        public Task<CurrentCanonicalReadPage?> ReadPreviousPageAsync(Guid stage,CatalogCursor before,int limit,CancellationToken caller)=>throw new InvalidOperationException("Unexpected paging.");
        public Task<CurrentCanonicalReadPage?> ReadAtOrBeforeAsync(Guid? stage,long date,int limit,CancellationToken caller)=>throw new InvalidOperationException("Unexpected paging.");
        public Task<CurrentCanonicalReadPage?> ReadAccountPageAsync(Guid? stage,CurrentCatalogAccountFilter account,CatalogCursor? after,int limit,CancellationToken caller)=>throw new InvalidOperationException("Unexpected paging.");
        public Task<CurrentCanonicalReadPage?> ReadAccountPreviousPageAsync(Guid stage,CurrentCatalogAccountFilter account,CatalogCursor before,int limit,CancellationToken caller)=>throw new InvalidOperationException("Unexpected paging.");
        public Task<CurrentCanonicalReadPage?> ReadAccountAtOrBeforeAsync(Guid? stage,CurrentCatalogAccountFilter account,long date,int limit,CancellationToken caller)=>throw new InvalidOperationException("Unexpected paging.");
    }
}
