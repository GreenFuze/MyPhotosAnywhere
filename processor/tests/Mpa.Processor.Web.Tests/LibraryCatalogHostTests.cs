using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.AppServices;
using Mpa.Processor.Core;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task LibraryCatalogHostRequiresAuthenticationAndPreservesTheLongStageAccountCursorContract()
    {
        using var assets=new Assets();var account=new string('a',256);var stage=Guid.NewGuid();var id=Guid.NewGuid();
        var cursor="c1|"+stage+"|"+account+"|253402300799999|"+id;Assert.IsTrue(cursor.Length>340 && cursor.Length<=400);
        var engine=new Engine {LibraryCatalogResult=new(true,stage,7,[new(id,account,"shared.jpg","image/jpeg",40,30,1,"DriveCapture",null)],null,null)};
        await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);
        using(var anonymous=await http.GetAsync("api/library/catalog").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        Assert.AreEqual(0,engine.LibraryCatalogCalls);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        using var response=await http.GetAsync("api/library/catalog?account="+account+"&cursor="+Uri.EscapeDataString(cursor)+"&direction=newer").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        Assert.IsTrue(json.GetProperty("available").GetBoolean());Assert.AreEqual(stage,json.GetProperty("stageId").GetGuid());Assert.AreEqual(7L,json.GetProperty("rootSequence").GetInt64());
        Assert.IsFalse(json.TryGetProperty("discoveryRevision",out _));Assert.IsFalse(json.TryGetProperty("configurationRevision",out _));
        Assert.AreEqual((account,cursor,true,(long?)null),engine.LibraryCatalogArguments);Assert.AreEqual(1,engine.LibraryCatalogCalls);
    }

    [TestMethod]
    public async Task LibraryCatalogHostPositionLegacyIndexAndUninitializedStatesPreserveTheAuthenticatedSession()
    {
        using var assets=new Assets();var engine=new Engine();var diagnostics=new List<Exception>();
        await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);host.Diagnostic+=diagnostics.Add;
        using var http=Client(host.LocalEndpoint);using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        foreach(var failure in new (Exception Failure,HttpStatusCode Status)[]{(new CurrentCanonicalPositionChangedException(),HttpStatusCode.Conflict),
            (new CurrentCatalogAccountPagingUnavailableException(),HttpStatusCode.Conflict),(new DesktopLibraryCatalogUnavailableException(),HttpStatusCode.ServiceUnavailable)})
        {
            engine.LibraryCatalogFailure=failure.Failure;
            using(var response=await http.GetAsync("api/library/catalog").ConfigureAwait(false))
            {Assert.AreEqual(failure.Status,response.StatusCode);var json=await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);Assert.IsTrue(json.GetProperty("message").GetString()!.Length>0);}
            using var session=await http.GetAsync("api/session").ConfigureAwait(false);session.EnsureSuccessStatusCode();
        }
        engine.LibraryCatalogFailure=null;
        using(var empty=await http.GetAsync("api/library/catalog").ConfigureAwait(false))
        {empty.EnsureSuccessStatusCode();var json=await empty.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);Assert.IsFalse(json.GetProperty("available").GetBoolean());Assert.AreEqual(JsonValueKind.Null,json.GetProperty("stageId").ValueKind);}
        Assert.AreEqual(0,diagnostics.Count);
    }

    [TestMethod]
    public async Task LibraryCatalogHostRejectsMalformedTransportInputsBeforeCallingTheEngine()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        foreach(var query in new[]{"direction=other","date=%2B1","account=someone%40example.com","cursor="+new string('x',401),"cursor=a&cursor=b"})
        {using var response=await http.GetAsync("api/library/catalog?"+query).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,response.StatusCode);}
        Assert.AreEqual(0,engine.LibraryCatalogCalls);
    }

    private sealed partial class Engine : IDesktopLibraryCatalogApplication
    {
        public DesktopLibraryCatalogPage LibraryCatalogResult {get;set;}=new(false,null,null,[],null,null);
        public Exception? LibraryCatalogFailure {get;set;}
        public int LibraryCatalogCalls {get;private set;}
        public (string? Account,string? Cursor,bool Newer,long? Date) LibraryCatalogArguments {get;private set;}
        public Task<DesktopLibraryCatalogPage> LibraryCatalogAsync(string? accountId,string? position,bool newer,long? dateUtc,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();LibraryCatalogCalls++;LibraryCatalogArguments=(accountId,position,newer,dateUtc);
            if(LibraryCatalogFailure is not null) throw LibraryCatalogFailure;
            return Task.FromResult(LibraryCatalogResult);
        }
    }
}
