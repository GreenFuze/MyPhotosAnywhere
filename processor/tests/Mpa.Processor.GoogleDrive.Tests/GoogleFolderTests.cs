using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed class GoogleFolderTests
{
    [TestMethod]
    public async Task RootAliasIsResolvedBeforePersistenceAndDuplicateNamesKeepDistinctDriveIds()
    {
        await using var vault = new TestCredentialVault(); var id = Guid.NewGuid(); await StoreAsync(vault, id).ConfigureAwait(false);
        using var handler = new Handler((_, count) => count switch
        {
            1 => """{"user":{"permissionId":"account-A","displayName":"Synthetic"}}""",
            2 => """{"id":"canonical-root","name":"My Drive","mimeType":"application/vnd.google-apps.folder","capabilities":{"canListChildren":true}}""",
            3 => """{"nextPageToken":"next","incompleteSearch":true,"files":[{"id":"folder-A","name":"Camera","mimeType":"application/vnd.google-apps.folder","parents":["canonical-root"],"capabilities":{"canListChildren":true}},{"id":"folder-B","name":"Camera","mimeType":"application/vnd.google-apps.folder","parents":["canonical-root"],"driveId":"shared-A","capabilities":{"canListChildren":false}}]}""",
            _ => throw new InvalidOperationException("Unexpected folder request.")
        });
        await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "account-A", handler);
        var root = await connection.ReadFolderAsync("root", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("canonical-root", root.Id); Assert.IsTrue(root.CanListChildren);
        var page = await connection.ReadFolderPageAsync(root.Id, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, page.Folders.Count); Assert.AreEqual(2, page.Folders.Select(folder => folder.Id).Distinct().Count());
        Assert.IsTrue(page.IncompleteSearch); Assert.AreEqual("next", page.NextPageToken);
        Assert.AreEqual("shared-A", page.Folders[1].SharedDriveId); Assert.IsFalse(page.Folders[1].CanListChildren);
    }

    [TestMethod]
    public async Task QueryInjectionIsRejectedBeforeAnyRequestAndSourceVerificationRejectsTrashedOrWrongIds()
    {
        await using var vault = new TestCredentialVault(); var id = Guid.NewGuid(); await StoreAsync(vault, id).ConfigureAwait(false);
        using var handler = new Handler((_, count) => count == 1 ? """{"user":{"permissionId":"account-A","displayName":"Synthetic"}}""" :
            """{"id":"different-folder","name":"Camera","mimeType":"application/vnd.google-apps.folder","trashed":true}""");
        await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "account-A", handler);
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadFolderPageAsync("folder' or trashed=true", null, CancellationToken.None));
        Assert.ThrowsExactly<ArgumentException>(() => connection.ReadFolderPageAsync("root", null, CancellationToken.None));
        Assert.AreEqual(0, handler.Calls);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadFolderAsync("folder-A", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task FolderPageCannotInjectAnItemFromOutsideItsCapturedParent()
    {
        await using var vault = new TestCredentialVault(); var id = Guid.NewGuid(); await StoreAsync(vault, id).ConfigureAwait(false);
        using var handler = new Handler((_, count) => count == 1 ? """{"user":{"permissionId":"account-A","displayName":"Synthetic"}}""" :
            """{"files":[{"id":"folder-A","name":"Camera","mimeType":"application/vnd.google-apps.folder","parents":["wrong-parent"]}]}""");
        await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "account-A", handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.ReadFolderPageAsync("captured-parent", null, CancellationToken.None)).ConfigureAwait(false);
    }
    private static async Task StoreAsync(TestCredentialVault vault, Guid id)
    {
        var store = new GoogleCredentialDataStore(vault, id, () => CancellationToken.None);
        await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
    }
    private static DesktopOAuthConfiguration Configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpa-folder-oauth-" + Guid.NewGuid().ToString("N") + ".json");
        try { File.WriteAllText(path, JsonSerializer.Serialize(new { installed = new { client_id = "synthetic.apps.googleusercontent.com", client_secret = "synthetic-public-client" } })); return DesktopOAuthConfiguration.Load(path); }
        finally { File.Delete(path); }
    }
    private sealed class Handler(Func<HttpRequestMessage, int, string> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("www.googleapis.com", request.RequestUri!.Host);
            Assert.AreEqual("synthetic-access", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request, ++Calls), Encoding.UTF8, "application/json") });
        }
    }
}
