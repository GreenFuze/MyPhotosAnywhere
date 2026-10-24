using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed class GoogleMediaTests
{
    [TestMethod]
    public async Task BoundedReadOnlyMediaPageKeepsIdsTimestampsAndPartialStatusWithoutThumbnailUrls()
    {
        await using var vault = new TestCredentialVault(); var id = Guid.NewGuid();
        await GrantAsync(vault, id).ConfigureAwait(false);
        using var handler = new Handler((request, count) => count == 1 ? Identity() : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"nextPageToken":"next-page","incompleteSearch":true,"files":[{"id":"file-A","name":"sample.jpg","mimeType":"image/jpeg","modifiedTime":"2026-10-20T10:00:00Z","imageMediaMetadata":{"width":4000,"height":3000,"time":"2014:08:21 12:00:00"}}]}""", Encoding.UTF8, "application/json")
        });
        await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "account-A", handler);
        var page = await connection.ReadMediaPageAsync(null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("file-A", page.Files.Single().Id); Assert.AreEqual(4000, page.Files.Single().Width);
        Assert.AreEqual("2014:08:21 12:00:00", page.Files.Single().CapturedTime);
        Assert.AreEqual("next-page", page.NextPageToken); Assert.IsTrue(page.IncompleteSearch);
        Assert.AreEqual(2, handler.Calls);
        Assert.IsFalse(JsonSerializer.Serialize(page).Contains("googleusercontent", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PrivateThumbnailIsProxiedWithBoundedBytesAndWithoutGivingTheClientAUrlOrToken()
    {
        await using var vault = new TestCredentialVault(); var id = Guid.NewGuid(); await GrantAsync(vault, id).ConfigureAwait(false);
        using var handler = new Handler((request, count) => count switch
        {
            1 => Identity(), 2 => Json("""{"id":"file-A","trashed":false,"thumbnailLink":"https://lh3.googleusercontent.com/drive-storage/synthetic=s220"}"""),
            3 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([255, 216, 255, 42]) }, _ => throw new InvalidOperationException("Unexpected media request.")
        });
        await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "account-A", handler);
        var preview = await connection.ReadMediaPreviewAsync("file-A", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("image/jpeg", preview!.MimeType); CollectionAssert.AreEqual(new byte[] { 255, 216, 255, 42 }, preview.Bytes);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task WrongAccountAndUntrustedThumbnailOriginCannotExfiltrateGoogleCredentials()
    {
        foreach (var wrongIdentity in new[] { false, true })
        {
            await using var vault = new TestCredentialVault(); var id = Guid.NewGuid(); await GrantAsync(vault, id).ConfigureAwait(false);
            using var handler = new Handler((request, count) => count == 1 ? Identity() : Json("""{"id":"file-A","thumbnailLink":"https://foreign.invalid/d/steal"}"""));
            await using var connection = new GoogleAccountConnection(Configuration(), vault, id, wrongIdentity ? "wrong-account" : "account-A", handler);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadMediaPreviewAsync("file-A", CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(wrongIdentity ? 1 : 2, handler.Calls);
        }
    }

    [TestMethod]
    public async Task TrashedFilesAndMissingThumbnailsAreUnavailableAndNeverDownloadOriginals()
    {
        foreach (var body in new[] { """{"id":"file-A","trashed":true,"thumbnailLink":"https://foreign.invalid/d/no"}""", """{"id":"file-A","trashed":false}""" })
        {
            await using var vault = new TestCredentialVault(); var id = Guid.NewGuid(); await GrantAsync(vault, id).ConfigureAwait(false);
            using var handler = new Handler((request, count) => count == 1 ? Identity() : Json(body));
            await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "account-A", handler);
            Assert.IsNull(await connection.ReadMediaPreviewAsync("file-A", CancellationToken.None).ConfigureAwait(false)); Assert.AreEqual(2, handler.Calls);
        }
    }
    private static async Task GrantAsync(TestCredentialVault vault, Guid id)
    {
        var store = new GoogleCredentialDataStore(vault, id, () => CancellationToken.None);
        await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
    }
    private static HttpResponseMessage Identity() => Json("""{"user":{"permissionId":"account-A","displayName":"Synthetic"}}""");
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static DesktopOAuthConfiguration Configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpa-media-oauth-" + Guid.NewGuid().ToString("N") + ".json");
        try { File.WriteAllText(path, """{"installed":{"client_id":"synthetic.apps.googleusercontent.com","client_secret":"synthetic-public-client"}}"""); return DesktopOAuthConfiguration.Load(path); }
        finally { File.Delete(path); }
    }
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("synthetic-access", request.Headers.Authorization!.Parameter);
            Assert.IsTrue(request.RequestUri!.Host is "www.googleapis.com" or "lh3.googleusercontent.com");
            return Task.FromResult(respond(request, ++Calls));
        }
    }
}
