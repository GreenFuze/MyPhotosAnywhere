using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleMediaTests
{
    [TestMethod]
    public async Task VersionedPreviewObservesTheSameActualFileVersionBeforeAndAfterThePrivateThumbnailRead()
    {
        await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);
        using var handler=new Handler((request,count)=>count switch
        {
            1=>Identity(),2=>VersionedPreviewMetadata(request,"1",true),
            3=>PreviewBytes(request,[255,216,255,42]),4=>VersionedPreviewMetadata(request,"1",false),_=>throw new InvalidOperationException("Unexpected preview request.")
        });
        await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);Assert.IsInstanceOfType<IGoogleVersionedMediaPreview>(connection);
        var preview=await connection.ReadVersionedMediaPreviewAsync("file-A",1,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(preview);Assert.AreEqual("image/jpeg",preview.MimeType);CollectionAssert.AreEqual(new byte[]{255,216,255,42},preview.Bytes);Assert.AreEqual(4,handler.Calls);
        Assert.IsFalse(JsonSerializer.Serialize(preview).Contains("googleusercontent",StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VersionedPreviewRejectsAChangedVersionAtEitherObservationWithoutReturningCapturedBytes(bool after)
    {
        await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);
        using var handler=new Handler((request,count)=>count switch
        {
            1=>Identity(),2=>VersionedPreviewMetadata(request,after?"1":"2",true),3=>PreviewBytes(request,[255,216,255,42]),4=>VersionedPreviewMetadata(request,"2",false),_=>throw new InvalidOperationException("Unexpected preview request.")
        });
        await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);
        await Assert.ThrowsExactlyAsync<DriveMediaVersionChangedException>(()=>connection.ReadVersionedMediaPreviewAsync("file-A",1,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(after?4:2,handler.Calls);
    }

    [TestMethod]
    public async Task VersionedPreviewRejectsMalformedOrForeignFileVersionMetadataBeforeSendingAThumbnailCredential()
    {
        foreach(var body in new[]{"{\"id\":\"file-A\",\"trashed\":false}","{\"id\":\"file-A\",\"version\":1}","{\"id\":\"file-A\",\"version\":\"0\"}",
            "{\"id\":\"file-A\",\"version\":\"01\"}","{\"id\":\"file-A\",\"version\":\"-1\"}","{\"id\":\"file-A\",\"version\":\"9223372036854775808\"}","{\"id\":\"other-file\",\"version\":\"1\"}"})
        {
            await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);
            using var handler=new Handler((_,count)=>count==1?Identity():count==2?Json(body):throw new InvalidOperationException("Unvalidated thumbnail IO."));
            await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.ReadVersionedMediaPreviewAsync("file-A",1,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(2,handler.Calls);
        }
    }

    [TestMethod]
    public async Task VersionedPreviewKeepsTheCredentialOriginAllowlistAndNeverDownloadsOriginalMedia()
    {
        foreach(var url in new[]{"http://lh3.googleusercontent.com/preview","https://foreign.invalid/preview","https://lh3.googleusercontent.com:8443/preview","https://user@lh3.googleusercontent.com/preview","https://lh3.googleusercontent.com/preview#fragment"})
        {
            await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);
            using var handler=new Handler((_,count)=>count==1?Identity():count==2?Json(JsonSerializer.Serialize(new{id="file-A",version="1",trashed=false,thumbnailLink=url})):throw new InvalidOperationException("Untrusted thumbnail IO."));
            await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.ReadVersionedMediaPreviewAsync("file-A",1,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(2,handler.Calls);
        }
    }

    [TestMethod]
    public async Task VersionedPreviewTrashedOrMissingThumbnailsReturnUnavailableWithoutFetchingAnOriginal()
    {
        foreach(var body in new[]{"{\"id\":\"file-A\",\"version\":\"1\",\"trashed\":true,\"thumbnailLink\":\"https://foreign.invalid/preview\"}","{\"id\":\"file-A\",\"version\":\"1\",\"trashed\":false}"})
        {
            await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);
            using var handler=new Handler((_,count)=>count==1?Identity():count==2?Json(body):throw new InvalidOperationException("Original download attempted."));
            await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);Assert.IsNull(await connection.ReadVersionedMediaPreviewAsync("file-A",1,CancellationToken.None).ConfigureAwait(false));Assert.AreEqual(2,handler.Calls);
        }
    }

    [TestMethod]
    public async Task VersionedPreviewEnforcesTheExistingFourMibAndSupportedImageByteBounds()
    {
        foreach(var bytes in new[]{new byte[]{1,2,3},new byte[4*1024*1024+1]})
        {
            await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);
            using var handler=new Handler((request,count)=>count switch {1=>Identity(),2=>VersionedPreviewMetadata(request,"1",true),3=>PreviewBytes(request,bytes),_=>throw new InvalidOperationException("Invalid preview bytes reached final admission.")});
            await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.ReadVersionedMediaPreviewAsync("file-A",1,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(3,handler.Calls);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VersionedPreviewCancellationAndExitJoinTheActualThumbnailTransportBeforeRejectingLateBytes(bool exit)
    {
        await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);using var handler=new VersionedPreviewHeldHandler();
        await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);using var canceled=new CancellationTokenSource();
        var reading=connection.ReadVersionedMediaPreviewAsync("file-A",1,canceled.Token);Task? stopping=null;
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);if(exit) stopping=connection.DisposeAsync().AsTask();else canceled.Cancel();
            Assert.IsFalse(reading.IsCompleted);if(stopping is not null) Assert.IsFalse(stopping.IsCompleted);
        }
        finally {handler.Release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);if(stopping is not null) await stopping.ConfigureAwait(false);Assert.AreEqual(3,handler.Calls);
    }

    [TestMethod]
    public async Task VersionedPreviewRejectsInvalidCapturedInputsBeforeAnyProviderRequest()
    {
        await using var vault=new TestCredentialVault();var id=Guid.NewGuid();await GrantAsync(vault,id).ConfigureAwait(false);using var handler=new Handler((_,_)=>throw new InvalidOperationException("Invalid input reached provider."));
        await using var connection=new GoogleAccountConnection(Configuration(),vault,id,"account-A",handler);
        Assert.ThrowsExactly<ArgumentException>(()=>connection.ReadVersionedMediaPreviewAsync("../foreign",1,CancellationToken.None));Assert.ThrowsExactly<ArgumentException>(()=>connection.ReadVersionedMediaPreviewAsync("file-A",0,CancellationToken.None));
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>connection.ReadVersionedMediaPreviewAsync("file-A",1,canceled.Token)).ConfigureAwait(false);Assert.AreEqual(0,handler.Calls);
    }

    private static HttpResponseMessage VersionedPreviewMetadata(HttpRequestMessage request,string version,bool thumbnail)
    {
        Assert.AreEqual("www.googleapis.com",request.RequestUri!.Host);Assert.AreEqual("/drive/v3/files/file-A",request.RequestUri.AbsolutePath);
        StringAssert.Contains(request.RequestUri.Query,"supportsAllDrives=true");StringAssert.Contains(request.RequestUri.Query,"version");Assert.IsFalse(request.RequestUri.Query.Contains("alt=media",StringComparison.Ordinal));
        return Json(thumbnail?JsonSerializer.Serialize(new{id="file-A",version,trashed=false,thumbnailLink="https://lh3.googleusercontent.com/private-preview"}):JsonSerializer.Serialize(new{id="file-A",version,trashed=false}));
    }
    private static HttpResponseMessage PreviewBytes(HttpRequestMessage request,byte[] bytes)
    {Assert.AreEqual("lh3.googleusercontent.com",request.RequestUri!.Host);return new(HttpStatusCode.OK) {Content=new ByteArrayContent(bytes)};}
    private sealed class VersionedPreviewHeldHandler : HttpMessageHandler
    {
        internal int Calls {get;private set;}
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();Assert.AreEqual(HttpMethod.Get,request.Method);Assert.AreEqual("synthetic-access",request.Headers.Authorization!.Parameter);Calls++;
            if(Calls==1) return Identity();if(Calls==2) return VersionedPreviewMetadata(request,"1",true);
            Assert.AreEqual(3,Calls);Entered.TrySetResult();await Release.Task.ConfigureAwait(false);return PreviewBytes(request,[255,216,255,42]);
        }
    }
}
