using System.Net;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.AppServices;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task ExportMetadataDownloadRequiresAuthenticationAndExactCapturedIdentityAndReturnsOnlyAnInertAttachment()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);using var http=Client(host.LocalEndpoint);
        var stage=Guid.NewGuid();var asset=Guid.NewGuid();var path="api/library/metadata-source?stage="+stage+"&asset="+asset+"&account=account-A";
        using(var anonymous=await http.GetAsync(path).ConfigureAwait(false))Assert.AreEqual(HttpStatusCode.Unauthorized,anonymous.StatusCode);Assert.AreEqual(0,engine.MetadataSourceCalls);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        foreach(var invalid in new[]{"api/library/metadata-source?asset="+asset,"api/library/metadata-source?stage="+Guid.Empty+"&asset="+asset,path+"&asset="+asset,path+"&account=account-B",path+"&path=C%3A%5Cprivate.json",path+"&sha256="+new string('a',64)})
        {using var response=await http.GetAsync(invalid).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,response.StatusCode);}
        Assert.AreEqual(0,engine.MetadataSourceCalls);
        using var download=await http.GetAsync(path).ConfigureAwait(false);download.EnsureSuccessStatusCode();
        Assert.AreEqual("application/octet-stream",download.Content.Headers.ContentType!.MediaType);Assert.AreEqual("attachment",download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.AreEqual("metadata-"+asset.ToString("N")+".json",download.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.IsTrue(download.Headers.CacheControl!.NoStore);Assert.AreEqual((stage,asset,"account-A"),engine.MetadataSourceArguments);
        CollectionAssert.AreEqual(engine.MetadataSourceBytes,await download.Content.ReadAsByteArrayAsync().ConfigureAwait(false));Assert.AreEqual((long)engine.MetadataSourceBytes.Length,download.Content.Headers.ContentLength);
    }
    [TestMethod]
    public async Task MissingExportMetadataHasAnExplicitNotFoundResultAndDoesNotEndTheBrowserSession()
    {
        using var assets=new Assets();var engine=new Engine{MetadataSourceMissing=true};await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);using var http=Client(host.LocalEndpoint);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        using var missing=await http.GetAsync("api/library/metadata-source?stage="+Guid.NewGuid()+"&asset="+Guid.NewGuid()).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.NotFound,missing.StatusCode);Assert.IsNull(missing.Content.Headers.ContentDisposition);
        using var session=await http.GetAsync("api/session").ConfigureAwait(false);session.EnsureSuccessStatusCode();
    }
    [TestMethod]
    [DataRow("identity")]
    [DataRow("hash")]
    [DataRow("size")]
    public async Task InvalidNativeMetadataDownloadCannotEmitAnAttachment(string damage)
    {
        using var assets=new Assets();var engine=new Engine{MetadataSourceDamage=damage};var diagnostics=new List<Exception>();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);host.Diagnostic+=diagnostics.Add;
        using var http=Client(host.LocalEndpoint);using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        using var failed=await http.GetAsync("api/library/metadata-source?stage="+Guid.NewGuid()+"&asset="+Guid.NewGuid()).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.InternalServerError,failed.StatusCode);Assert.IsNull(failed.Content.Headers.ContentDisposition);Assert.AreEqual(1,diagnostics.Count);Assert.IsInstanceOfType<InvalidDataException>(diagnostics.Single());
    }
    private sealed partial class Engine:IDesktopLibraryMetadataSourceApplication
    {
        internal byte[] MetadataSourceBytes{get;}=[0xef,0xbb,0xbf,0x7b,0x22,0xff,0,0x7d,13,10];
        internal int MetadataSourceCalls{get;private set;}
        internal (Guid Stage,Guid Asset,string? Account) MetadataSourceArguments{get;private set;}
        internal bool MetadataSourceMissing{get;set;}
        internal string? MetadataSourceDamage{get;set;}
        public Task<RecoveredMetadataSource?> LibraryMetadataSourceAsync(Guid stageId,Guid assetId,string? accountId,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();MetadataSourceCalls++;MetadataSourceArguments=(stageId,assetId,accountId);
            if(MetadataSourceMissing)return Task.FromResult<RecoveredMetadataSource?>(null);
            var bytes=MetadataSourceDamage=="size"?new byte[TakeoutMetadataDocument.MaximumSourceBytes+1]:MetadataSourceBytes;
            return Task.FromResult<RecoveredMetadataSource?>(new(stageId,MetadataSourceDamage=="identity"?Guid.NewGuid():assetId,MetadataSourceDamage=="hash"?new string('b',64):Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes));
        }
    }
}
