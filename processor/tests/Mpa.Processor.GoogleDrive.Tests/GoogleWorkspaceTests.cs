using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed partial class GoogleWorkspaceTests
{
    private static readonly byte[] Content = "{\"ok\":true}"u8.ToArray();
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Content));
    private static readonly string[] WorkspaceParents = ["workspace"];
    private static string Metadata => JsonSerializer.Serialize(new { id = "catalog-file", version = "7", size = Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        parents = WorkspaceParents, trashed = false, mimeType = "application/json", capabilities = new { canDownload = true } });
    private static string Changed(Action<JsonObject> change)
    { var root = JsonNode.Parse(Metadata)!.AsObject(); change(root); return root.ToJsonString(); }

    [TestMethod]
    public async Task ReadsOnlyCapturedWorkspaceBytesWithExactHashAndTwoScopeChecks()
    {
        using var fixture = new Fixture(); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var result = await connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("account-A", result.AccountId); Assert.AreEqual("catalog-file", result.FileId); Assert.AreEqual("workspace", result.FolderId);
        Assert.AreEqual(7L, result.DriveVersion); CollectionAssert.AreEqual(Content, result.Bytes.ToArray());
        Assert.AreEqual(4, fixture.Calls); Assert.AreEqual(2, fixture.MetadataCalls); Assert.AreEqual(1, fixture.BlobCalls);
    }

    [TestMethod]
    public async Task WrongScopeAndUnsupportedObjectsCannotReachTheBlobDownload()
    {
        (string Body, Failure Error)[] cases = [
            (Changed(root => root["id"] = "substituted-file"), Failure.Invalid),
            (Changed(root => root["parents"] = new JsonArray("outside-workspace")), Failure.Changed),
            (Changed(root => root["parents"] = new JsonArray("workspace", "workspace")), Failure.Invalid),
            (Changed(root => root["parents"] = new JsonArray()), Failure.Changed),
            (Changed(root => root["trashed"] = true), Failure.Changed),
            (Changed(root => root["mimeType"] = "image/jpeg"), Failure.Invalid),
            (Changed(root => root["mimeType"] = "application/vnd.google-apps.shortcut"), Failure.Invalid),
            (Changed(root => root["mimeType"] = "application/vnd.google-apps.document"), Failure.Invalid),
            (Changed(root => root["capabilities"]!["canDownload"] = false), Failure.Denied),
            (Changed(root => root["version"] = "07"), Failure.Invalid),
            (Changed(root => root["version"] = 7), Failure.Invalid),
            (Changed(root => root["size"] = "1"), Failure.Changed)
        ];
        foreach (var item in cases)
        {
            using var fixture = new Fixture { Before = item.Body }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await ExpectFailure(item.Error, () => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.BlobCalls); Assert.AreEqual(1, fixture.MetadataCalls);
        }
    }

    private enum Failure { Invalid, Changed, Denied }
    private static Task ExpectFailure(Failure failure, Func<Task> read) => failure switch
    {
        Failure.Invalid => Assert.ThrowsExactlyAsync<InvalidDataException>(read),
        Failure.Changed => Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(read),
        Failure.Denied => Assert.ThrowsExactlyAsync<DriveWorkspaceDownloadDeniedException>(read),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    [TestMethod]
    public async Task AValidHashCannotHideAMovedReplacedOrNewlyRestrictedFile()
    {
        foreach (var metadata in new[] { Changed(root => root["version"] = "8"), Changed(root => root["parents"] = new JsonArray("outside-workspace")),
            Changed(root => root["trashed"] = true), Changed(root => root["capabilities"]!["canDownload"] = false) })
        {
            using var fixture = new Fixture { After = metadata }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<IOException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1, fixture.BlobCalls); Assert.AreEqual(2, fixture.MetadataCalls);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CorruptTruncatedAndOversizedBytesNeverReturnAWorkspaceObject(bool unknownLength)
    {
        foreach (var bytes in new[] { Content[..^1], Content.Concat(new byte[] { 0 }).ToArray(), Encoding.UTF8.GetBytes("{\"no\":true}") })
        {
            using var fixture = new Fixture { Blob = bytes, UnknownLength = unknownLength }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1, fixture.BlobCalls); Assert.AreEqual(1, fixture.MetadataCalls);
        }
    }

    [TestMethod]
    public async Task CancellationDuringTheBlobReadCannotReturnOrAcceptALateObject()
    {
        using var canceled = new CancellationTokenSource();
        using var fixture = new Fixture { BlobObserved = canceled.Cancel }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest, canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.BlobCalls); Assert.AreEqual(1, fixture.MetadataCalls);
    }

    [TestMethod]
    public async Task InvalidArgumentsCancellationAndNetworkFailuresRemainExplicit()
    {
        using var fixture = new Fixture { BlobStatus = HttpStatusCode.ServiceUnavailable }; await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceObjectAsync("root", "catalog-file", Content.Length, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceObjectAsync("workspace", "workspace", Content.Length, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceObjectAsync("workspace", "invalid/path", Content.Length, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", 0, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", 4 * 1024 * 1024 + 1L, Digest, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest.ToUpperInvariant(), CancellationToken.None));
        Assert.AreEqual(0, fixture.Calls);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest, canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Calls);
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => connection.ReadWorkspaceObjectAsync("workspace", "catalog-file", Content.Length, Digest, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, error.StatusCode); Assert.AreEqual(1, fixture.MetadataCalls);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly TestCredentialVault _vault = new();
        public string Before { get; init; } = Metadata;
        public string? After { get; init; }
        public byte[] Blob { get; init; } = Content;
        public HttpStatusCode BlobStatus { get; init; } = HttpStatusCode.OK;
        public bool UnknownLength { get; init; }
        public Action? BlobObserved { get; init; }
        public int Calls { get; private set; }
        public int MetadataCalls { get; private set; }
        public int BlobCalls { get; private set; }
        public async Task<GoogleAccountConnection> ConnectAsync()
        {
            var id = Guid.NewGuid(); var store = new GoogleCredentialDataStore(_vault, id, () => CancellationToken.None);
            await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
            var path = Path.Combine(Path.GetTempPath(), "mpa-workspace-oauth-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { installed = new { client_id = "synthetic.apps.googleusercontent.com", client_secret = "synthetic-public-client" } }));
                return new(DesktopOAuthConfiguration.Load(path), _vault, id, "account-A", this);
            }
            finally { File.Delete(path); }
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Assert.AreEqual(HttpMethod.Get, request.Method); Assert.AreEqual("www.googleapis.com", request.RequestUri!.Host);
            Assert.AreEqual("synthetic-access", request.Headers.Authorization!.Parameter);
            if (request.RequestUri.AbsolutePath == "/drive/v3/about")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"user":{"permissionId":"account-A","displayName":"Synthetic"}}""", Encoding.UTF8, "application/json") });
            Assert.AreEqual("/drive/v3/files/catalog-file", request.RequestUri.AbsolutePath);
            StringAssert.Contains(request.RequestUri.Query, "supportsAllDrives=true");
            if (request.RequestUri.Query.Contains("alt=media", StringComparison.Ordinal))
            {
                BlobCalls++; BlobObserved?.Invoke();
                HttpContent content = UnknownLength ? new StreamContent(new UnknownLengthStream(Blob)) : new ByteArrayContent(Blob);
                if (UnknownLength) Assert.IsNull(content.Headers.ContentLength);
                return Task.FromResult(new HttpResponseMessage(BlobStatus) { Content = content });
            }
            StringAssert.Contains(Uri.UnescapeDataString(request.RequestUri.Query), "capabilities(canDownload)");
            MetadataCalls++; if (MetadataCalls > 2) throw new InvalidOperationException("Unexpected metadata retry.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MetadataCalls == 1 ? Before : After ?? Before, Encoding.UTF8, "application/json") });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _vault.DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
    }
    private sealed class UnknownLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
    }
}
