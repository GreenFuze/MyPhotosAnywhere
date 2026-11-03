using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task ExplicitMetadataAssociationRequiresAuthenticatedHostCsrfAndOnlyCapturedKeys()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);var sidecar=new string('a',64);var original=new string('b',64);
        var route="api/import-original-choices?id="+engine.Import.Id.ToString("D");
        using(var anonymous=await http.GetAsync(route).ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        var csrf=(await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString();
        using(var choices=await http.GetAsync(route).ConfigureAwait(false))
        {choices.EnsureSuccessStatusCode();var body=await choices.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);Assert.AreEqual("verified-file",body.GetProperty("originals")[0].GetProperty("fileId").GetString());Assert.AreEqual(original,body.GetProperty("originals")[0].GetProperty("key").GetString());}
        foreach(var query in new[]{route+"&after=../outside",route+"&after="+Guid.Empty.ToString("D"),route+"&after="+Guid.NewGuid().ToString("N"),route+"&id="+engine.Import.Id.ToString("D"),"api/import-original-choices?id="+Guid.NewGuid().ToString("D")})
        {using var rejected=await http.GetAsync(query).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,rejected.StatusCode);}
        var valid=new{id=engine.Import.Id,sidecarKey=sidecar,originalKey=original};
        using(var noCsrf=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,valid,null,"api/import-metadata-original").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,noCsrf.StatusCode);
        foreach(var body in new object[]{new{id=Guid.Empty,sidecarKey=sidecar,originalKey=original},new{id=Guid.NewGuid(),sidecarKey=sidecar,originalKey=original},new{id=engine.Import.Id,sidecarKey="outside.json",originalKey=original},new{id=engine.Import.Id,sidecarKey=sidecar,originalKey="provider-id"},new{id=engine.Import.Id,sidecarKey=sidecar,originalKey=original,sha256=sidecar},new{id=engine.Import.Id,sidecarKey=sidecar,originalKey=original,verified=true},new{id=engine.Import.Id,sidecarKey=sidecar}})
        {using var rejected=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,body,csrf,"api/import-metadata-original").ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,rejected.StatusCode);}
        Assert.AreEqual(0,engine.MetadataSelections.Count);Assert.AreEqual(1,engine.OriginalChoiceReads);
        using(var selected=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,valid,csrf,"api/import-metadata-original").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.NoContent,selected.StatusCode);
        engine.RejectMetadataSelection=true;
        using(var retained=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,valid,csrf,"api/import-metadata-original").ConfigureAwait(false))
        {Assert.AreEqual(HttpStatusCode.Conflict,retained.StatusCode);Assert.AreEqual("The first selection was retained.",(await retained.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("message").GetString());}
        Assert.AreEqual((engine.Import.Id,sidecar,original),engine.MetadataSelections.Single());Assert.AreEqual(0,engine.ImportCommands.Count);Assert.AreEqual(0,host.FrameworkDiagnostics.Count);
    }
}
