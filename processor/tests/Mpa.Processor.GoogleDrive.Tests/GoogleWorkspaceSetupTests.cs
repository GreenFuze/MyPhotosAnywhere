using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed class GoogleWorkspaceSetupTests
{
    private const string Valid = """{"id":"workspace","name":"MPA metadata","mimeType":"application/vnd.google-apps.folder","trashed":false,"version":"7","shared":false,"capabilities":{"canListChildren":true,"canAddChildren":true}}""";

    [TestMethod]
    public async Task InspectionReportsExactAccountFolderRevisionAndSharingWithoutAnyWrite()
    {
        foreach (var team in new[] { false, true })
        {
            var root = JsonNode.Parse(Valid)!.AsObject(); if (team) { root["driveId"] = "shared-drive"; root.Remove("shared"); }
            await using var fixture = await Fixture.CreateAsync(root.ToJsonString()).ConfigureAwait(false);
            Assert.IsInstanceOfType<IGoogleWorkspaceSetup>(fixture.Connection);
            var result = await fixture.Connection.InspectWorkspaceAsync("workspace", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(new DriveWorkspaceAccess("account-A", "workspace", "MPA metadata", 7, team), result);
            Assert.AreEqual(3, fixture.Calls);
        }
    }

    [TestMethod]
    public async Task SharedFoldersAreExplicitAndInvalidOrAmbiguousPropertiesCannotEnroll()
    {
        foreach (var mutation in new Action<JsonObject>[] { r => r["shared"] = "true", r => r.Remove("shared"), r => r["version"] = "07", r => r["name"] = " ",
            r => r["driveId"] = "invalid'identity", r => r["capabilities"]!["canAddChildren"] = 1 })
        {
            var root = JsonNode.Parse(Valid)!.AsObject(); mutation(root); await using var fixture = await Fixture.CreateAsync(root.ToJsonString()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Connection.InspectWorkspaceAsync("workspace", CancellationToken.None)).ConfigureAwait(false);
        }
        var shared = JsonNode.Parse(Valid)!.AsObject(); shared["shared"] = true;
        await using var expected = await Fixture.CreateAsync(shared.ToJsonString()).ConfigureAwait(false);
        Assert.IsTrue((await expected.Connection.InspectWorkspaceAsync("workspace", CancellationToken.None).ConfigureAwait(false)).Shared);
    }

    [TestMethod]
    public async Task NoWriteAccessWrongIdentityTrashAndShortcutsAreRejected()
    {
        foreach (var denied in new[] { "canListChildren", "canAddChildren" })
        {
            var root = JsonNode.Parse(Valid)!.AsObject(); root["capabilities"]![denied] = false;
            await using var fixture = await Fixture.CreateAsync(root.ToJsonString()).ConfigureAwait(false);
            var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => fixture.Connection.InspectWorkspaceAsync("workspace", CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
        }
        foreach (var mutation in new Action<JsonObject>[] { r => r["id"] = "another-folder", r => r["trashed"] = true, r => r["mimeType"] = "application/vnd.google-apps.shortcut" })
        {
            var root = JsonNode.Parse(Valid)!.AsObject(); mutation(root); await using var fixture = await Fixture.CreateAsync(root.ToJsonString()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<DriveWorkspaceObjectChangedException>(() => fixture.Connection.InspectWorkspaceAsync("workspace", CancellationToken.None)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CanonicalRootIdsCannotBypassTheDedicatedWorkspaceRequirement()
    {
        await using (var fixture = await Fixture.CreateAsync(Valid).ConfigureAwait(false))
        {
            await Assert.ThrowsExactlyAsync<DriveWorkspaceSelectionException>(() => fixture.Connection.InspectWorkspaceAsync("canonical-root", CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(2, fixture.Calls);
        }
        var shared = JsonNode.Parse(Valid)!.AsObject(); shared["driveId"] = "workspace"; shared.Remove("shared");
        await using var team = await Fixture.CreateAsync(shared.ToJsonString()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<DriveWorkspaceSelectionException>(() => team.Connection.InspectWorkspaceAsync("workspace", CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RootAliasesAndQueryInjectionFailBeforeAuthenticationOrNetwork()
    {
        await using var fixture = await Fixture.CreateAsync(Valid).ConfigureAwait(false);
        foreach (var id in new[] { "root", "", "folder' or trashed=true", "folder/child" })
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Connection.InspectWorkspaceAsync(id, CancellationToken.None));
        Assert.AreEqual(0, fixture.Calls);
    }

    private sealed class Fixture : HttpMessageHandler, IAsyncDisposable
    {
        private readonly TestCredentialVault _vault = new();
        private readonly string _response;
        public GoogleAccountConnection Connection { get; private set; } = null!;
        public int Calls { get; private set; }
        private Fixture(string response) => _response = response;
        public static async Task<Fixture> CreateAsync(string response)
        {
            var fixture = new Fixture(response); var id = Guid.NewGuid();
            var path = Path.Combine(Path.GetTempPath(), "mpa-workspace-setup-oauth-" + id.ToString("N") + ".json");
            try
            {
                var store = new GoogleCredentialDataStore(fixture._vault, id, () => CancellationToken.None);
                await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
                File.WriteAllText(path, "{\"installed\":{\"client_id\":\"synthetic.apps.googleusercontent.com\",\"client_secret\":\"synthetic-public-client\"}}");
                fixture.Connection = new(DesktopOAuthConfiguration.Load(path), fixture._vault, id, "account-A", fixture); return fixture;
            }
            catch { await fixture._vault.DisposeAsync().ConfigureAwait(false); fixture.Dispose(); throw; }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Assert.AreEqual(HttpMethod.Get, request.Method); Assert.AreEqual("www.googleapis.com", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath == "/drive/v3/files/root")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"canonical-root\"}", Encoding.UTF8, "application/json") });
            var body = request.RequestUri.AbsolutePath == "/drive/v3/about" ? "{\"user\":{\"permissionId\":\"account-A\",\"displayName\":\"Synthetic\"}}" : _response;
            if (request.RequestUri.AbsolutePath != "/drive/v3/about") Assert.AreEqual("/drive/v3/files/workspace", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
        public async ValueTask DisposeAsync() { if (Connection is not null) await Connection.DisposeAsync().ConfigureAwait(false); await _vault.DisposeAsync().ConfigureAwait(false); Dispose(); }
    }
}
