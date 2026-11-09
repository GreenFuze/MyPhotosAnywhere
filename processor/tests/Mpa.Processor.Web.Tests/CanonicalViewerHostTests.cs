using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task CanonicalViewerTransportRejectsAnEmptyCapturedStageBeforeEngineAdmissionAndPreservesTheSession()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        var json=await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);var csrf=json.GetProperty("csrf").GetString();
        using var request=new HttpRequestMessage(HttpMethod.Post,"api/viewers") {Content=JsonContent.Create(new CatalogViewerRequest(Guid.NewGuid(),Guid.NewGuid(),null,Guid.Empty))};request.Headers.Add("X-MPA-CSRF",csrf);request.Headers.Add("Origin",host.LocalEndpoint.GetLeftPart(UriPartial.Authority));
        using var rejected=await http.SendAsync(request).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,rejected.StatusCode);Assert.AreEqual(0,engine.ViewerCalls.Count);
        using var session=await http.GetAsync("api/session").ConfigureAwait(false);session.EnsureSuccessStatusCode();
    }
}
