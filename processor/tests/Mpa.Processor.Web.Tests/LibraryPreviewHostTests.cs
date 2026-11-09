using System.Net;
using System.Net.Http.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.Web.Tests;

public sealed partial class WebHostTests
{
    [TestMethod]
    public async Task LibraryPreviewTransportRequiresAuthenticationAndExactStageAssetAccountBeforeReturningOnlyBoundedImageBytes()
    {
        using var assets=new Assets();var engine=new Engine();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);using var http=Client(host.LocalEndpoint);
        var stage=Guid.NewGuid();var asset=Guid.NewGuid();var path="api/library/preview?stage="+stage+"&asset="+asset+"&account=account-A";
        using(var anonymous=await http.GetAsync(path).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized,anonymous.StatusCode);Assert.AreEqual(0,engine.LibraryPreviewCalls);
        using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();
        foreach(var invalid in new[]{"api/library/preview?asset="+asset,"api/library/preview?stage="+Guid.Empty+"&asset="+asset,path+"&asset="+asset,path+"&account=account-B"})
        {using var response=await http.GetAsync(invalid).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.BadRequest,response.StatusCode);}
        Assert.AreEqual(0,engine.LibraryPreviewCalls);
        using var preview=await http.GetAsync(path).ConfigureAwait(false);preview.EnsureSuccessStatusCode();Assert.AreEqual("image/jpeg",preview.Content.Headers.ContentType!.MediaType);
        CollectionAssert.AreEqual(new byte[]{255,216,255,42},await preview.Content.ReadAsByteArrayAsync().ConfigureAwait(false));Assert.AreEqual((stage,asset,"account-A"),engine.LibraryPreviewArguments);Assert.AreEqual(1,engine.LibraryPreviewCalls);
    }

    [TestMethod]
    public async Task LibraryPreviewChangedProviderVersionAndUnsupportedCapabilityKeepTheAuthenticatedSessionAndMissingSlots()
    {
        using var assets=new Assets();var engine=new Engine();var diagnostics=new List<Exception>();await using var host=await DesktopWebHost.StartAsync(engine,assets.Path,null,CancellationToken.None).ConfigureAwait(false);host.Diagnostic+=diagnostics.Add;
        using var http=Client(host.LocalEndpoint);using var login=await LoginAsync(http,host.OpenLocalBrowser(),host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);login.EnsureSuccessStatusCode();var path="api/library/preview?stage="+Guid.NewGuid()+"&asset="+Guid.NewGuid();
        foreach(var failure in new (Exception Error,HttpStatusCode Status)[]{(new DriveMediaVersionChangedException(),HttpStatusCode.Conflict),(new DesktopLibraryPreviewUnavailableException(),HttpStatusCode.ServiceUnavailable)})
        {
            engine.LibraryPreviewFailure=failure.Error;using(var response=await http.GetAsync(path).ConfigureAwait(false)) Assert.AreEqual(failure.Status,response.StatusCode);
            using var session=await http.GetAsync("api/session").ConfigureAwait(false);session.EnsureSuccessStatusCode();
        }
        engine.LibraryPreviewFailure=null;engine.LibraryPreviewResult=null;using var missing=await http.GetAsync(path).ConfigureAwait(false);Assert.AreEqual(HttpStatusCode.NotFound,missing.StatusCode);Assert.AreEqual(0,diagnostics.Count);
    }

    private sealed partial class Engine : IDesktopLibraryPreviewApplication
    {
        internal int LibraryPreviewCalls {get;private set;}
        internal (Guid Stage,Guid Asset,string? Account) LibraryPreviewArguments {get;private set;}
        internal Exception? LibraryPreviewFailure {get;set;}
        internal DesktopPreview? LibraryPreviewResult {get;set;}=new([255,216,255,42],"image/jpeg");
        public Task<DesktopPreview?> LibraryPreviewAsync(Guid stageId,Guid assetId,string? accountId,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();LibraryPreviewCalls++;LibraryPreviewArguments=(stageId,assetId,accountId);
            if(LibraryPreviewFailure is not null) throw LibraryPreviewFailure;return Task.FromResult(LibraryPreviewResult);
        }
    }
}
