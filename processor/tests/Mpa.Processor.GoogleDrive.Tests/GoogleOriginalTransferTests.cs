using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed class GoogleOriginalTransferTests
{
    private static Task Admit(CancellationToken token){token.ThrowIfCancellationRequested();return Task.CompletedTask;}
    [TestMethod]
    public async Task OriginalChunksUseProtectedResumeStateActualRangesAndIndependentProviderProperties()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);var admissions=0;
        Task Gate(CancellationToken token){token.ThrowIfCancellationRequested();admissions++;return Task.CompletedTask;}
        var initial=await connection.PrepareOriginalAsync(fixture.Capture,Gate,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(0L,initial.AcknowledgedBytes);Assert.IsNull(initial.CompletedObject);
        Assert.AreEqual(1,fixture.SessionStarts);Assert.AreEqual(1,fixture.Vault.Records.Keys.Count(k=>k.StartsWith("google-original-v1:",StringComparison.Ordinal)));
        Assert.IsFalse(JsonSerializer.Serialize(initial).Contains("upload_id",StringComparison.Ordinal));
        var first=await connection.UploadOriginalChunkAsync(fixture.Capture,0,fixture.Payload.AsMemory(0,256*1024),Gate,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(256*1024L,first.AcknowledgedBytes);Assert.IsNull(first.CompletedObject);
        var tail=fixture.Payload[(256*1024)..].ToArray();var expected=tail[0];fixture.RenameOnCompletion="human rename.jpg";
        var complete=await connection.UploadOriginalChunkAsync(fixture.Capture,first.AcknowledgedBytes,tail,token=>{tail[0]=(byte)(expected^255);return Gate(token);},CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(expected,tail[0]);Assert.AreEqual(fixture.Capture.Bytes,complete.AcknowledgedBytes);Assert.IsNotNull(complete.CompletedObject);Assert.AreEqual(fixture.Capture.FileId,complete.CompletedObject.FileId);
        Assert.AreEqual(fixture.Capture.Sha256,complete.CompletedObject.ContentSha256);Assert.AreEqual("human rename.jpg",complete.CompletedObject.Name);
        CollectionAssert.AreEqual(fixture.Payload,fixture.Uploaded);Assert.AreEqual(1,fixture.SessionStarts);Assert.AreEqual(2,fixture.ChunkWrites);Assert.AreEqual(fixture.Calls,admissions);
        Assert.AreEqual(0,fixture.Vault.Records.Keys.Count(k=>k.StartsWith("google-original-v1:",StringComparison.Ordinal)));Assert.IsTrue(fixture.Responses.All(c=>c.Disposed));
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostChunkReplyReconcilesActualServerBytesAtTheSameFileAfterConnectionRestart(bool final)
    {
        using var fixture=new Fixture();var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        if(final)await connection.UploadOriginalChunkAsync(fixture.Capture,0,fixture.Payload.AsMemory(0,256*1024),Admit,CancellationToken.None).ConfigureAwait(false);
        fixture.LoseNextReply=true;
        var error=await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>connection.UploadOriginalChunkAsync(fixture.Capture,final?256*1024:0,final?fixture.Payload.AsMemory(256*1024):fixture.Payload.AsMemory(0,256*1024),Admit,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsFalse(error.ToString().Contains("private-upload",StringComparison.Ordinal));await connection.DisposeAsync().ConfigureAwait(false);
        await using var restarted=await fixture.ConnectAsync().ConfigureAwait(false);var progress=await restarted.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(final?fixture.Capture.Bytes:256*1024,progress.AcknowledgedBytes);
        if(!final)progress=await restarted.UploadOriginalChunkAsync(fixture.Capture,progress.AcknowledgedBytes,fixture.Payload.AsMemory(256*1024),Admit,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(progress.CompletedObject);Assert.AreEqual(fixture.Capture.Sha256,progress.CompletedObject.ContentSha256);Assert.AreEqual(1,fixture.SessionStarts);Assert.AreEqual(2,fixture.ChunkWrites);Assert.AreEqual(1,fixture.CreatedIds.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(fixture.Responses.All(c=>c.Disposed));
    }
    [TestMethod]
    public async Task PartialAcknowledgementUsesTheServerRangeRatherThanTheSentChunkLength()
    {
        using var fixture=new Fixture{PartialFirstAck=43};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        var first=await connection.UploadOriginalChunkAsync(fixture.Capture,0,fixture.Payload.AsMemory(0,256*1024),Admit,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(43L,first.AcknowledgedBytes);
        var next=await connection.UploadOriginalChunkAsync(fixture.Capture,43,fixture.Payload.AsMemory(43),Admit,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(next.CompletedObject);CollectionAssert.AreEqual(fixture.Payload,fixture.Uploaded);Assert.AreEqual(2,fixture.ChunkWrites);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExpiredSessionRechecksItsAssignedObjectBeforeCreatingOnlyAnotherSession(bool completedDuringExpiry)
    {
        using var fixture=new Fixture{CompleteDuringExpiry=completedDuringExpiry};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);fixture.ExpireSession=true;
        var progress=await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(completedDuringExpiry?1:2,fixture.SessionStarts);Assert.AreEqual(1,fixture.CreatedIds.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(completedDuringExpiry?fixture.Capture.Bytes:0,progress.AcknowledgedBytes);Assert.AreEqual(completedDuringExpiry,progress.CompletedObject is not null);
    }
    [TestMethod]
    [DataRow("https://evil.example/upload/drive/v3/files?uploadType=resumable&upload_id=x")]
    [DataRow("http://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=x")]
    [DataRow("https://user@www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=x")]
    [DataRow("https://www.googleapis.com:444/upload/drive/v3/files?uploadType=resumable&upload_id=x")]
    [DataRow("https://www.googleapis.com/drive/v3/files?uploadType=resumable&upload_id=x")]
    [DataRow("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=x&upload_id=y")]
    [DataRow("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=x#fragment")]
    public async Task UnsupportedSessionLocationsCannotReceiveCredentialsOrBecomeProtectedResumeState(string location)
    {
        using var fixture=new Fixture{Location=location};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0,fixture.ChunkWrites);Assert.AreEqual(0,fixture.Vault.Records.Keys.Count(k=>k.StartsWith("google-original-v1:",StringComparison.Ordinal)));Assert.IsTrue(fixture.Responses.All(c=>c.Disposed));
    }
    [TestMethod]
    public async Task ProtectedSessionCannotBeReusedForChangedInputDestinationOrAnotherTransferAtTheSameIdentity()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        var calls=fixture.Calls;var secret=fixture.Vault.Records.Single(p=>p.Key.StartsWith("google-original-v1:",StringComparison.Ordinal));var original=secret.Value.ToArray();
        foreach(var changed in new[]{fixture.Capture with{Name="changed.jpg"},fixture.Capture with{Sha256=new string('a',64)},fixture.Capture with{FolderId="different-folder"},fixture.Capture with{TransferId=Guid.NewGuid()}})
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.PrepareOriginalAsync(changed,Admit,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(calls,fixture.Calls);CollectionAssert.AreEqual(original,fixture.Vault.Records[secret.Key]);
    }
    [TestMethod]
    [DataRow("sha")]
    [DataRow("parent")]
    [DataRow("mime")]
    [DataRow("size")]
    [DataRow("version")]
    public async Task ExistingConflictingObjectsAreNeverUpdatedOrAcceptedAsCompletedOriginals(string changed)
    {
        using var fixture=new Fixture{Complete=true,MetadataChange=changed};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(0,fixture.SessionStarts);Assert.AreEqual(0,fixture.ChunkWrites);
    }
    [TestMethod]
    public async Task MissingProviderChecksumRequiresIndependentBoundedContentReadsAtTheSameVersion()
    {
        using var fixture=new Fixture{Complete=true,ReturnChecksum=false};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);var observed=await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(observed.CompletedObject);Assert.IsNull(observed.CompletedObject.ContentSha256);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for(var at=0;at<fixture.Payload.Length;at+=65536)
        {var count=Math.Min(65536,fixture.Payload.Length-at);var chunk=await connection.ReadOriginalRangeAsync(fixture.Capture,observed.CompletedObject.Version,at,count,Admit,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(count,chunk.Length);hash.AppendData(chunk);}
        Assert.AreEqual(fixture.Capture.Sha256,Convert.ToHexStringLower(hash.GetHashAndReset()));Assert.AreEqual(5,fixture.RangeReads);Assert.AreEqual(0,fixture.SessionStarts);Assert.AreEqual(0,fixture.ChunkWrites);Assert.IsTrue(fixture.Responses.All(c=>c.Disposed));
    }
    [TestMethod]
    [DataRow("ignored")]
    [DataRow("wrong-range")]
    [DataRow("version")]
    public async Task IndependentVerificationRejectsIgnoredRangesAndChangedRemoteVersions(string issue)
    {
        using var fixture=new Fixture{Complete=true,ReturnChecksum=false,RangeIssue=issue};await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);var progress=await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>connection.ReadOriginalRangeAsync(fixture.Capture,progress.CompletedObject!.Version,0,65536,Admit,CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(0,fixture.ChunkWrites);Assert.IsTrue(fixture.Responses.All(c=>c.Disposed));
    }
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task ActualOriginalAdmissionRunsAtEveryDriveBoundaryAndCannotLeakIntoOtherOperations(int rejected)
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);var admissions=0;var failure=new InvalidOperationException("Original action is no longer current.");
        var error=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>connection.PrepareOriginalAsync(fixture.Capture,token=>{token.ThrowIfCancellationRequested();if(++admissions==rejected)throw failure;return Task.CompletedTask;},CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(failure,error);Assert.AreEqual(rejected,admissions);Assert.AreEqual(rejected-1,fixture.Calls);await connection.CheckAsync(CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(rejected,admissions);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationAndExitJoinAnUncooperativeChunkAndRetainItsSessionForReconciliation(bool exit)
    {
        using var fixture=new Fixture{BlockChunk=true};var connection=await fixture.ConnectAsync().ConfigureAwait(false);await connection.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);
        using var caller=new CancellationTokenSource();var uploading=connection.UploadOriginalChunkAsync(fixture.Capture,0,fixture.Payload.AsMemory(0,256*1024),Admit,caller.Token);Task? stopping=null;
        try
        {
            await fixture.ChunkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);if(!exit)await caller.CancelAsync().ConfigureAwait(false);stopping=connection.DisposeAsync().AsTask();Assert.IsFalse(uploading.IsCompleted);Assert.IsFalse(stopping.IsCompleted);
        }
        finally {fixture.ChunkRelease.TrySetResult();try{await uploading.ConfigureAwait(false);}catch(OperationCanceledException){ }if(stopping is not null)await stopping.ConfigureAwait(false);}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>uploading).ConfigureAwait(false);fixture.BlockChunk=false;
        await using var retry=await fixture.ConnectAsync().ConfigureAwait(false);var actual=await retry.PrepareOriginalAsync(fixture.Capture,Admit,CancellationToken.None).ConfigureAwait(false);Assert.AreEqual(256*1024L,actual.AcknowledgedBytes);Assert.AreEqual(1,fixture.SessionStarts);Assert.IsTrue(fixture.Responses.All(c=>c.Disposed));
    }
    [TestMethod]
    public async Task InvalidBindingsAndChunksFailBeforeNetworkOrUnboundedBufferAllocation()
    {
        using var fixture=new Fixture();await using var connection=await fixture.ConnectAsync().ConfigureAwait(false);
        foreach(var changed in new[]{fixture.Capture with{AccountId="different"},fixture.Capture with{FolderId="root"},fixture.Capture with{FileId="root"},fixture.Capture with{Bytes=0},fixture.Capture with{MimeType="application/vnd.google-apps.document"},fixture.Capture with{Name="../a.jpg"}})
            Assert.ThrowsExactly<ArgumentException>(()=>connection.PrepareOriginalAsync(changed,Admit,CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(()=>connection.UploadOriginalChunkAsync(fixture.Capture,0,new byte[1],Admit,CancellationToken.None));Assert.ThrowsExactly<ArgumentException>(()=>connection.UploadOriginalChunkAsync(fixture.Capture,0,new byte[GoogleAccountConnection.MaximumOriginalChunkBytes+1],Admit,CancellationToken.None));
        using var canceled=new CancellationTokenSource();canceled.Cancel();Assert.ThrowsExactly<OperationCanceledException>(()=>connection.PrepareOriginalAsync(fixture.Capture,Admit,canceled.Token));Assert.AreEqual(0,fixture.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly TestCredentialVault Vault=new();private readonly Guid _connection=Guid.NewGuid();
        internal byte[] Payload {get;}=Enumerable.Range(0,256*1024+13).Select(i=>(byte)(i%251)).ToArray();
        internal byte[] Uploaded {get;}
        internal DriveOriginalUpload Capture {get;}
        internal List<string> CreatedIds {get;}=[];
        internal List<TrackedContent> Responses {get;}=[];
        internal int Calls {get;private set;}
        internal int SessionStarts {get;private set;}
        internal int ChunkWrites {get;private set;}
        internal int RangeReads {get;private set;}
        internal bool Complete {get;set;}
        internal bool ReturnChecksum {get;init;}=true;
        internal string? MetadataChange {get;init;}
        internal string? RangeIssue {get;init;}
        internal string? Location {get;init;}
        internal bool ExpireSession {get;set;}
        internal bool CompleteDuringExpiry {get;init;}
        internal bool LoseNextReply {get;set;}
        internal int PartialFirstAck {get;init;}
        internal string? RenameOnCompletion {get;set;}
        internal bool BlockChunk {get;set;}
        internal TaskCompletionSource ChunkEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ChunkRelease {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _received,_metadataReads;private long _version=9;
        internal Fixture(){Uploaded=new byte[Payload.Length];Capture=new(Guid.NewGuid(),"account-A","originals","original-file","original.jpg","image/jpeg",Payload.Length,Convert.ToHexStringLower(SHA256.HashData(Payload)));}
        internal async Task<GoogleAccountConnection> ConnectAsync()
        {
            var store=new GoogleCredentialDataStore(Vault,_connection,()=>CancellationToken.None);if(!Vault.Records.ContainsKey("google-oauth-v1:"+store.UserId))await store.StoreAsync(store.UserId,GoogleCredentialTests.Grant()).ConfigureAwait(false);
            var path=Path.Combine(Path.GetTempPath(),"mpa-original-oauth-"+Guid.NewGuid().ToString("N")+".json");
            try {File.WriteAllText(path,"{\"installed\":{\"client_id\":\"synthetic.apps.googleusercontent.com\",\"client_secret\":\"synthetic-public-client\"}}");return new(DesktopOAuthConfiguration.Load(path),Vault,_connection,"account-A",new Handler(this));}
            finally {File.Delete(path);}
        }
        private HttpResponseMessage Response(HttpStatusCode status,byte[] body){var content=new TrackedContent(body);Responses.Add(content);return new(status){Content=content};}
        private HttpResponseMessage Json(HttpStatusCode status,string body)=>Response(status,Encoding.UTF8.GetBytes(body));
        private HttpResponseMessage Progress()
        {var response=Response((HttpStatusCode)308,[]);if(_received>0)response.Headers.TryAddWithoutValidation("Range","bytes=0-"+(_received-1).ToString(CultureInfo.InvariantCulture));return response;}
        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls++;Assert.AreEqual("www.googleapis.com",request.RequestUri!.Host);Assert.AreEqual("https",request.RequestUri.Scheme);Assert.AreEqual("synthetic-access",request.Headers.Authorization!.Parameter);
            var path=request.RequestUri.AbsolutePath;
            if(path=="/drive/v3/about")return Json(HttpStatusCode.OK,"{\"user\":{\"permissionId\":\"account-A\",\"displayName\":\"Synthetic\"}}");
            if(path=="/drive/v3/files/originals")return Json(HttpStatusCode.OK,"{\"id\":\"originals\",\"mimeType\":\"application/vnd.google-apps.folder\",\"trashed\":false,\"capabilities\":{\"canAddChildren\":true}}");
            if(path=="/drive/v3/files/original-file")
            {
                if(!Complete)return Response(HttpStatusCode.NotFound,[]);
                if(request.RequestUri.Query.Contains("alt=media",StringComparison.Ordinal))
                {
                    RangeReads++;var item=request.Headers.Range!.Ranges.Single();var offset=checked((int)item.From!.Value);var count=checked((int)(item.To!.Value-offset+1));var response=Response(RangeIssue=="ignored"?HttpStatusCode.OK:HttpStatusCode.PartialContent,Payload.AsSpan(offset,count).ToArray());
                    response.Content.Headers.ContentRange=new(RangeIssue=="wrong-range"?offset+1:offset,offset+count-1,Payload.Length);if(RangeIssue=="version")_version++;return response;
                }
                _metadataReads++;var json=new JsonObject{["id"]=Capture.FileId,["name"]=RenameOnCompletion??Capture.Name,["mimeType"]=Capture.MimeType,["version"]=_version.ToString(CultureInfo.InvariantCulture),["size"]=Payload.Length.ToString(CultureInfo.InvariantCulture),["parents"]=new JsonArray(Capture.FolderId),["trashed"]=false,["capabilities"]=new JsonObject{["canDownload"]=true}};
                if(ReturnChecksum)json["sha256Checksum"]=MetadataChange=="sha"?new string('0',64):Capture.Sha256;
                if(MetadataChange=="parent")json["parents"]=new JsonArray("other-parent");if(MetadataChange=="mime")json["mimeType"]="application/vnd.google-apps.document";if(MetadataChange=="size")json["size"]="1";if(MetadataChange=="version"&&_metadataReads%2==0)json["version"]="10";
                return Json(HttpStatusCode.OK,json.ToJsonString());
            }
            Assert.AreEqual("/upload/drive/v3/files",path);StringAssert.Contains(request.RequestUri.Query,"uploadType=resumable");
            if(request.Method==HttpMethod.Post)
            {
                using var metadata=JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false));Assert.AreEqual(Capture.FileId,metadata.RootElement.GetProperty("id").GetString());Assert.AreEqual(Capture.FolderId,metadata.RootElement.GetProperty("parents")[0].GetString());
                Assert.AreEqual(Capture.Bytes.ToString(CultureInfo.InvariantCulture),request.Headers.GetValues("X-Upload-Content-Length").Single());CreatedIds.Add(metadata.RootElement.GetProperty("id").GetString()!);SessionStarts++;_received=0;
                var response=Response(HttpStatusCode.OK,[]);response.Headers.Location=new Uri(Location??"https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=private-upload-"+SessionStarts.ToString(CultureInfo.InvariantCulture));return response;
            }
            Assert.AreEqual(HttpMethod.Put,request.Method);var range=request.Content!.Headers.ContentRange!;Assert.AreEqual(Capture.Bytes,range.Length!.Value);
            if(range.From is null)
            {
                if(ExpireSession){ExpireSession=false;if(CompleteDuringExpiry){Complete=true;Payload.CopyTo(Uploaded,0);}return Response(HttpStatusCode.NotFound,[]);}return Complete?Response(HttpStatusCode.OK,[]):Progress();
            }
            Assert.AreEqual((long)_received,range.From.Value);var chunk=await request.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);Assert.IsTrue(chunk.Length<=GoogleAccountConnection.MaximumOriginalChunkBytes);ChunkWrites++;ChunkEntered.TrySetResult();if(BlockChunk)await ChunkRelease.Task.ConfigureAwait(false);
            var accepted=ChunkWrites==1&&PartialFirstAck>0?PartialFirstAck:chunk.Length;chunk.AsSpan(0,accepted).CopyTo(Uploaded.AsSpan(_received));_received+=accepted;Complete=_received==Payload.Length;
            if(LoseNextReply){LoseNextReply=false;throw new HttpRequestException("lost response at "+request.RequestUri);}
            return Complete?Response(HttpStatusCode.Created,[]):Progress();
        }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>fixture.SendAsync(request,cancellationToken);}
        public void Dispose(){ChunkRelease.TrySetResult();Vault.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {internal bool Disposed {get;private set;}protected override void Dispose(bool disposing){if(disposing)Disposed=true;base.Dispose(disposing);}}
}
