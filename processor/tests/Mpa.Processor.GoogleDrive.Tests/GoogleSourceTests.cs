using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed class GoogleSourceTests
{
    private static readonly string[] CapturedParents = ["folder-A"];
    [TestMethod]
    public async Task MalformedItemDoesNotLoseOtherMediaFoldersOrTheCapturedPageCheckpoint()
    {
        using var fixture = new Fixture("""
            {"nextPageToken":"next=opaque","incompleteSearch":true,"files":[
              {"id":"folder-B","version":"1","name":"Nested","mimeType":"application/vnd.google-apps.folder","parents":["folder-A"],"capabilities":{"canListChildren":true}},
              {"id":"bad-file","version":"not-a-version","name":"bad.jpg","mimeType":"image/jpeg","parents":["folder-A"]},
              {"id":"photo-C","version":"5","name":"photo.jpg","mimeType":"image/jpeg","size":"7120","parents":["folder-A"],"imageMediaMetadata":{"width":400,"height":300,"time":"2026:10:20 15:30:00"}},
              {"id":"sidecar-D","version":"2","name":"photo.jpg.json","mimeType":"application/json","parents":["folder-A"]}
            ]}
            """);
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var page = await connection.ReadSourcePageAsync("folder-A", null, "captured+token", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(3, page.Items.Count); Assert.AreEqual(1, page.Failures.Count);
        Assert.AreEqual("bad-file", page.Failures.Single().FileId); Assert.AreEqual(1, page.Failures.Single().PageIndex);
        Assert.AreEqual("next=opaque", page.NextPageToken); Assert.IsTrue(page.IncompleteSearch);
        Assert.IsNotNull(page.Items[0].Folder); Assert.IsTrue(page.Items[0].Folder!.CanListChildren);
        Assert.AreEqual("2026:10:20 15:30:00", page.Items[1].Media!.CapturedTime);
        Assert.AreEqual(7120L, page.Items[1].Size); Assert.IsNull(page.Items[2].Media); Assert.IsNull(page.Items[2].Folder);
        StringAssert.Contains(fixture.Query!, "pageToken=captured%2Btoken"); StringAssert.Contains(fixture.Query!, "corpora=user");
        Assert.AreEqual(2, fixture.Calls, "Only identity and bounded metadata listing may be read.");
    }

    [TestMethod]
    public async Task ForeignParentSharedDriveOrRepeatedIdentityFailsThePageInsteadOfAcceptingUnscopedFacts()
    {
        foreach (var body in new[] {
            """{"files":[{"id":"outside","parents":["another-folder"]}]}""",
            """{"files":[{"id":"outside","parents":["folder-A"],"driveId":"different-drive"}]}""",
            """{"files":[{"id":"duplicate","parents":["folder-A"],"driveId":"drive-A"},{"id":"duplicate","parents":["folder-A"],"driveId":"drive-A"}]}"""
        })
        {
            using var fixture = new Fixture(body); await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadSourcePageAsync("folder-A", "drive-A", null, CancellationToken.None)).ConfigureAwait(false);
            StringAssert.Contains(fixture.Query!, "corpora=drive&driveId=drive-A");
        }
    }

    [TestMethod]
    public async Task MoreThanSixtyEntriesCannotExceedThePageBoundAndBadInputsNeverSendARequest()
    {
        using var fixture = new Fixture(JsonSerializer.Serialize(new { files = Enumerable.Range(0, 61).Select(index => new { id = "file-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), parents = CapturedParents }) }));
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadSourcePageAsync("root", null, null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadSourcePageAsync("folder-A", "drive' injected", null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadSourcePageAsync("folder-A", null, "invalid\n", CancellationToken.None));
        Assert.AreEqual(0, fixture.Calls);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadSourcePageAsync("folder-A", null, null, CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InvalidIdentityIsAnIsolatedItemFailureAndMissingDatesStayUnknown()
    {
        using var fixture = new Fixture("""
            {"files":[{"id":"invalid/path"},{"id":"valid","version":"1","name":"undated.mp4","mimeType":"video/mp4","parents":["folder-A"]}]}
            """);
        await using var connection = await fixture.ConnectAsync().ConfigureAwait(false);
        var page = await connection.ReadSourcePageAsync("folder-A", null, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, page.Items.Count); Assert.AreEqual(1, page.Failures.Count); Assert.IsNull(page.Failures.Single().FileId);
        Assert.IsNull(page.Items.Single().Media!.CapturedTime); Assert.IsNull(page.Items.Single().Media!.ModifiedTime);
        Assert.IsNull(page.NextPageToken); Assert.IsFalse(page.IncompleteSearch);
    }
    private sealed class Fixture(string response) : IDisposable
    {
        private readonly TestCredentialVault _vault = new();
        private readonly Handler _handler = new(response);
        public int Calls => _handler.Calls;
        public string? Query => _handler.Query;
        public async Task<GoogleAccountConnection> ConnectAsync()
        {
            var id = Guid.NewGuid(); var store = new GoogleCredentialDataStore(_vault, id, () => CancellationToken.None);
            await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
            var path = Path.Combine(Path.GetTempPath(), "mpa-source-oauth-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { installed = new { client_id = "synthetic.apps.googleusercontent.com", client_secret = "synthetic-public-client" } }));
                return new GoogleAccountConnection(DesktopOAuthConfiguration.Load(path), _vault, id, "account-A", _handler);
            }
            finally { File.Delete(path); }
        }
        public void Dispose() { _handler.Dispose(); _vault.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private sealed class Handler(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Query { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("www.googleapis.com", request.RequestUri!.Host); Assert.AreEqual("synthetic-access", request.Headers.Authorization!.Parameter);
            Calls++; if (Calls > 2) throw new InvalidOperationException("Source page attempted an unexpected request.");
            Query = request.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Calls == 1 ?
                """{"user":{"permissionId":"account-A","displayName":"Synthetic"}}""" : response, Encoding.UTF8, "application/json") });
        }
    }
}
