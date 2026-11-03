using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task MetadataCleanupRequiresHostCsrfPausedProcessingAndOnlyAnExactReviewedImport()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);
        using(var anonymous=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},null,"api/import-metadata-cleanup").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        var csrf=(await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString();
        using(var forged=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},null,"api/import-metadata-cleanup").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,forged.StatusCode);
        using(var running=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},csrf,"api/import-metadata-cleanup").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Conflict,running.StatusCode);
        engine.ImportsPaused=true;
        foreach(var body in new object[]{new{id=Guid.Empty},new{id=Guid.NewGuid()},new{id=engine.Import.Id.ToString("N")},new{id=engine.Import.Id,path="../source.bin"},new{id=engine.Import.Id,backup=true},new{id=engine.Import.Id,publicationId=Guid.NewGuid()}})
        {using var rejected=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,body,csrf,"api/import-metadata-cleanup").ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,rejected.StatusCode);}
        Assert.AreEqual(0,engine.MetadataCleanups);
        using(var query=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},csrf,"api/import-metadata-cleanup?path=source.bin").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.BadRequest,query.StatusCode);
        using(var duplicateRequest=new HttpRequestMessage(HttpMethod.Post,new Uri(host.LocalEndpoint,"api/import-metadata-cleanup")))
        {
            duplicateRequest.Headers.Add("Origin",host.LocalEndpoint.GetLeftPart(UriPartial.Authority));duplicateRequest.Headers.Add("X-MPA-CSRF",csrf);
            duplicateRequest.Content=new StringContent("{\"id\":\""+engine.Import.Id.ToString("D")+"\",\"id\":\""+engine.Import.Id.ToString("D")+"\"}",System.Text.Encoding.UTF8,"application/json");
            using var duplicate=await http.SendAsync(duplicateRequest).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,duplicate.StatusCode);
        }
        Assert.AreEqual(0,engine.MetadataCleanups);
        using(var cleaned=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},csrf,"api/import-metadata-cleanup").ConfigureAwait(false))
        {cleaned.EnsureSuccessStatusCode();var result=await cleaned.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);Assert.AreEqual("1",result.GetProperty("retired").GetString());Assert.AreEqual("2",result.GetProperty("kept").GetString());}
        Assert.AreEqual(1,engine.MetadataCleanups);Assert.AreEqual(0,engine.ImportCommands.Count);Assert.AreEqual(0,host.FrameworkDiagnostics.Count);
    }
}
