using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Auth.OAuth2.Requests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed class GoogleConnectionTests
{
    [TestMethod]
    public async Task ActualLoopbackPreservesOnlyTheMatchingAuthorizationState()
    {
        foreach (var wrongState in new[] { false, true })
        {
            var browser = new CapturingBrowser();
            var receiver = new DesktopCodeReceiver(browser);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var request = new GoogleAuthorizationCodeRequestUrl(new Uri("https://accounts.google.com/o/oauth2/auth"))
            {
                ClientId = "synthetic.apps.googleusercontent.com", Scope = "synthetic", RedirectUri = receiver.RedirectUri, ResponseType = "code"
            };
            var pending = receiver.ReceiveCodeAsync(request, deadline.Token);
            _ = await browser.Opened.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var callback = new Uri(receiver.RedirectUri + "?code=synthetic&state=" + Uri.EscapeDataString(wrongState ? "wrong" : request.State));
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            using var response = await http.GetAsync(callback, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (wrongState)
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => pending).ConfigureAwait(false);
            else
                Assert.AreEqual("synthetic", (await pending.ConfigureAwait(false)).Code);
        }
    }

    [TestMethod]
    public async Task ExplicitNewConnectionUsesPkceSystemBrowserAndJoinsCanceledLoopback()
    {
        await using var vault = new TestCredentialVault();
        await using var connection = new GoogleAccountConnection(Configuration(), vault, Guid.NewGuid());
        var browser = new CapturingBrowser();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var authorization = connection.AuthorizeAsync(browser, deadline.Token);
        var uri = await browser.Opened.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var fields = Query(uri);
        Assert.AreEqual("accounts.google.com", uri.Host);
        Assert.AreEqual("S256", fields["code_challenge_method"]);
        Assert.AreEqual(43, fields["code_challenge"].Length);
        Assert.IsTrue(fields["state"].Length >= 32);
        var callback = new Uri(fields["redirect_uri"]);
        Assert.AreEqual("127.0.0.1", callback.Host);
        Assert.AreEqual("http", callback.Scheme);
        await deadline.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => authorization).ConfigureAwait(false);
        using var socket = new TcpClient();
        using var socketDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<SocketException>(() => socket.ConnectAsync(callback.Host, callback.Port, socketDeadline.Token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(0, vault.Records.Count);
    }

    [TestMethod]
    public async Task ShutdownCancelsAndJoinsOutstandingBrowserAuthorization()
    {
        await using var vault = new TestCredentialVault();
        var connection = new GoogleAccountConnection(Configuration(), vault, Guid.NewGuid());
        var browser = new CapturingBrowser();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var authorization = connection.AuthorizeAsync(browser, deadline.Token);
        _ = await browser.Opened.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        await connection.DisposeAsync().AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => authorization).ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => connection.CheckAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ExistingGrantUsesVerifiedPermissionIdWithoutOpeningBrowser()
    {
        await using var vault = new TestCredentialVault();
        var id = Guid.NewGuid();
        var store = new GoogleCredentialDataStore(vault, id, () => CancellationToken.None);
        await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
        using var handler = new IdentityHandler(HttpStatusCode.OK, "{\"user\":{\"permissionId\":\"1234567\",\"displayName\":\"Synthetic\"}}");
        await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "1234567", handler);
        var browser = new CapturingBrowser();
        var account = await connection.AuthorizeAsync(browser, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("1234567", account.PermissionId);
        Assert.IsFalse(browser.Opened.Task.IsCompleted);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task WrongAccountAndTransientFailurePreserveCapturedGrant()
    {
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable })
        {
            await using var vault = new TestCredentialVault();
            var id = Guid.NewGuid();
            var store = new GoogleCredentialDataStore(vault, id, () => CancellationToken.None);
            await store.StoreAsync(store.UserId, GoogleCredentialTests.Grant()).ConfigureAwait(false);
            using var handler = new IdentityHandler(status, "{\"user\":{\"permissionId\":\"different\"}}");
            await using var connection = new GoogleAccountConnection(Configuration(), vault, id, "1234567", handler);
            if (status == HttpStatusCode.OK)
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => connection.CheckAsync(CancellationToken.None)).ConfigureAwait(false);
            else
                await Assert.ThrowsExactlyAsync<HttpRequestException>(() => connection.CheckAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("synthetic-refresh", (await store.GetAsync<TokenResponse>(store.UserId).ConfigureAwait(false)).RefreshToken);
        }
    }

    private static DesktopOAuthConfiguration Configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpa-synthetic-oauth-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { installed = new { client_id = "synthetic.apps.googleusercontent.com", client_secret = "synthetic-public-client" } }));
            return DesktopOAuthConfiguration.Load(path);
        }
        finally { File.Delete(path); }
    }
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(field => field.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
    private sealed class CapturingBrowser : IAuthorizationBrowser
    {
        public TaskCompletionSource<Uri> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open(Uri authorizationUri) => Opened.SetResult(authorizationUri);
    }
    private sealed class IdentityHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("www.googleapis.com", request.RequestUri!.Host);
            Assert.AreEqual("/drive/v3/about", request.RequestUri.AbsolutePath);
            Assert.AreEqual("synthetic-access", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
