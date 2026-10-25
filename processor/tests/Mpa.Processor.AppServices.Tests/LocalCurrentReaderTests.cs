using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task LocalCurrentReaderCheckpointsActualHttpMetadataAndBytesBeforeRootInterpretationAndFencesLast()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        fixture.Http.BeforeBody=async()=>
        {
            var actual=await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false);
            Assert.AreEqual("reading",actual!.Phase);Assert.AreEqual(7L,actual.Version);Assert.IsNull(actual.RootSequence);
        };
        fixture.AfterTransport=async()=>
        {
            var actual=await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false);
            Assert.AreEqual("read",actual!.Phase);Assert.AreEqual(CurrentHash(fixture.Data.Cloud["captured-3"].Bytes.ToArray()),actual.ContentSha256);
        };
        var captured=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3L,captured.Head.Sequence);Assert.AreEqual("captured",captured.Attempt.Phase);Assert.AreEqual(2,fixture.Http.MetadataReads);Assert.AreEqual(1,fixture.Http.BodyReads);
        fixture.Http.BeforeBody=null;fixture.AfterTransport=null;
        var fenced=await fixture.Reader.FenceAsync(captured,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("fenced",fenced.Attempt.Phase);Assert.AreEqual(4,fixture.Http.MetadataReads);Assert.AreEqual(2,fixture.Http.BodyReads);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.FenceAsync(captured,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(4,fixture.Http.MetadataReads);Assert.AreEqual(3,fixture.Data.Creates);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    public async Task LocalCurrentReaderActualMalformedBodyRetainsItsHashAndCannotBeRewrittenUnderTheSameVersion()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);var original=fixture.Data.Cloud["captured-3"];
        var bad=original.Bytes.ToArray();bad[0]=0;fixture.Data.Cloud["captured-3"]=original with {DriveVersion=8,Bytes=bad};
        await Assert.ThrowsExactlyAsync<ProtocolFormatException>(()=>fixture.Reader.CaptureAsync(CancellationToken.None)).ConfigureAwait(false);
        var retained=await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false);Assert.AreEqual("read",retained!.Phase);Assert.AreEqual(CurrentHash(bad),retained.ContentSha256);
        fixture.Data.Cloud["captured-3"]=original with {DriveVersion=8};
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.CaptureAsync(CancellationToken.None)).ConfigureAwait(false);
        fixture.Data.Cloud["captured-3"]=original with {DriveVersion=9};
        Assert.AreEqual(3L,(await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false)).Head.Sequence);
    }

    [TestMethod]
    public async Task LocalCurrentReaderChangedFinalMetadataBlocksBeforeAnotherBodyAndRetainsTheActualHighWater()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var captured=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Data.Cloud["captured-3"]=fixture.Data.Cloud["captured-3"] with {DriveVersion=8};
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.FenceAsync(captured,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(3,fixture.Http.MetadataReads);Assert.AreEqual(1,fixture.Http.BodyReads);
        var blocked=await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false);Assert.AreEqual("blocked",blocked!.Phase);Assert.AreEqual(8L,blocked.MaxVersion);
        Assert.AreEqual(8L,(await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false)).Attempt.Version);
    }

    [TestMethod]
    public async Task LocalCurrentReaderRejectsForeignLiveOwnersAndSupersededNoncesBeforeActualIo()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var first=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>other.FenceAsync(first,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);
        _=await other.CaptureAsync(CancellationToken.None).ConfigureAwait(false);reads=fixture.Http.MetadataReads;
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.FenceAsync(first,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(reads,fixture.Http.MetadataReads);
    }

    [TestMethod]
    public async Task LocalCurrentReaderDisconnectDuringActualBodyCannotAdmitTheOldAccess()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        fixture.Http.BeforeBody=async()=>
        {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{new DriveAccountRepository(database).Disconnect("permission-A",1);return true;},CancellationToken.None).ConfigureAwait(false);};
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(()=>fixture.Reader.CaptureAsync(CancellationToken.None)).ConfigureAwait(false);
        var saved=await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false);Assert.AreEqual("reading",saved!.Phase);Assert.IsNull(saved.RootSequence);
        Assert.AreEqual(3,fixture.Data.Creates);Assert.AreEqual(3,fixture.Data.Allocations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalCurrentReaderCancellationOrCloseJoinsUncooperativeActualBodyAndRetainsBorrowers(bool exit)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);using var caller=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeBody=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};
        var reading=fixture.Reader.CaptureAsync(caller.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);var stopping=Task.CompletedTask;
        if(exit) stopping=fixture.Reader.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
        try {Assert.IsFalse(reading.IsCompleted);if(exit) Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(fixture.Http.Disposed);Assert.AreEqual(0,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>reading).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsFalse(fixture.Http.Disposed);Assert.AreEqual("reading",(await CurrentStateAsync(fixture.Data,store=>store.Current()).ConfigureAwait(false))!.Phase);
        if(exit)
        {
            Assert.IsTrue(fixture.Reader.ExitConfirmed);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(()=>fixture.Reader.CaptureAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        else
        {
            fixture.Http.BeforeBody=null;Assert.AreEqual(3L,(await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false)).Head.Sequence);
        }
    }

    [TestMethod]
    public async Task LocalCurrentReaderHasNoPublisherRequirementAndKeepsActualSharingCorpusBoundToItsApproval()
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync().ConfigureAwait(false);
        var signer=new JournaledProtocolSigner(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var revoked=await signer.SignControllerAsync(Guid.NewGuid(),"library-publisher-authorization",(identity,sequence)=>new CanonicalJson().Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format="mpa",major=1,minor=0,libraryId=identity.LibraryId.ToString("D"),actorId=identity.ControllerId.ToString("D"),epoch=identity.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            sequence=sequence.ToString(CultureInfo.InvariantCulture),kind="library-publisher-authorization",requiredCapabilities=new[]{CatalogPublisherAuthorization.Capability},
            payload=new {workspace=new {accountId="permission-A",folderId="metadata"},publisher=(object?)null}
        })),CancellationToken.None).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibraryAuthorityRepository(database,fixture.Data.Budget).SelectPublisherPolicy(revoked.Envelope!.Value.Span),CancellationToken.None).ConfigureAwait(false);
        var next=await SignNextCurrentObservationAsync(fixture.Data,new(new("permission-A","revoked-policy","metadata",revoked.Envelope!.Value.Length,revoked.EnvelopeSha256!),revoked.Sequence)).ConfigureAwait(false);
        fixture.Data.Cloud["captured-3"]=fixture.Data.Cloud["captured-3"] with {DriveVersion=8,Bytes=next.Envelope!.Value.ToArray()};
        Assert.AreEqual(5L,(await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false)).Head.Sequence);
        var reads=fixture.Http.MetadataReads;fixture.Data.Setup.Accounts.Readers.FolderRead=(folder,_)=>Task.FromResult(new DriveFolder(folder,"Changed",true,["parent"],"other-corpus"));
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>fixture.Reader.CaptureAsync(CancellationToken.None)).ConfigureAwait(false);Assert.AreEqual(reads,fixture.Http.MetadataReads);
    }

    private sealed partial class LibraryFactory
    {
        public Func<string,string,IGoogleWorkspaceConditionalObserver,CancellationToken,Task<DriveWorkspaceConditionalJson>>? ConditionalObserve {get;set;}
        public Func<string,string,long,string,IGoogleWorkspaceObjectObserver,CancellationToken,Task<DriveWorkspaceObject>>? ObjectObserve {get;set;}
        private sealed partial class Connection : IGoogleWorkspaceConditionalObservation
        {
            public Task<DriveWorkspaceConditionalJson> ObserveConditionalWorkspaceJsonAsync(string folder,string file,IGoogleWorkspaceConditionalObserver observer,CancellationToken token)=>
                owner.ConditionalObserve?.Invoke(folder,file,observer,token) ?? throw new InvalidOperationException("Unexpected conditional current observation.");
        }
        private sealed partial class Connection : IGoogleWorkspaceObservedObjectReader
        {
            public Task<DriveWorkspaceObject> ReadObservedWorkspaceObjectAsync(string folder,string file,long bytes,string hash,IGoogleWorkspaceObjectObserver observer,CancellationToken token)=>
                owner.ObjectObserve?.Invoke(folder,file,bytes,hash,observer,token) ?? throw new InvalidOperationException("Unexpected immutable graph observation.");
        }
    }

    private sealed class CurrentReaderFixture : IAsyncDisposable
    {
        public InitialPublicationFixture Data {get;}
        public CurrentReadHttp Http {get;}
        public LocalCurrentControlReader Reader {get;}
        public Func<Task>? AfterTransport {get;set;}
        private readonly GoogleAccountConnection _connection;
        private readonly SetupSigningVault _vault;
        private CurrentReaderFixture(InitialPublicationFixture data,GoogleAccountConnection connection,CurrentReadHttp http,SetupSigningVault vault)
        {
            Data=data;_connection=connection;Http=http;_vault=vault;
            Reader=new(data.Setup.Accounts.State,data.Setup.Accounts.Service,data.Setup.Keys,data.Budget);
            data.Setup.Accounts.Readers.ConditionalObserve=async(folder,file,observer,token)=>
            {var result=await connection.ObserveConditionalWorkspaceJsonAsync(folder,file,observer,token).ConfigureAwait(false);if(AfterTransport is not null) await AfterTransport().ConfigureAwait(false);return result;};
            data.Setup.Accounts.Readers.ObjectObserve=(folder,file,bytes,hash,observer,token)=>connection.ReadObservedWorkspaceObjectAsync(folder,file,bytes,hash,observer,token);
        }
        public static async Task<CurrentReaderFixture> CreateAsync(bool addSource=false)
        {
            var data=await ObservedCurrentFixtureAsync(addSource).ConfigureAwait(false);var vault=new SetupSigningVault();var http=new CurrentReadHttp(data);
            GoogleAccountConnection? connection=null;
            var path=Path.Combine(Path.GetTempPath(),"mpa-current-reader-client-"+Guid.NewGuid().ToString("N")+".json");
            try
            {
                var id=Guid.NewGuid();var store=new GoogleCredentialDataStore(vault,id,()=>CancellationToken.None);
                await store.StoreAsync(store.UserId,new TokenResponse {AccessToken="synthetic-access",RefreshToken="synthetic-refresh",Scope=GoogleCredentialDataStore.DriveScope,
                    TokenType="Bearer",ExpiresInSeconds=3600,IssuedUtc=DateTime.UtcNow}).ConfigureAwait(false);
                File.WriteAllText(path,JsonSerializer.Serialize(new {installed=new {client_id="synthetic.apps.googleusercontent.com",client_secret="synthetic-public-client"}}));
                connection=new(DesktopOAuthConfiguration.Load(path),vault,id,"permission-A",http);return new(data,connection,http,vault);
            }
            catch {await data.DisposeAsync().ConfigureAwait(false);if(connection is not null) await connection.DisposeAsync().ConfigureAwait(false);http.Dispose();await vault.DisposeAsync().ConfigureAwait(false);throw;}
            finally {File.Delete(path);}
        }
        public async ValueTask DisposeAsync()
        {await Reader.DisposeAsync().ConfigureAwait(false);await Data.DisposeAsync().ConfigureAwait(false);await _connection.DisposeAsync().ConfigureAwait(false);await _vault.DisposeAsync().ConfigureAwait(false);}
    }
    private sealed class CurrentReadHttp(InitialPublicationFixture data) : HttpMessageHandler
    {
        public Func<Task>? BeforeBody {get;set;}
        public Func<string,Task>? BeforeFileBody {get;set;}
        public Func<string,string,string>? MetadataJson {get;set;}
        public List<string> BodyFiles {get;}=[];
        public HashSet<string> MissingFiles {get;}=new(StringComparer.Ordinal);
        public int MetadataReads {get;private set;}
        public int BodyReads {get;private set;}
        public bool Disposed {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Assert.AreEqual(HttpMethod.Get,request.Method);Assert.AreEqual("www.googleapis.com",request.RequestUri!.Host);
            Assert.AreEqual("https",request.RequestUri.Scheme);Assert.AreEqual("synthetic-access",request.Headers.Authorization!.Parameter);
            if(request.RequestUri.AbsolutePath=="/drive/v3/about") return Json("""{"user":{"permissionId":"permission-A","displayName":"Synthetic"}}""");
            StringAssert.Contains(request.RequestUri.Query,"supportsAllDrives=true");
            var file=Uri.UnescapeDataString(request.RequestUri.AbsolutePath[(request.RequestUri.AbsolutePath.LastIndexOf('/')+1)..]);
            if(MissingFiles.Contains(file)) return Json("{}",HttpStatusCode.NotFound);
            Assert.IsTrue(data.Cloud.ContainsKey(file),"Only exact owned fixture identities may be read.");
            if(request.RequestUri.AbsolutePath=="/drive/v3/files/"+file && request.RequestUri.Query.Contains("alt=media",StringComparison.Ordinal))
            {
                StringAssert.Contains(request.RequestUri.Query,"alt=media");BodyReads++;if(BeforeBody is not null) await BeforeBody().ConfigureAwait(false);
                BodyFiles.Add(file);if(BeforeFileBody is not null) await BeforeFileBody(file).ConfigureAwait(false);
                return new(HttpStatusCode.OK) {Content=new ByteArrayContent(data.Cloud[file].Bytes.ToArray())};
            }
            MetadataReads++;var actual=data.Cloud[file];
            if(request.RequestUri.AbsolutePath=="/drive/v3/files/"+file)
            {
                var metadata=JsonSerializer.Serialize(new {id=actual.FileId,version=actual.DriveVersion.ToString(CultureInfo.InvariantCulture),size=actual.Bytes.Length.ToString(CultureInfo.InvariantCulture),
                    mimeType=data.MetadataMimeTypes.GetValueOrDefault(file,actual.MimeType),parents=new[]{actual.FolderId},trashed=false,capabilities=new {canDownload=true}});
                return Json(MetadataJson?.Invoke(file,metadata) ?? metadata);
            }
            Assert.AreEqual("/drive/v2/files/"+file,request.RequestUri.AbsolutePath);
            var raw=JsonSerializer.Serialize(new {id=actual.FileId,version=actual.DriveVersion.ToString(CultureInfo.InvariantCulture),etag="\"actual-"+actual.DriveVersion.ToString(CultureInfo.InvariantCulture)+"\"",
                fileSize=actual.Bytes.Length.ToString(CultureInfo.InvariantCulture),mimeType="application/json",parents=new[]{new {id=actual.FolderId}},labels=new {trashed=false},capabilities=new {canDownload=true,canEdit=true}});
            return Json(MetadataJson?.Invoke(file,raw) ?? raw);
        }
        private static HttpResponseMessage Json(string body,HttpStatusCode status=HttpStatusCode.OK)=>new(status) {Content=new StringContent(body,Encoding.UTF8,"application/json")};
        protected override void Dispose(bool disposing) {if(disposing) Disposed=true;base.Dispose(disposing);}
    }
}
