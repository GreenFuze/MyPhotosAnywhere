using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mpa.Processor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task ImportRoutesRequireHostSessionCsrfAndExactCapturedIdentitiesBeforeNativeAdmission()
    {
        using var assets=new Assets();var engine=new Engine();
        engine.Import=engine.Import with{Entries=2,Metadata=new(1,0,0,1,false)};
        await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);var diagnostics=new List<Exception>();host.Diagnostic+=diagnostics.Add;
        using(var anonymous=await http.GetAsync("api/imports").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        using(var anonymousIssues=await http.GetAsync("api/import-metadata-issues?id="+engine.Import.Id.ToString("D")).ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,anonymousIssues.StatusCode);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        var csrf=(await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString();
        using(var read=await http.GetAsync("api/imports").ConfigureAwait(false))
        {
            read.EnsureSuccessStatusCode();var body=await read.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
            Assert.IsTrue(body.GetProperty("canChooseFolder").GetBoolean());var item=body.GetProperty("imports")[0];
            Assert.AreEqual("0",item.GetProperty("verified").GetString());Assert.AreEqual("1",item.GetProperty("media").GetString());
            Assert.AreEqual("photos",item.GetProperty("inputName").GetString());Assert.IsFalse(item.GetProperty("requested").GetBoolean());
            var metadata=item.GetProperty("metadata");Assert.AreEqual("1",metadata.GetProperty("total").GetString());Assert.AreEqual("0",metadata.GetProperty("ready").GetString());
            Assert.AreEqual("1",metadata.GetProperty("waiting").GetString());Assert.IsFalse(metadata.GetProperty("finished").GetBoolean());
        }
        foreach(var route in new[]{"api/import-folders","api/import-start","api/import-retry"})
        {
            using var forged=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},null,route).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized,forged.StatusCode);
        }
        foreach(var body in new object[]{new{id=Guid.Empty},new{id=Guid.NewGuid()},new{id=engine.Import.Id,path=@"C:\untrusted"},new{id=engine.Import.Id,verified=true},new{id=engine.Import.Id,metadata=new{ready="1",finished=true}}})
        {
            using var invalid=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,body,csrf,"api/import-start").ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest,invalid.StatusCode);
        }
        Assert.AreEqual(0,engine.ImportCommands.Count);
        engine.Import=engine.Import with{State="reviewing",Entries=null,Media=null};
        using(var pending=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},csrf,"api/import-start").ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Conflict,pending.StatusCode);
        Assert.AreEqual(0,engine.ImportCommands.Count);engine.Import=engine.Import with{State="ready",Entries=1,Media=1,Metadata=null};
        var destination=Guid.NewGuid();
        using(var picked=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=destination},csrf,"api/import-folders").ConfigureAwait(false))
        {picked.EnsureSuccessStatusCode();Assert.AreEqual(JsonValueKind.Null,(await picked.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("import").ValueKind);}
        using(var started=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},csrf,"api/import-start").ConfigureAwait(false))started.EnsureSuccessStatusCode();
        using(var retried=await SourceMutationAsync(http,host.LocalEndpoint,HttpMethod.Post,new{id=engine.Import.Id},csrf,"api/import-retry").ConfigureAwait(false))retried.EnsureSuccessStatusCode();
        CollectionAssert.AreEqual(new[]{("choose",destination),("start",engine.Import.Id),("retry",engine.Import.Id)},engine.ImportCommands);
        Assert.AreEqual(0,diagnostics.Count);Assert.AreEqual(0,host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task MetadataIssueReadsRequireCapturedImportAndCursorWithoutAdmittingAnyMutation()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);
        using var http=Client(host.LocalEndpoint);using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        var route="api/import-metadata-issues?id="+engine.Import.Id.ToString("D");var after=new string('0',64);
        using(var read=await http.GetAsync(route+"&after="+after).ConfigureAwait(false))
        {
            read.EnsureSuccessStatusCode();var page=await read.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);var issue=page.GetProperty("issues")[0];
            Assert.AreEqual("a.jpg.json",issue.GetProperty("name").GetString());Assert.AreEqual("input-unavailable",issue.GetProperty("code").GetString());Assert.IsTrue(issue.GetProperty("retryable").GetBoolean());Assert.AreEqual(JsonValueKind.Null,page.GetProperty("next").ValueKind);
        }
        Assert.AreEqual(1,engine.MetadataIssueReads);Assert.AreEqual(after,engine.MetadataIssueAfter);
        foreach(var invalid in new[]{"api/import-metadata-issues", "api/import-metadata-issues?id="+Guid.Empty.ToString("D"),"api/import-metadata-issues?id="+Guid.NewGuid().ToString("D"),"api/import-metadata-issues?id="+engine.Import.Id.ToString("N"),route+"&after=../outside",route+"&after="+new string('A',64),route+"&after="+new string('a',65),route+"&id="+engine.Import.Id.ToString("D")})
        {using var rejected=await http.GetAsync(invalid).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,rejected.StatusCode,invalid);}
        engine.Import=engine.Import with{State="reviewing",Entries=null,Media=null};
        using(var pending=await http.GetAsync(route).ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Conflict,pending.StatusCode);
        Assert.AreEqual(1,engine.MetadataIssueReads);Assert.AreEqual(0,engine.ImportCommands.Count);Assert.AreEqual(0,host.FrameworkDiagnostics.Count);
    }

    private sealed partial class Engine:IDesktopImportApplication
    {
        public TakeoutImportActivity Import{get;set;}=new(Guid.NewGuid(),"photos",new(Guid.NewGuid(),"account",1,"destination",1),"ready",1,1,0,false,null);
        public List<(string Action,Guid Id)> ImportCommands{get;}=[];
        public int MetadataIssueReads{get;private set;}
        public string? MetadataIssueAfter{get;private set;}
        public int OriginalChoiceReads{get;private set;}
        public bool RejectMetadataSelection{get;set;}
        public List<(Guid Import,string Sidecar,string Original)> MetadataSelections{get;}=[];
        public Task<TakeoutOriginalChoices> ImportOriginalChoicesAsync(Guid importId,string? after,CancellationToken token)
        {token.ThrowIfCancellationRequested();Assert.AreEqual(Import.Id,importId);OriginalChoiceReads++;return Task.FromResult(new TakeoutOriginalChoices([new(new string('b',64),"Folder/a.jpg","verified-file")],null));}
        public Task SelectImportMetadataOriginalAsync(Guid importId,string sidecarKey,string originalKey,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(RejectMetadataSelection)throw new TakeoutMetadataSelectionException("The first selection was retained.");MetadataSelections.Add((importId,sidecarKey,originalKey));return Task.CompletedTask;}
        public Task<DesktopImports> ImportsAsync(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(new DesktopImports(true,[Import]));}
        public Task<TakeoutMetadataIssuePage> ImportMetadataIssuesAsync(Guid importId,string? after,CancellationToken token)
        {token.ThrowIfCancellationRequested();Assert.AreEqual(Import.Id,importId);MetadataIssueReads++;MetadataIssueAfter=after;return Task.FromResult(new TakeoutMetadataIssuePage([new(new string('a',64),"a.jpg.json","input-unavailable",true)],null));}
        public Task<TakeoutImportActivity?> ChooseImportFolderAsync(Guid sourceId,CancellationToken token)
        {token.ThrowIfCancellationRequested();ImportCommands.Add(("choose",sourceId));return Task.FromResult<TakeoutImportActivity?>(null);}
        public Task<TakeoutImportActivity> StartImportAsync(Guid importId,CancellationToken token)
        {token.ThrowIfCancellationRequested();ImportCommands.Add(("start",importId));return Task.FromResult(Import with{Requested=true,State="copying"});}
        public Task<TakeoutImportActivity> RetryImportAsync(Guid importId,CancellationToken token)
        {token.ThrowIfCancellationRequested();ImportCommands.Add(("retry",importId));return Task.FromResult(Import);}
    }
}
