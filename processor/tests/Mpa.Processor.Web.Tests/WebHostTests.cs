using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Mpa.Processor.Credentials;
using Mpa.Processor.Lan;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.Web.Tests;

[TestClass]
public sealed class WebHostTests
{
    [TestMethod]
    public async Task ActualHttpsBrowserSessionUsesTheApprovedServerPinAndCannotManageHostGoogleAccounts()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var vault = new Vault();
        await using var identity = new LanIdentityOwner(vault, Guid.NewGuid(), Guid.NewGuid());
        await identity.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, identity.Identity, CancellationToken.None).ConfigureAwait(false);
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer(),
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            } };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => TlsPeerPin.Matches(certificate, identity.Identity.TlsKeyId, errors, DateTimeOffset.UtcNow);
        Assert.AreEqual(2, host.ListeningEndpoints.Count);
        Assert.IsTrue(host.ListeningEndpoints.All(endpoint => endpoint.Host == "127.0.0.1"), "Even the HTTPS fixture must never trigger an all-interface listener.");
        using var http = new HttpClient(handler) { BaseAddress = host.HttpsEndpoint, Timeout = TimeSpan.FromSeconds(15) };
        using var accepted = await LoginAsync(http, host.InviteBrowser(), host.HttpsEndpoint!.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        accepted.EnsureSuccessStatusCode();
        var session = await accepted.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        Assert.IsFalse(session.GetProperty("hostOwner").GetBoolean());
        StringAssert.Contains(accepted.Headers.GetValues("Set-Cookie").Single(), "secure");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/accounts") { Content = JsonContent.Create(new { operation = "connect" }) };
        request.Headers.Add("Origin", host.HttpsEndpoint.GetLeftPart(UriPartial.Authority)); request.Headers.Add("X-MPA-CSRF", session.GetProperty("csrf").GetString());
        using var denied = await http.SendAsync(request).ConfigureAwait(false); Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var status = await http.GetAsync("api/status").ConfigureAwait(false); status.EnsureSuccessStatusCode();
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task ActualHttpRequiresSingleUseApprovalAndRejectsForeignOriginCsrfAndRebinding()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint);
        using (var denied = await http.GetAsync("api/status").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        var invitation = host.OpenLocalBrowser();
        using (var hostile = await LoginAsync(http, invitation, "https://foreign.invalid").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Forbidden, hostile.StatusCode);
        using var accepted = await LoginAsync(http, invitation, host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        accepted.EnsureSuccessStatusCode();
        var csrf = (await accepted.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString()!;
        var cookie = accepted.Headers.GetValues("Set-Cookie").Single();
        StringAssert.Contains(cookie, "httponly"); StringAssert.Contains(cookie, "samesite=strict");
        using (var replay = await LoginAsync(http, invitation, host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);
        using (var status = await http.GetAsync("api/status").ConfigureAwait(false)) status.EnsureSuccessStatusCode();
        using (var forged = await MutationAsync(http, "api/pause", host.LocalEndpoint, null).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, forged.StatusCode);
        using (var paused = await MutationAsync(http, "api/pause", host.LocalEndpoint, csrf).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.NoContent, paused.StatusCode);
        Assert.IsTrue(engine.Paused);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "api/status"); rebound.Headers.Host = "foreign.invalid:" + host.LocalEndpoint.Port;
        using (var denied = await http.SendAsync(rebound).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task ClosingOneBrowserLeavesEngineAndOtherAuthenticatedBrowserRunning()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var other = Client(host.LocalEndpoint);
        using (var first = Client(host.LocalEndpoint))
        {
            using var login = await LoginAsync(first, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); login.EnsureSuccessStatusCode();
        }
        using var second = await LoginAsync(other, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); second.EnsureSuccessStatusCode();
        using var response = await other.GetAsync("api/photos?account=synthetic").ConfigureAwait(false); response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<DesktopPhotoPage>().ConfigureAwait(false);
        Assert.AreEqual("synthetic-file", page!.Photos.Single().Id);
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task HostShutdownJoinsAnActiveReadAndStopsAdmissionBeforeReleasingTheEngine()
    {
        using var assets = new Assets(); var engine = new Engine { Hold = true };
        var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); login.EnsureSuccessStatusCode();
        var response = http.GetAsync("api/photos?account=synthetic");
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stop = host.DisposeAsync().AsTask();
        try { Assert.IsFalse(stop.IsCompleted, "Request owner must join before the application can be released."); }
        finally { engine.Release.TrySetResult(); }
        using var completed = await response.ConfigureAwait(false); completed.EnsureSuccessStatusCode();
        await stop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
        await host.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OperationalFailureIsNotAnEmptyPhotoPageAndAssetsCannotReadArbitraryPaths()
    {
        using var assets = new Assets(); var engine = new Engine { Unavailable = true };
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); login.EnsureSuccessStatusCode();
        using var response = await http.GetAsync("api/photos?account=synthetic").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync().ConfigureAwait(false), "preserved");
        using var missing = await http.GetAsync("private-key.json").ConfigureAwait(false); Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var index = await http.GetAsync("").ConfigureAwait(false); index.EnsureSuccessStatusCode();
        StringAssert.Contains(index.Headers.GetValues("Content-Security-Policy").Single(), "frame-ancestors 'none'");
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task SourceChangesRequireCsrfAndCapturedRevisionsAndReportConflictsWithoutLosingTheSession()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString()!;
        var selection = new DesktopAddSource("account-A", "folder-A", true, 1);
        using (var forged = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, selection, null).ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.IsNull(engine.AddedSource);
        using (var invalid = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, new { accountId = (string?)null, folderId = "folder-A", expectedRevision = 1 }, csrf).ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.IsNull(engine.AddedSource);
        using (var added = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, selection, csrf).ConfigureAwait(false))
        {
            added.EnsureSuccessStatusCode();
            Assert.AreEqual(2L, (await added.Content.ReadFromJsonAsync<Mpa.Processor.Core.LibrarySourceSnapshot>().ConfigureAwait(false))!.Revision);
        }
        Assert.AreEqual(selection, engine.AddedSource);
        using (var stale = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Put,
            new DesktopUpdateSource(Guid.NewGuid(), false, false, 1, 1), csrf).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            StringAssert.Contains(await stale.Content.ReadAsStringAsync().ConfigureAwait(false), "Refresh Sources");
        }
        using var stillUsable = await http.GetAsync("api/status").ConfigureAwait(false); stillUsable.EnsureSuccessStatusCode();
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    private static async Task<HttpResponseMessage> SourceMutationAsync(HttpClient http, Uri endpoint, HttpMethod method, object body, string? csrf)
    {
        using var request = new HttpRequestMessage(method, "api/sources") { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", endpoint.GetLeftPart(UriPartial.Authority));
        if (csrf is not null) request.Headers.Add("X-MPA-CSRF", csrf);
        return await http.SendAsync(request).ConfigureAwait(false);
    }

    private static HttpClient Client(Uri endpoint) => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(15) };
    private static async Task<HttpResponseMessage> LoginAsync(HttpClient http, BrowserInvitation invitation, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/session") { Content = JsonContent.Create(new { secret = invitation.Secret }) };
        request.Headers.Add("Origin", origin); return await http.SendAsync(request).ConfigureAwait(false);
    }
    private static async Task<HttpResponseMessage> MutationAsync(HttpClient http, string path, Uri endpoint, string? csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", endpoint.GetLeftPart(UriPartial.Authority)); if (csrf is not null) request.Headers.Add("X-MPA-CSRF", csrf);
        return await http.SendAsync(request).ConfigureAwait(false);
    }
    private sealed class Engine : IDesktopWebApplication
    {
        public bool Paused { get; private set; }
        public bool Hold { get; init; }
        public bool Unavailable { get; init; }
        public DesktopAddSource? AddedSource { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DesktopSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new DesktopSnapshot(Guid.NewGuid(), "ready", []));
        public async Task<DesktopPhotoPage> PhotosAsync(string accountId, string? cursor, CancellationToken cancellationToken)
        {
            if (Unavailable) throw new HttpRequestException("Synthetic Google outage.");
            Started.TrySetResult(); if (Hold) await Release.Task.ConfigureAwait(false);
            return new([new("synthetic-file", "synthetic.jpg", "image/jpeg", 40, 30, null, null)], null, false);
        }
        public Task<DesktopPreview?> PreviewAsync(string accountId, string fileId, CancellationToken cancellationToken) => Task.FromResult<DesktopPreview?>(null);
        public Task PauseAsync(CancellationToken cancellationToken) { Paused = true; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken) { Paused = false; return Task.CompletedTask; }
        public Task AccountAsync(DesktopAccountCommand command, CancellationToken cancellationToken) => throw new InvalidOperationException("This test must not perform Google account actions.");
        public Task<Mpa.Processor.Core.LibrarySourceSnapshot> SourcesAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("This test does not configure sources.");
        public Task<DesktopFolderLocation> FoldersAsync(string accountId, string folderId, string? cursor, CancellationToken cancellationToken) => throw new InvalidOperationException("This test does not read folders.");
        public Task<Mpa.Processor.Core.LibrarySourceSnapshot> AddSourceAsync(DesktopAddSource request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); AddedSource = request; return Task.FromResult(new Mpa.Processor.Core.LibrarySourceSnapshot(2, [])); }
        public Task<Mpa.Processor.Core.LibrarySourceSnapshot> UpdateSourceAsync(DesktopUpdateSource request, CancellationToken cancellationToken) => throw new Mpa.Processor.Core.StaleLibraryConfigurationException();
    }
    private sealed class Assets : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpa-web-test-" + Guid.NewGuid().ToString("N"));
        public Assets() { Directory.CreateDirectory(Path); File.WriteAllText(System.IO.Path.Combine(Path, "index.html"), "<!doctype html><title>Synthetic MPA test</title>"); }
        public void Dispose() { File.Delete(System.IO.Path.Combine(Path, "index.html")); Directory.Delete(Path); }
    }
    private sealed class Vault : ICredentialVault
    {
        private byte[]? _record;
        public Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_record is null ? null : SecretLease.CopyFrom(_record)); }
        public Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); if (_record is not null) CryptographicOperations.ZeroMemory(_record); _record = bytes.ToArray(); return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("Web transport tests must not delete credentials.");
        public ValueTask DisposeAsync() { if (_record is not null) CryptographicOperations.ZeroMemory(_record); return ValueTask.CompletedTask; }
    }
}
