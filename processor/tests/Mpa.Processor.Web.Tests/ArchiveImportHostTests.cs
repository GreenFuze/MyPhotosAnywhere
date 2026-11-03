using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task ArchiveSelectionRequiresHostCsrfAndOnlyPassesDestinationToNativeDialogWithoutStartingCopy()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);var source=Guid.NewGuid();
        using(var anonymous=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=source},null,"api/import-archives").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        var csrf=(await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString();
        using(var progress=await http.GetAsync("api/imports").ConfigureAwait(false)){progress.EnsureSuccessStatusCode();Assert.IsTrue((await progress.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("canChooseArchives").GetBoolean());}
        using(var forged=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=source},null,"api/import-archives").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,forged.StatusCode);
        foreach(var body in new object[]{new{id=Guid.Empty},new{id=source,path="C:/untrusted.zip"},new{id=source,parts=new[]{"a.zip","b.zip"}},new{id=source,verified=true},new{id=source,backup="receipt"}})
        {using var rejected=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,body,csrf,"api/import-archives").ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,rejected.StatusCode);}
        Assert.AreEqual(0,engine.ImportCommands.Count);
        using(var picked=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=source},csrf,"api/import-archives").ConfigureAwait(false))
        {picked.EnsureSuccessStatusCode();Assert.AreEqual(JsonValueKind.Null,(await picked.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("import").ValueKind);}
        CollectionAssert.AreEqual(new[]{("archives",source)},engine.ImportCommands);Assert.IsFalse(engine.Import.Requested);Assert.AreEqual(0,host.FrameworkDiagnostics.Count);
    }
}
