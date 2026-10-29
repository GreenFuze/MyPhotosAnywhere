using System.Net;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

public sealed partial class GoogleWorkspaceTests
{
    [TestMethod]
    public async Task ExactJsonByIdReadsActualBoundedBytesAndBothRevisionFencesWithoutListing()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var result=await ((IGoogleWorkspaceExactJsonReader)connection).ReadWorkspaceJsonByIdAsync("workspace","catalog-file",CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("account-A",result.AccountId);Assert.AreEqual("workspace",result.FolderId);Assert.AreEqual("catalog-file",result.FileId);
        Assert.AreEqual(7L,result.DriveVersion);CollectionAssert.AreEqual(Content,result.Bytes.ToArray());
        Assert.AreEqual(4,fixture.Calls);Assert.AreEqual(2,fixture.MetadataCalls);Assert.AreEqual(1,fixture.BlobCalls);
    }

    [TestMethod]
    public async Task ExactJsonByIdRejectsOriginalsForeignScopeAndUnboundedUnknownSizesBeforeBody()
    {
        (string Body,Failure Error)[] cases=[
            (Changed(root=>root["id"]="equal-copy"),Failure.Invalid),
            (Changed(root=>root["parents"]=new JsonArray("foreign")),Failure.Changed),
            (Changed(root=>root["mimeType"]="image/jpeg"),Failure.Invalid),
            (Changed(root=>root["mimeType"]="application/vnd.google-apps.shortcut"),Failure.Invalid),
            (Changed(root=>root["mimeType"]="application/gzip"),Failure.Changed),
            (Changed(root=>root["mimeType"]="application/octet-stream"),Failure.Changed),
            (Changed(root=>root["capabilities"]!["canDownload"]=false),Failure.Denied),
            (Changed(root=>root["size"]="0"),Failure.Invalid),
            (Changed(root=>root["size"]="262145"),Failure.Invalid),
            (Changed(root=>root["size"]="011"),Failure.Invalid),
            (Changed(root=>root["size"]=Content.Length),Failure.Invalid),
            (Changed(root=>root["trashed"]=true),Failure.Changed)
        ];
        foreach(var item in cases)
        {
            using var fixture=new Fixture { Before=item.Body };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await ExpectFailure(item.Error,()=>connection.ReadWorkspaceJsonByIdAsync("workspace","catalog-file",CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1,fixture.MetadataCalls);Assert.AreEqual(0,fixture.BlobCalls);
        }
    }

    [TestMethod]
    public async Task ExactJsonByIdCannotReturnAChangingMovedOrNewlyRestrictedCapture()
    {
        foreach(var metadata in new[]{Changed(root=>root["version"]="8"),Changed(root=>root["parents"]=new JsonArray("foreign")),
            Changed(root=>root["mimeType"]="application/gzip"),Changed(root=>root["capabilities"]!["canDownload"]=false)})
        {
            using var fixture=new Fixture { After=metadata };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<IOException>(()=>connection.ReadWorkspaceJsonByIdAsync("workspace","catalog-file",CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(2,fixture.MetadataCalls);Assert.AreEqual(1,fixture.BlobCalls);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExactJsonByIdRejectsTruncatedOrOversizedBodiesForKnownAndUnknownLengths(bool unknownLength)
    {
        foreach(var item in new[]{(Body:Content[..^1],Error:Failure.Changed),(Body:Content.Concat(new byte[]{0}).ToArray(),Error:Failure.Invalid)})
        {
            using var fixture=new Fixture { Blob=item.Body,UnknownLength=unknownLength };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await ExpectFailure(item.Error,()=>connection.ReadWorkspaceJsonByIdAsync("workspace","catalog-file",CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1,fixture.MetadataCalls);Assert.AreEqual(1,fixture.BlobCalls);
        }
    }

    [TestMethod]
    public async Task ExactJsonByIdCancellationCannotAdmitTheLateActualBody()
    {
        using var caller=new CancellationTokenSource();using var fixture=new Fixture { BlobObserved=caller.Cancel };
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(()=>connection.ReadWorkspaceJsonByIdAsync("workspace","catalog-file",caller.Token)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.BlobCalls);Assert.AreEqual(1,fixture.MetadataCalls);
    }

    [TestMethod]
    public async Task ExactJsonByIdRejectsInvalidArgumentsBeforeIoAndPreservesNetworkFailures()
    {
        using var fixture=new Fixture { BlobStatus=HttpStatusCode.ServiceUnavailable };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        foreach(var file in new[]{"root","workspace","invalid/path"})
            Assert.ThrowsExactly<ArgumentException>(()=>connection.ReadWorkspaceJsonByIdAsync("workspace",file,CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>connection.ReadWorkspaceJsonByIdAsync("root","catalog-file",CancellationToken.None));
        Assert.AreEqual(0,fixture.Calls);using var caller=new CancellationTokenSource();caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>connection.ReadWorkspaceJsonByIdAsync("workspace","catalog-file",caller.Token)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Calls);
        var error=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.ReadWorkspaceJsonByIdAsync("workspace","catalog-file",CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable,error.StatusCode);Assert.AreEqual(1,fixture.MetadataCalls);Assert.AreEqual(1,fixture.BlobCalls);
    }
}
