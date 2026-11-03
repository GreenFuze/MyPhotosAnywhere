using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed partial class GoogleWorkspaceConditionalJsonTests
{
    private static readonly byte[] Initial="{\"head\":1}"u8.ToArray();
    private static readonly byte[] Next="{\"head\":2}"u8.ToArray();
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));

    [TestMethod]
    public async Task ConditionalJsonCapturesOwnedBytesAndUsesOnlyTheActualSameConnectionCondition()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.IsInstanceOfType<IGoogleWorkspaceConditionalJson>(connection);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(7L,first.Version);Assert.AreEqual("\"actual-7\"",first.EntityTag);Assert.AreEqual(Hash(Initial),first.Sha256);
        var exposed=first.Bytes;MemoryMarshal.AsMemory(exposed).Span[0]=0;CollectionAssert.AreEqual(Initial,first.Bytes.ToArray());
        var input=Next.ToArray();fixture.OnMetadata=call=>{if (call==3) input[0]=0;};
        var after=await connection.ReplaceConditionalWorkspaceJsonAsync(first,input,Hash(Next),CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(Next,fixture.Stored);CollectionAssert.AreEqual(Next,after.Bytes.ToArray());Assert.AreEqual(0,input[0]);
        Assert.AreEqual(8L,after.Version);Assert.AreEqual("account-A",after.AccountId);Assert.AreEqual("workspace",after.FolderId);
        Assert.AreEqual(first.FileId,after.FileId);Assert.AreEqual(1,fixture.Puts);Assert.AreEqual(5,fixture.MetadataReads);Assert.AreEqual(2,fixture.BlobReads);
        Assert.IsTrue(fixture.WriteResponse!.Disposed); // Success body is neither trusted nor consumed.
    }

    [TestMethod]
    public async Task ConditionalJsonRejectsOriginalsWeakTagsInvalidSizesAndForeignScopeBeforeBody()
    {
        (Action<JsonObject> Change,ConditionalFailure Error)[] bad=[(root=>root["mimeType"]="image/jpeg",ConditionalFailure.Changed),
            (root=>root["mimeType"]="application/gzip",ConditionalFailure.Changed),(root=>root["id"]="equal-copy",ConditionalFailure.Changed),
            (root=>root["parents"]=new JsonArray(new JsonObject { ["id"]="foreign" }),ConditionalFailure.Changed),
            (root=>root["etag"]="W/\"weak\"",ConditionalFailure.Invalid),(root=>root["etag"]="*",ConditionalFailure.Invalid),
            (root=>root["etag"]="7",ConditionalFailure.Invalid),(root=>root["fileSize"]="0",ConditionalFailure.Invalid),
            (root=>root["fileSize"]="262145",ConditionalFailure.Invalid),(root=>root["version"]="07",ConditionalFailure.Invalid),
            (root=>root["labels"]!["trashed"]=true,ConditionalFailure.Changed),(root=>root["capabilities"]!["canDownload"]=false,ConditionalFailure.Denied),
            (root=>root["driveId"]="invalid/corpus",ConditionalFailure.Invalid)];
        foreach(var item in bad)
        {
            using var fixture=new Fixture { Transform=(_,root)=>item.Change(root) };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await ExpectConditionalFailure(item.Error,()=>connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0,fixture.BlobReads);Assert.AreEqual(0,fixture.Puts);
        }
    }

    [TestMethod]
    public async Task ConditionalJsonRejectsChangedWithinCaptureFencesAndTruncatedActualBodies()
    {
        using(var fixture=new Fixture { Transform=(call,root)=>{if (call==2) root["version"]="8";} })
        {
            await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None)).ConfigureAwait(false);
        }
        foreach(var length in new[]{Initial.Length-1,Initial.Length+1})
        {
            using var fixture=new Fixture { Body=_=>new byte[length] };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            await ExpectConditionalFailure(length<Initial.Length ? ConditionalFailure.Changed : ConditionalFailure.Invalid,
                ()=>connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0,fixture.Puts);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConditionalJsonDoesNotRewriteAStaleOrRewoundCapture(bool rewind)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        if (rewind) fixture.Version=6;else fixture.Tag="\"another-current\"";
        var failure=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed,failure.StatusCode);Assert.AreEqual(0,fixture.Puts);CollectionAssert.AreEqual(Initial,fixture.Stored);
    }

    [TestMethod]
    public async Task ConditionalJsonAllowsMonotonicNumericMovementBetweenSeparatelyFencedCapturesWithoutUsingItAsEtag()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        fixture.Version=8; // The captured strong tag remains actual; version is never If-Match.
        var next=await connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(9L,next.Version);Assert.AreEqual(1,fixture.Puts);
    }

    [TestMethod]
    public async Task ConditionalJsonCannotOverwriteWhenTheProviderChangesLengthUnderTheSameTag()
    {
        using var fixture=new Fixture { Transform=(call,root)=>{if (call>=3) root["fileSize"]="12";} };
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(Initial.Length,first.ByteLength);
        var failure=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed,failure.StatusCode);Assert.AreEqual(0,fixture.Puts);CollectionAssert.AreEqual(Initial,fixture.Stored);
    }

    [TestMethod]
    public async Task ConditionalJsonPreservesServer412AndNeverFallsBackToV3OrAnUnconditionalUpdate()
    {
        using var fixture=new Fixture { PutStatus=HttpStatusCode.PreconditionFailed };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        var failure=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed,failure.StatusCode);Assert.AreEqual(1,fixture.Puts);Assert.AreEqual(3,fixture.MetadataReads);
        CollectionAssert.AreEqual(Initial,fixture.Stored);Assert.IsTrue(fixture.WriteResponse!.Disposed);
    }

    [TestMethod]
    public async Task ConditionalJsonLostCompletionRecoversSameActualIdAndExactPendingBytesWithoutAnotherWrite()
    {
        using var fixture=new Fixture { LoseCompletion=true };await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
        var recovered=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.FileId,recovered.FileId);Assert.AreEqual(Hash(Next),recovered.Sha256);CollectionAssert.AreEqual(Next,recovered.Bytes.ToArray());Assert.AreEqual(1,fixture.Puts);
    }

    [TestMethod]
    public async Task ConditionalJsonRefusesAnOriginalMimeOrRetiredEditPermissionAtTheWriteBoundary()
    {
        using(var fixture=new Fixture { Transform=(call,root)=>{if(call>=3) root["mimeType"]="image/jpeg";} })
        {
            await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0,fixture.Puts);CollectionAssert.AreEqual(Initial,fixture.Stored);
        }
        using(var fixture=new Fixture { Transform=(call,root)=>{if(call>=3) root["capabilities"]!["canEdit"]=false;} })
        {
            await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
            var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
            var failure=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden,failure.StatusCode);Assert.AreEqual(0,fixture.Puts);CollectionAssert.AreEqual(Initial,fixture.Stored);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConditionalJsonRetainsActualSharedDriveScopeAndRefusesACorpusMove(bool moved)
    {
        using var fixture=new Fixture { Transform=(call,root)=>root["driveId"]=moved && call>=3 ? "different-drive" : "shared-drive" };
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("shared-drive",first.SharedDriveId);
        if (moved)
        {
            var failure=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.PreconditionFailed,failure.StatusCode);Assert.AreEqual(0,fixture.Puts);CollectionAssert.AreEqual(Initial,fixture.Stored);
        }
        else
        {
            var next=await connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("shared-drive",next.SharedDriveId);Assert.AreEqual(1,fixture.Puts);CollectionAssert.AreEqual(Next,fixture.Stored);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConditionalJsonCannotTreatA200OrDifferentReadbackAsAVerifiedUpdate(bool unchangedCondition)
    {
        using var fixture=new Fixture { Transform=(call,root)=>{if(call>=4 && unchangedCondition) {root["version"]="7";root["etag"]="\"actual-7\"";}} };
        if (!unchangedCondition) fixture.AfterPut=()=>fixture.Stored[0]=0;
        await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.Puts);Assert.AreEqual(5,fixture.MetadataReads);Assert.IsTrue(fixture.WriteResponse!.Disposed);
    }

    [TestMethod]
    public async Task ConditionalJsonRejectsForeignConnectionsInvalidInputAndUnchangedWritesBeforeNetwork()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        using var other=new Fixture();await using var foreign=await other.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(()=>foreign.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None));Assert.AreEqual(0,other.Calls);
        Assert.ThrowsExactly<ArgumentException>(()=>connection.CaptureConditionalWorkspaceJsonAsync("root","current-file",CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Initial,Hash(Initial),CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Initial),CancellationToken.None));
        var invalid="[]"u8.ToArray();Assert.ThrowsExactly<InvalidDataException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,invalid,Hash(invalid),CancellationToken.None));
        Assert.AreEqual(4,fixture.Calls);Assert.AreEqual(0,fixture.Puts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConditionalJsonCancellationOrCloseJoinsPhysicalWriteBeforeAdmittingOrReleasingItsOwner(bool exit)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforePut=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var writing=connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if (exit) stopping=connection.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try { Assert.IsFalse(writing.IsCompleted);if (exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.HandlerDisposed); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(()=>writing).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.Puts);Assert.AreEqual(3,fixture.MetadataReads);CollectionAssert.AreEqual(Next,fixture.Stored);Assert.IsTrue(fixture.WriteResponse!.Disposed);
    }

    [TestMethod]
    public async Task ClosingAdmissionInsideAnAlreadyAdmittedWriteCannotStartReadback()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        var first=await connection.CaptureConditionalWorkspaceJsonAsync("workspace","current-file",CancellationToken.None).ConfigureAwait(false);
        var stopping=Task.CompletedTask;
        fixture.AfterPut=()=>stopping=connection.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<OperationCanceledException>(()=>connection.ReplaceConditionalWorkspaceJsonAsync(first,Next,Hash(Next),CancellationToken.None)).ConfigureAwait(false);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(1,fixture.Puts);Assert.AreEqual(3,fixture.MetadataReads);Assert.AreEqual(1,fixture.BlobReads);
        CollectionAssert.AreEqual(Next,fixture.Stored);Assert.IsTrue(fixture.WriteResponse!.Disposed);Assert.IsTrue(fixture.HandlerDisposed);
    }

    private enum ConditionalFailure { Invalid,Changed,Denied }
    private static Task ExpectConditionalFailure(ConditionalFailure expected,Func<Task> operation)=>expected switch
    {
        ConditionalFailure.Invalid=>Assert.ThrowsExactlyAsync<InvalidDataException>(operation),
        ConditionalFailure.Changed=>Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(operation),
        ConditionalFailure.Denied=>Assert.ThrowsExactlyAsync<DriveWorkspaceDownloadDeniedException>(operation),
        _=>throw new InvalidOperationException("Unknown expected failure.")
    };
    private sealed class Fixture : HttpMessageHandler
    {
        private readonly TestCredentialVault _vault=new();
        public byte[] Stored { get; private set; }=Initial.ToArray();
        public long Version { get; set; }=7;
        public string Tag { get; set; }="\"actual-7\"";
        public Action<int,JsonObject>? Transform { get; init; }
        public Action<int>? OnMetadata { get; set; }
        public Func<int,byte[]>? Body { get; init; }
        public HttpStatusCode PutStatus { get; init; }=HttpStatusCode.OK;
        public bool LoseCompletion { get; init; }
        public Func<Task>? BeforePut { get; set; }
        public Action? AfterPut { get; set; }
        public int Calls { get; private set; }
        public int MetadataReads { get; private set; }
        public int BlobReads { get; private set; }
        public int Puts { get; private set; }
        public bool HandlerDisposed { get; private set; }
        public UnreadWriteContent? WriteResponse { get; private set; }
        public async Task<GoogleAccountConnection> ConnectAsync()
        {
            var id=Guid.NewGuid();var store=new GoogleCredentialDataStore(_vault,id,()=>CancellationToken.None);
            await store.StoreAsync(store.UserId,GoogleCredentialTests.Grant()).ConfigureAwait(false);
            var path=Path.Combine(Path.GetTempPath(),"mpa-conditional-oauth-"+Guid.NewGuid().ToString("N")+".json");
            try
            {
                File.WriteAllText(path,JsonSerializer.Serialize(new {installed=new {client_id="synthetic.apps.googleusercontent.com",client_secret="synthetic-public-client"}}));
                return new(DesktopOAuthConfiguration.Load(path),_vault,id,"account-A",this);
            }
            finally { File.Delete(path); }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls++;Assert.AreEqual("https",request.RequestUri!.Scheme);Assert.AreEqual("www.googleapis.com",request.RequestUri.Host);
            Assert.AreEqual("synthetic-access",request.Headers.Authorization!.Parameter);var path=request.RequestUri.AbsolutePath;
            if (path=="/drive/v3/about") return Json("""{"user":{"permissionId":"account-A","displayName":"Synthetic"}}""");
            StringAssert.Contains(request.RequestUri.Query,"supportsAllDrives=true");
            if (request.Method==HttpMethod.Put)
            {
                Assert.AreEqual("/upload/drive/v2/files/current-file",path);StringAssert.Contains(request.RequestUri.Query,"uploadType=media");
                Assert.AreEqual("application/json",request.Content!.Headers.ContentType!.MediaType);Assert.AreEqual(Tag,request.Headers.IfMatch.Single().ToString());
                Puts++;var bytes=await request.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (BeforePut is not null) await BeforePut().ConfigureAwait(false); // Deliberately ignores cancellation while held.
                if (PutStatus==HttpStatusCode.OK) { Stored=bytes;Version++;Tag="\"actual-"+Version.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\""; }
                AfterPut?.Invoke();
                if (LoseCompletion) throw new HttpRequestException("Synthetic actual completion discarded before response.");
                WriteResponse=new();return new(PutStatus) {Content=WriteResponse};
            }
            Assert.AreEqual(HttpMethod.Get,request.Method);
            if (path=="/drive/v3/files/current-file")
            { StringAssert.Contains(request.RequestUri.Query,"alt=media");BlobReads++;return new(HttpStatusCode.OK) {Content=new ByteArrayContent(Body?.Invoke(BlobReads) ?? Stored)}; }
            Assert.AreEqual("/drive/v2/files/current-file",path);MetadataReads++;OnMetadata?.Invoke(MetadataReads);
            var root=new JsonObject { ["id"]="current-file",["etag"]=Tag,["version"]=Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["fileSize"]=Stored.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),["mimeType"]="application/json",
                ["parents"]=new JsonArray(new JsonObject {["id"]="workspace"}),["labels"]=new JsonObject {["trashed"]=false},
                ["capabilities"]=new JsonObject {["canDownload"]=true,["canEdit"]=true}};
            Transform?.Invoke(MetadataReads,root);return Json(root.ToJsonString());
        }
        private static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK) {Content=new StringContent(body,Encoding.UTF8,"application/json")};
        protected override void Dispose(bool disposing)
        { if (disposing) { HandlerDisposed=true;_vault.DisposeAsync().AsTask().GetAwaiter().GetResult(); }base.Dispose(disposing); }
    }
    private sealed class UnreadWriteContent : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>throw new InvalidOperationException("A successful update response is not a trusted receipt.");
        protected override bool TryComputeLength(out long length) {length=0;return false;}
        protected override void Dispose(bool disposing) {if (disposing) Disposed=true;base.Dispose(disposing);}
    }
}
