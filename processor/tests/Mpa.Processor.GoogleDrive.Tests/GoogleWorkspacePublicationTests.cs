using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed partial class GoogleWorkspacePublicationTests
{
    private static readonly byte[] Content = "{\"ok\":true}"u8.ToArray();
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Content));
    private static readonly string[] Parents = ["workspace"];
    private static string Metadata => JsonSerializer.Serialize(new { id = "allocated-file", version = "7", size = Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        parents = Parents, trashed = false, mimeType = "application/json", capabilities = new { canDownload = true } });
    private static string Changed(Action<JsonObject> change) { var root = JsonNode.Parse(Metadata)!.AsObject(); change(root); return root.ToJsonString(); }
    private static Task<DriveWorkspaceObject> Create(GoogleAccountConnection connection, CancellationToken token = default) =>
        connection.CreateWorkspaceObjectAsync("workspace", "allocated-file", "application/json", Content, Digest, token);

    [TestMethod]
    public async Task AllocationIsSeparateFromCreationSoTheOwnerCanJournalItsIdentityFirst()
    {
        using var fixture = new Fixture(); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.IsInstanceOfType<IGoogleWorkspacePublication>(connection);
        Assert.AreEqual("allocated-file", await connection.AllocateWorkspaceObjectIdAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(1, fixture.Allocations); Assert.AreEqual(0, fixture.Creates); Assert.AreEqual(0, fixture.RootReads);
    }

    [TestMethod]
    public async Task CreateOwnsCapturedBytesAndVerifiesActualStoredScopeLengthHashAndRevision()
    {
        var input = Content.ToArray(); using var fixture = new Fixture { RootObserved = () => input[0] = 0 };
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var result = await connection.CreateWorkspaceObjectAsync("workspace", "allocated-file", "application/json", input, Digest, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(Content, result.Bytes.ToArray()); Assert.AreEqual(0, input[0]);
        Assert.AreEqual("account-A", result.AccountId); Assert.AreEqual("allocated-file", result.FileId); Assert.AreEqual(7L, result.DriveVersion);
        Assert.AreEqual(1, fixture.Creates); Assert.AreEqual(2, fixture.RootReads); Assert.AreEqual(2, fixture.MetadataReads); Assert.AreEqual(1, fixture.BlobReads);
        Assert.AreEqual(0, fixture.Allocations); Assert.IsTrue(fixture.Responses.All(content => content.Disposed));
    }

    [TestMethod]
    public async Task ConflictVerifiesTheExistingObjectWithoutUpdatingItOrAllocatingAnotherIdentity()
    {
        using var fixture = new Fixture { CreateStatus = HttpStatusCode.Conflict }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.AreEqual("allocated-file", (await Create(connection).ConfigureAwait(false)).FileId);
        Assert.AreEqual(1, fixture.Creates); Assert.AreEqual(0, fixture.Allocations); Assert.AreEqual(1, fixture.BlobReads);
    }

    [TestMethod]
    public async Task ServerFailureAndLostReplyResumeAtTheSameCapturedIdentity()
    {
        foreach (var loseReply in new[] { false, true })
        {
            using var fixture = new Fixture { FailFirstCreate = true, LoseReply = loseReply, CreateStatus = HttpStatusCode.Conflict };
            await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Create(connection)).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.MetadataReads);
            Assert.AreEqual("allocated-file", (await Create(connection).ConfigureAwait(false)).FileId);
            Assert.AreEqual(2, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
        }
    }

    [TestMethod]
    public async Task ConflictCannotAcceptAnOriginalWrongMimeMovedChangedOrCorruptObject()
    {
        var cases = new (string Before, string? After, byte[] Blob, bool Invalid)[] {
            (Changed(root => root["mimeType"] = "image/jpeg"), null, Content, true),
            (Changed(root => root["mimeType"] = "application/octet-stream"), null, Content, false),
            (Changed(root => root["parents"] = new JsonArray("outside")), null, Content, false),
            (Metadata, Changed(root => root["version"] = "8"), Content, false),
            (Metadata, null, "{\"ok\":fals}"u8.ToArray(), true)
        };
        foreach (var item in cases)
        {
            using var fixture = new Fixture { CreateStatus = HttpStatusCode.Conflict, Before = item.Before, After = item.After, Blob = item.Blob };
            await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            if (item.Invalid) await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Create(connection)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(() => Create(connection)).ConfigureAwait(false);
            Assert.AreEqual(1, fixture.Creates); Assert.AreEqual(0, fixture.Allocations);
        }
    }

    [TestMethod]
    public async Task RootPermissionAndLateWorkspaceChangesNeverProduceAVerifiedCompletion()
    {
        foreach (var late in new[] { false, true })
        {
            using var fixture = new Fixture { DenyRootAfter = late ? 1 : 0 }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Create(connection)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode); Assert.AreEqual(late ? 1 : 0, fixture.Creates);
        }
    }

    [TestMethod]
    public async Task InvalidInputsAndCanceledRequestsDoNotReachGoogle()
    {
        using var fixture = new Fixture(); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => connection.CreateWorkspaceObjectAsync("root", "allocated-file", "application/json", Content, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.CreateWorkspaceObjectAsync("workspace", "workspace", "application/json", Content, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.CreateWorkspaceObjectAsync("workspace", "allocated-file", "image/jpeg", Content, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.CreateWorkspaceObjectAsync("workspace", "allocated-file", "application/json", new byte[GoogleAccountConnection.MaximumWorkspaceObjectBytes + 1], Digest, CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() => connection.CreateWorkspaceObjectAsync("workspace", "allocated-file", "application/json", Content, new string('0', 64), CancellationToken.None));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Create(connection, canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Calls);
    }

    [TestMethod]
    public async Task MalformedGeneratedIdentitiesAreNotCapturedAsSuccessfulAllocations()
    {
        foreach (var body in new[] { "{}", "{\"space\":\"drive\",\"ids\":[]}", "{\"space\":\"drive\",\"ids\":[\"a\",\"b\"]}",
            "{\"space\":\"appDataFolder\",\"ids\":[\"allocated-file\"]}", "{\"space\":\"drive\",\"ids\":[\"root\"]}" })
        {
            using var fixture = new Fixture { AllocationBody = body }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.AllocateWorkspaceObjectIdAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.Creates);
        }
    }

    [TestMethod]
    public async Task CancellationAndDisposalJoinTheActualCreateResponseBeforeReleasingTheConnection()
    {
        using var fixture = new Fixture { BlockCreate = true }; var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        using var canceled = new CancellationTokenSource();
        var create = Create(connection, canceled.Token);
        await fixture.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); canceled.Cancel();
        var dispose = connection.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(50).ConfigureAwait(false); Assert.IsFalse(create.IsCompleted); Assert.IsFalse(dispose.IsCompleted);
        }
        finally { fixture.CreateRelease.TrySetResult(true); }
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await create.ConfigureAwait(false)).ConfigureAwait(false);
        await dispose.ConfigureAwait(false); Assert.IsTrue(fixture.Responses.All(content => content.Disposed)); Assert.AreEqual(0, fixture.MetadataReads);
    }

    private sealed partial class Fixture : HttpMessageHandler
    {
        private readonly TestCredentialVault _vault = new();
        public string AllocationBody { get; init; } = "{\"space\":\"drive\",\"ids\":[\"allocated-file\"]}";
        public string Before { get; init; } = Metadata;
        public string? After { get; init; }
        public byte[] Blob { get; init; } = Content;
        public HttpStatusCode CreateStatus { get; init; } = HttpStatusCode.OK;
        public int DenyRootAfter { get; init; } = int.MaxValue;
        public bool FailFirstCreate { get; init; }
        public bool LoseReply { get; init; }
        public bool BlockCreate { get; init; }
        public Action? RootObserved { get; init; }
        public int Calls { get; private set; }
        public int Allocations { get; private set; }
        public int Creates { get; private set; }
        public int RootReads { get; private set; }
        public int MetadataReads { get; private set; }
        public int BlobReads { get; private set; }
        public List<TrackedContent> Responses { get; } = [];
        public TaskCompletionSource<bool> CreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CreateRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GoogleAccountConnection> ConnectAsync()
        {
            var id = Guid.NewGuid(); var store = new GoogleCredentialDataStore(_vault, id, () => CancellationToken.None);
            await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
            var path = Path.Combine(Path.GetTempPath(), "mpa-publication-oauth-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"installed\":{\"client_id\":\"synthetic.apps.googleusercontent.com\",\"client_secret\":\"synthetic-public-client\"}}");
                return new(DesktopOAuthConfiguration.Load(path), _vault, id, "account-A", this);
            }
            finally { File.Delete(path); }
        }
        private HttpResponseMessage Response(HttpStatusCode status, string body)
        {
            var content = new TrackedContent(body); Responses.Add(content); return new(status) { Content = content };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Assert.AreEqual("www.googleapis.com", request.RequestUri!.Host); Assert.AreEqual("synthetic-access", request.Headers.Authorization!.Parameter);
            if (request.RequestUri.AbsolutePath == "/drive/v3/about") return Response(HttpStatusCode.OK, "{\"user\":{\"permissionId\":\"account-A\",\"displayName\":\"Synthetic\"}}");
            if (request.Method == HttpMethod.Post)
            {
                Assert.AreEqual("/upload/drive/v3/files", request.RequestUri.AbsolutePath); StringAssert.Contains(request.RequestUri.Query, "uploadType=multipart");
                var content = (MultipartContent)request.Content!; Assert.AreEqual("multipart/related", content.Headers.ContentType!.MediaType);
                var parts = content.ToArray(); Assert.AreEqual(2, parts.Length);
                using var metadata = JsonDocument.Parse(await parts[0].ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                Assert.AreEqual("allocated-file", metadata.RootElement.GetProperty("id").GetString());
                Assert.AreEqual("workspace", metadata.RootElement.GetProperty("parents")[0].GetString());
                Assert.AreEqual(Digest, metadata.RootElement.GetProperty("properties").GetProperty("mpaSha256").GetString());
                Assert.AreEqual("catalog-object-v1", metadata.RootElement.GetProperty("properties").GetProperty("mpaRole").GetString());
                Assert.IsFalse(metadata.RootElement.TryGetProperty("appProperties", out _));
                CollectionAssert.AreEqual(Content, await parts[1].ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                Creates++; CreateEntered.TrySetResult(true); if (BlockCreate) await CreateRelease.Task.ConfigureAwait(false);
                if (FailFirstCreate && Creates == 1)
                {
                    if (LoseReply) throw new HttpRequestException("Injected lost create reply after storage.");
                    return Response(HttpStatusCode.ServiceUnavailable, "{}");
                }
                return Response(CreateStatus, "{\"id\":\"allocated-file\"}");
            }
            Assert.AreEqual(HttpMethod.Get, request.Method);
            if (request.RequestUri.AbsolutePath == "/drive/v3/files")
            {
                DocumentQueries.Add(Uri.UnescapeDataString(request.RequestUri.Query)); DocumentsEntered.TrySetResult();
                if (BlockDocuments) await DocumentsRelease.Task.ConfigureAwait(false);
                return Response(HttpStatusCode.OK, DocumentsBody);
            }
            if (request.RequestUri.AbsolutePath == "/drive/v3/files/generateIds")
            { Allocations++; StringAssert.Contains(request.RequestUri.Query, "count=1&space=drive&type=files"); return Response(HttpStatusCode.OK, AllocationBody); }
            StringAssert.Contains(request.RequestUri.Query, "supportsAllDrives=true");
            if (request.RequestUri.AbsolutePath == "/drive/v3/files/workspace")
            {
                RootReads++; RootObserved?.Invoke();
                return Response(HttpStatusCode.OK, "{\"id\":\"workspace\",\"mimeType\":\"application/vnd.google-apps.folder\",\"trashed\":false,\"capabilities\":{\"canAddChildren\":" + (RootReads <= DenyRootAfter ? "true" : "false") + "}}");
            }
            Assert.AreEqual("/drive/v3/files/allocated-file", request.RequestUri.AbsolutePath);
            if (request.RequestUri.Query.Contains("alt=media", StringComparison.Ordinal))
            { BlobReads++; return Response(HttpStatusCode.OK, Encoding.UTF8.GetString(Blob)); }
            MetadataReads++; return Response(HttpStatusCode.OK, MetadataReads == 1 ? Before : After ?? Before);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) _vault.DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
    }
    private sealed class TrackedContent(string body) : StringContent(body, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
