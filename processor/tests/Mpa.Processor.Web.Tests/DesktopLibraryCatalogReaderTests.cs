using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Web.Tests;

[TestClass]
public sealed class DesktopLibraryCatalogReaderTests
{
    private static readonly JsonSerializerOptions WebJsonOptions=new(JsonSerializerDefaults.Web);
    [TestMethod]
    public async Task LibraryCatalogPagesBindActualStageAndAccountAndKeepDiscoveryRevisionsOutOfTheContract()
    {
        var selection=Selection();var first=Asset("account-A",100);var last=Asset("account-A",50);var query=new Queries();
        EnqueuePage(query,selection,[first,last],[Asset("account-A",0)],[Asset("account-A",200)]);
        var reader=new DesktopLibraryCatalogReader(query);var page=await reader.ReadAsync("account-A",null,false,null,CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(page.Available);Assert.AreEqual(selection.StageId,page.StageId);Assert.AreEqual(selection.RootSequence,page.RootSequence);
        CollectionAssert.AreEqual(new[]{first.Id,last.Id},page.Photos.Select(p=>p.Id).ToArray());Assert.IsNotNull(page.OlderCursor);Assert.IsNotNull(page.NewerCursor);
        Assert.AreEqual(new Call("account-after",null,"account-A",null,60),query.Calls[0]);
        Assert.AreEqual(new Call("account-after",selection.StageId,"account-A",new(last.EffectiveUtc,last.Id),1),query.Calls[1]);
        Assert.AreEqual(new Call("account-before",selection.StageId,"account-A",new(first.EffectiveUtc,first.Id),1),query.Calls[2]);
        using(var json=JsonDocument.Parse(JsonSerializer.Serialize(page,WebJsonOptions)))
        {Assert.IsFalse(json.RootElement.TryGetProperty("discoveryRevision",out _));Assert.IsFalse(json.RootElement.TryGetProperty("configurationRevision",out _));}
        EnqueuePage(query,selection,[first],[],[]);
        _=await reader.ReadAsync("account-A",page.NewerCursor,true,null,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new Call("account-before",selection.StageId,"account-A",new(first.EffectiveUtc,first.Id),60),query.Calls[3]);
    }

    [TestMethod]
    public async Task LibraryCatalogRejectsOtherAccountLegacyNoncanonicalAndInvalidCursorsBeforeAnyQuery()
    {
        var query=new Queries();var reader=new DesktopLibraryCatalogReader(query);var stage=Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");var id=Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        foreach(var cursor in new[]{"1|*|unknown|"+id,"c1|"+stage+"|account-B|0|"+id,"c1|"+stage.ToString("D").ToUpperInvariant()+"|account-A|0|"+id,
            "c1|"+Guid.Empty+"|account-A|0|"+id,"c1|"+stage+"|account-A|+1|"+id,"c1|"+stage+"|account-A|01|"+id,"c1|"+stage+"|account-A|253402300800000|"+id,
            "c1|"+stage+"|account-A|unknown|"+Guid.Empty,new string('x',401)})
            await Assert.ThrowsExactlyAsync<InvalidDesktopCatalogQueryException>(()=>reader.ReadAsync("account-A",cursor,false,null,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDesktopCatalogQueryException>(()=>reader.ReadAsync("*",null,false,null,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDesktopCatalogQueryException>(()=>reader.ReadAsync(null,null,true,null,CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDesktopCatalogQueryException>(()=>reader.ReadAsync(null,null,false,long.MinValue,CancellationToken.None)).ConfigureAwait(false);
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>reader.ReadAsync(null,null,false,null,canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(0,query.Calls.Count);
    }

    [TestMethod]
    public async Task LibraryCatalogAChangedCursorStageCannotBecomeAPageFromTheReplacementIndex()
    {
        var original=Selection();var replacement=Selection();var photo=Asset("account-A",100);var query=new Queries();query.Pages.Enqueue(Page(replacement,[photo]));
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>new DesktopLibraryCatalogReader(query).ReadAsync(null,
            "c1|"+original.StageId+"|*|100|"+photo.Id,false,null,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1,query.Calls.Count);Assert.AreEqual(original.StageId,query.Calls[0].Stage);
    }

    [TestMethod]
    public async Task LibraryCatalogAStageChangeBetweenPageAndEdgeProbeRejectsTheEntireMixedResult()
    {
        var original=Selection();var photo=Asset("account-A",100);var query=new Queries();query.Pages.Enqueue(Page(original,[photo]));query.Pages.Enqueue(Page(Selection(),[]));
        await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>new DesktopLibraryCatalogReader(query).ReadAsync(null,null,false,null,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2,query.Calls.Count);Assert.AreEqual(original.StageId,query.Calls[1].Stage);
    }

    [TestMethod]
    public async Task LibraryCatalogNoSelectionIsExplicitButAnOperationalFailureNeverBecomesAnEmptyLibrary()
    {
        var query=new Queries();query.Pages.Enqueue(null);var page=await new DesktopLibraryCatalogReader(query).ReadAsync(null,null,false,null,CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(page.Available);Assert.IsNull(page.StageId);Assert.IsNull(page.RootSequence);Assert.AreEqual(0,page.Photos.Count);
        var failure=new Queries {Failure=new IOException("Synthetic retained-index failure.")};
        await Assert.ThrowsExactlyAsync<IOException>(()=>new DesktopLibraryCatalogReader(failure).ReadAsync(null,null,false,null,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1,failure.Calls.Count);
    }

    [TestMethod]
    public async Task LibraryCatalogDateAndPreviousQueriesUseTheCanonicalOwnerAndPreserveDateProvenance()
    {
        var selection=Selection();var photo=Asset("account-A",0);var query=new Queries();EnqueuePage(query,selection,[photo],[],[]);
        var reader=new DesktopLibraryCatalogReader(query);var page=await reader.ReadAsync(null,null,false,0,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new Call("date",null,null,null,60,Date:0),query.Calls[0]);Assert.AreEqual("DriveCapture",page.Photos.Single().DateProvenance);
        Assert.AreEqual(photo.RawCapture,page.Photos.Single().RawCapture);Assert.AreEqual(photo.Name,page.Photos.Single().Name);
        EnqueuePage(query,selection,[photo],[],[]);_=await reader.ReadAsync(null,"c1|"+selection.StageId+"|*|unknown|"+photo.Id,true,null,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new Call("before",selection.StageId,null,new(null,photo.Id),60),query.Calls[3]);
    }

    [TestMethod]
    public async Task LibraryCatalogExactResolutionPreservesMissingSlotsAndRejectsAnotherPhotoOrAccount()
    {
        var selection=Selection();var photo=Asset("account-A",0);var query=new Queries {Exact=new(selection,null)};var reader=new DesktopLibraryCatalogReader(query);
        Assert.IsNull((await reader.ResolveAsync(selection.StageId,photo.Id,"account-A",CancellationToken.None).ConfigureAwait(false)).Asset);
        Assert.AreEqual(new Call("account-id",selection.StageId,"account-A",null,0,Id:photo.Id),query.Calls[0]);
        query.Exact=new(selection,Asset("account-A",0));await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>reader.ResolveAsync(selection.StageId,photo.Id,null,CancellationToken.None)).ConfigureAwait(false);
        query.Exact=new(selection,photo);await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>reader.ResolveAsync(selection.StageId,photo.Id,"account-B",CancellationToken.None)).ConfigureAwait(false);
        query.Exact=new(Selection(),photo);await Assert.ThrowsExactlyAsync<CurrentCanonicalPositionChangedException>(()=>reader.ResolveAsync(selection.StageId,photo.Id,null,CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LibraryCatalogInvalidDuplicateOrForeignAccountPagesFailBeforeEdgeQueries()
    {
        var selection=Selection();var photo=Asset("account-A",0);
        foreach(var frame in new[]{Page(selection,[photo,photo]),Page(selection,[Asset("account-B",0)]),new CurrentCanonicalReadPage(selection,new(Array.AsReadOnly(Array.Empty<DriveCatalogAsset>()),true))})
        {
            var query=new Queries();query.Pages.Enqueue(frame);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>new DesktopLibraryCatalogReader(query).ReadAsync("account-A",null,false,null,CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1,query.Calls.Count);
        }
    }

    private sealed record Call(string Kind,Guid? Stage,string? Account,CatalogCursor? Key,int Limit,long? Date=null,Guid? Id=null);
    private sealed class Queries : ICurrentCanonicalCatalogQueries
    {
        internal Queue<CurrentCanonicalReadPage?> Pages {get;}=new();internal List<Call> Calls {get;}=[];
        internal Exception? Failure {get;init;}internal CurrentCanonicalReadAsset? Exact {get;set;}
        private Task<CurrentCanonicalReadPage?> Read(Call call,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls.Add(call);if(Failure is not null) throw Failure;return Task.FromResult(Pages.Dequeue());}
        private Task<CurrentCanonicalReadAsset?> Find(Call call,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls.Add(call);return Task.FromResult(Exact);}
        public Task<CurrentCanonicalReadPage?> ReadPageAsync(Guid? stage,CatalogCursor? key,int limit,CancellationToken token)=>Read(new("after",stage,null,key,limit),token);
        public Task<CurrentCanonicalReadPage?> ReadPreviousPageAsync(Guid stage,CatalogCursor key,int limit,CancellationToken token)=>Read(new("before",stage,null,key,limit),token);
        public Task<CurrentCanonicalReadPage?> ReadAtOrBeforeAsync(Guid? stage,long date,int limit,CancellationToken token)=>Read(new("date",stage,null,null,limit,date),token);
        public Task<CurrentCanonicalReadAsset?> FindAssetAsync(Guid stage,Guid id,CancellationToken token)=>Find(new("id",stage,null,null,0,Id:id),token);
        public Task<CurrentCanonicalReadPage?> ReadAccountPageAsync(Guid? stage,CurrentCatalogAccountFilter account,CatalogCursor? key,int limit,CancellationToken token)=>Read(new("account-after",stage,account.AccountId,key,limit),token);
        public Task<CurrentCanonicalReadPage?> ReadAccountPreviousPageAsync(Guid stage,CurrentCatalogAccountFilter account,CatalogCursor key,int limit,CancellationToken token)=>Read(new("account-before",stage,account.AccountId,key,limit),token);
        public Task<CurrentCanonicalReadPage?> ReadAccountAtOrBeforeAsync(Guid? stage,CurrentCatalogAccountFilter account,long date,int limit,CancellationToken token)=>Read(new("account-date",stage,account.AccountId,null,limit,date),token);
        public Task<CurrentCanonicalReadAsset?> FindAccountAssetAsync(Guid stage,CurrentCatalogAccountFilter account,Guid id,CancellationToken token)=>Find(new("account-id",stage,account.AccountId,null,0,Id:id),token);
    }
    private static void EnqueuePage(Queries query,CurrentCatalogActivation selection,DriveCatalogAsset[] main,DriveCatalogAsset[] older,DriveCatalogAsset[] newer)
    {query.Pages.Enqueue(Page(selection,main));query.Pages.Enqueue(Page(selection,older));query.Pages.Enqueue(Page(selection,newer));}
    private static CurrentCanonicalReadPage Page(CurrentCatalogActivation selection,DriveCatalogAsset[] photos)=>new(selection,new(Array.AsReadOnly(photos),false));
    private static CurrentCatalogActivation Selection()
    {
        var stage=Guid.NewGuid();return new(stage,"synthetic historical query context",new string('a',64),7,Guid.NewGuid(),1,1,null,1,1,new string('b',64),1,new string('c',64),
            Guid.NewGuid(),1,new string('d',64),new("account-A","catalog","metadata",10,new string('e',64)),1,1,new(stage,4096,new string('f',64),10,1));
    }
    private static DriveCatalogAsset Asset(string account,long? date)=>new(Guid.NewGuid(),account,"synthetic-file",1,"Literal <tag> שלום.jpg","image/jpeg",40,30,date,"2026:10:29 01:02:03",null,
        Array.AsReadOnly(new[]{new DriveCatalogSource(Guid.NewGuid(),1,0)}));
}
