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
    public async Task WorkspaceApprovalRequiresHostSessionCsrfAndReviewedCapturedFields()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint); var diagnostics = new List<Exception>(); host.Diagnostic += diagnostics.Add;
        using (var anonymous = await http.GetAsync("api/library").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode(); var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString()!;
        using (var current = await http.GetAsync("api/library").ConfigureAwait(false)) { current.EnsureSuccessStatusCode(); Assert.AreEqual(JsonValueKind.Null, (await current.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("controller").ValueKind); }
        using (var inspected = await http.GetAsync("api/workspace-inspection?account=account&folder=metadata").ConfigureAwait(false)) inspected.EnsureSuccessStatusCode();
        var approval = new Mpa.Processor.AppServices.ApproveLibraryWorkspace("account", "metadata", 1, 7, 1, new string('a', 64), false, true);
        using (var forged = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, approval, null, "api/library").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, forged.StatusCode);
        using (var invalid = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, approval with { FolderId = "root" }, csrf, "api/library").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.IsNull(engine.WorkspaceApproval);
        using (var saved = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, approval, csrf, "api/library").ConfigureAwait(false)) saved.EnsureSuccessStatusCode();
        Assert.AreEqual(approval, engine.WorkspaceApproval);
        engine.RejectWorkspace = true;
        using (var changed = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, approval, csrf, "api/library").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Conflict, changed.StatusCode);
        engine.RejectWorkspace = false; engine.WorkspaceFailure = new Mpa.Processor.GoogleDrive.DriveWorkspaceSelectionException();
        using (var root = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, approval, csrf, "api/library").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.BadRequest, root.StatusCode);
        engine.WorkspaceFailure = new Mpa.Processor.GoogleDrive.DriveWorkspaceObjectChangedException();
        using (var moved = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, approval, csrf, "api/library").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Conflict, moved.StatusCode);
        engine.WorkspaceFailure = new Mpa.Processor.Core.LibraryBootstrapApprovalConflictException();
        using (var retained = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, approval, csrf, "api/library").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Conflict, retained.StatusCode);
        Assert.AreEqual(0, diagnostics.Count); Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }
    [TestMethod]
    public async Task ViewerMutationsRequireCsrfAndUseAuthenticatedBrowserIdentityInsteadOfCallerSuppliedOwnership()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint); var diagnostics = new List<Exception>(); host.Diagnostic += diagnostics.Add;
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false); var csrf = session.GetProperty("csrf").GetString(); var id = Guid.NewGuid();
        using (var denied = await MutationAsync(http, "api/viewers", host.LocalEndpoint, null).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.AreEqual(0, engine.ViewerCalls.Count);
        async Task<HttpResponseMessage> OpenAsync(HttpClient client, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/viewers") { Content = JsonContent.Create(new Mpa.Processor.Core.CatalogViewerRequest(id, Guid.NewGuid(), null)) };
            request.Headers.Add("Origin", host.LocalEndpoint.GetLeftPart(UriPartial.Authority)); request.Headers.Add("X-MPA-CSRF", token); return await client.SendAsync(request).ConfigureAwait(false);
        }
        using (var created = await OpenAsync(http, csrf!).ConfigureAwait(false))
        { created.EnsureSuccessStatusCode(); Assert.AreEqual(id, (await created.Content.ReadFromJsonAsync<DesktopViewerWindow>().ConfigureAwait(false))!.Id); }
        var owner = engine.ViewerCalls.Single().Owner; Assert.AreNotEqual(Guid.Empty, owner);
        using var other = Client(host.LocalEndpoint); using var otherLogin = await LoginAsync(other, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); otherLogin.EnsureSuccessStatusCode();
        var otherSession = await otherLogin.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        using (var independent = await OpenAsync(other, otherSession.GetProperty("csrf").GetString()!).ConfigureAwait(false)) independent.EnsureSuccessStatusCode();
        Assert.AreNotEqual(owner, engine.ViewerCalls[1].Owner);
        using (var invalid = await http.GetAsync("api/viewers?viewer=" + id.ToString("D") + "&position=01").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var gone = await http.GetAsync("api/viewers?viewer=" + id.ToString("D") + "&position=0").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
        using var close = new HttpRequestMessage(HttpMethod.Delete, "api/viewers?viewer=" + id.ToString("D")) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        close.Headers.Add("Origin", host.LocalEndpoint.GetLeftPart(UriPartial.Authority)); close.Headers.Add("X-MPA-CSRF", csrf);
        using (var closed = await http.SendAsync(close).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.NoContent, closed.StatusCode);
        Assert.AreEqual(("close", owner), engine.ViewerCalls[^1]);
        using var signOut = new HttpRequestMessage(HttpMethod.Delete, "api/session") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        signOut.Headers.Add("Origin", host.LocalEndpoint.GetLeftPart(UriPartial.Authority)); signOut.Headers.Add("X-MPA-CSRF", csrf);
        using (var signedOut = await http.SendAsync(signOut).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.AreEqual(("close-browser", owner), engine.ViewerCalls[^1]);
        using (var locked = await http.GetAsync("api/status").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, locked.StatusCode);
        using (var otherStillConnected = await other.GetAsync("api/status").ConfigureAwait(false)) otherStillConnected.EnsureSuccessStatusCode();
        Assert.AreEqual(0, diagnostics.Count); Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }
    [TestMethod]
    public async Task CatalogRequiresBrowserAuthenticationAndRejectsMalformedPositionsWithoutFaultingTheEngine()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint); var diagnostics = new List<Exception>(); host.Diagnostic += diagnostics.Add;
        using (var denied = await http.GetAsync("api/catalog").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); login.EnsureSuccessStatusCode();
        using (var valid = await http.GetAsync("api/catalog").ConfigureAwait(false))
        { valid.EnsureSuccessStatusCode(); Assert.AreEqual(0, (await valid.Content.ReadFromJsonAsync<DesktopCatalogPage>().ConfigureAwait(false))!.Photos.Count); }
        foreach (var invalid in new[] { "api/catalog?direction=wrong", "api/catalog?date=+1", "api/catalog?cursor=invalid", "api/catalog?account=a&account=b", "api/catalog-preview?asset=not-a-uuid" })
        { using var rejected = await http.GetAsync(invalid).ConfigureAwait(false); Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, invalid); }
        using (var unavailable = await http.GetAsync("api/catalog-preview?asset=" + Guid.NewGuid().ToString("D")).ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.NotFound, unavailable.StatusCode);
        using var usable = await http.GetAsync("api/status").ConfigureAwait(false); usable.EnsureSuccessStatusCode(); Assert.AreEqual(0, diagnostics.Count); Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }
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
        foreach (var path in new[] { "api/library", "api/workspace-inspection?account=account&folder=metadata" })
        { using var setup = await http.GetAsync(path).ConfigureAwait(false); Assert.AreEqual(HttpStatusCode.Forbidden, setup.StatusCode); }
        var approval = new Mpa.Processor.AppServices.ApproveLibraryWorkspace("account", "metadata", 1, 7, 1, new string('a', 64), false, true);
        using (var setup = await SourceMutationAsync(http, host.HttpsEndpoint, HttpMethod.Post, approval, session.GetProperty("csrf").GetString(), "api/library").ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.Forbidden, setup.StatusCode);
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
        using var response = await other.GetAsync("api/catalog?account=synthetic").ConfigureAwait(false); response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<DesktopCatalogPage>().ConfigureAwait(false);
        Assert.AreEqual(Guid.Parse("00000000-0000-4000-8000-000000000001"), page!.Photos.Single().Id);
        Assert.AreEqual("synthetic.jpg", page.Photos.Single().Name);
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task HostShutdownJoinsAnActiveReadAndStopsAdmissionBeforeReleasingTheEngine()
    {
        using var assets = new Assets(); var engine = new Engine { Hold = true };
        var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false); login.EnsureSuccessStatusCode();
        var response = http.GetAsync("api/catalog?account=synthetic");
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stop = host.DisposeAsync().AsTask();
        try { Assert.IsFalse(stop.IsCompleted, "Request owner must join before the application can be released."); Assert.IsFalse(host.ExitConfirmed); }
        finally { engine.Release.TrySetResult(); }
        using var completed = await response.ConfigureAwait(false); completed.EnsureSuccessStatusCode();
        await stop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.IsTrue(host.ExitConfirmed);
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
        using var response = await http.GetAsync("api/catalog?account=synthetic").ConfigureAwait(false);
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

    [TestMethod]
    public async Task ProcessingStateConflictPreservesTheUsableSessionAndIsNotAnInternalFailure()
    {
        using var assets = new Assets(); var engine = new Engine { ControlConflict = true }; var diagnostics = 0;
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        host.Diagnostic += _ => Interlocked.Increment(ref diagnostics);
        using var http = Client(host.LocalEndpoint);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode(); var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString()!;
        using var conflict = await MutationAsync(http, "api/pause", host.LocalEndpoint, csrf).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode); StringAssert.Contains(await conflict.Content.ReadAsStringAsync().ConfigureAwait(false), "Refresh Activity");
        using var status = await http.GetAsync("api/status").ConfigureAwait(false); status.EnsureSuccessStatusCode();
        Assert.AreEqual(0, diagnostics); Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task ActivityAndSourceRecoveryRequireAuthenticatedCapturedCommandsAndCsrf()
    {
        using var assets = new Assets(); var engine = new Engine();
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        using var http = Client(host.LocalEndpoint);
        using (var anonymous = await http.GetAsync("api/activity").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode(); var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString()!;
        var scan = new Mpa.Processor.Core.SourceScanRequest(Guid.NewGuid(), Guid.NewGuid(), 3, 2);
        using (var forged = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, scan, null, "api/source-scans").ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.IsNull(engine.RequestedScan);
        using (var invalid = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, scan with { RequestId = Guid.Empty }, csrf, "api/source-scans").ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var created = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, scan, csrf, "api/source-scans").ConfigureAwait(false)) created.EnsureSuccessStatusCode();
        Assert.AreEqual(scan, engine.RequestedScan);
        var retry = new Mpa.Processor.Core.SourceRetryRequest(scan.RequestId, scan.SourceId, 3, 2);
        using (var recovered = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post, retry, csrf, "api/source-retries").ConfigureAwait(false)) recovered.EnsureSuccessStatusCode();
        Assert.AreEqual(retry, engine.RequestedRetry);
        using (var activity = await http.GetAsync("api/activity").ConfigureAwait(false)) activity.EnsureSuccessStatusCode();
        using (var invalidCursor = await http.GetAsync("api/activity?cursor=invalid").ConfigureAwait(false)) Assert.AreEqual(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    [TestMethod]
    public async Task StorageAdmissionFailureIsActionableAndDoesNotInvalidateTheBrowserSession()
    {
        using var assets = new Assets(); var engine = new Engine { StorageBlocked = true }; var diagnostics = 0;
        await using var host = await DesktopWebHost.StartAsync(engine, assets.Path, null, CancellationToken.None).ConfigureAwait(false);
        host.Diagnostic += _ => diagnostics++;
        using var http = Client(host.LocalEndpoint);
        using var login = await LoginAsync(http, host.OpenLocalBrowser(), host.LocalEndpoint.GetLeftPart(UriPartial.Authority)).ConfigureAwait(false);
        login.EnsureSuccessStatusCode(); var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("csrf").GetString()!;
        using var denied = await SourceMutationAsync(http, host.LocalEndpoint, HttpMethod.Post,
            new Mpa.Processor.Core.SourceScanRequest(Guid.NewGuid(), Guid.NewGuid(), 1, 1), csrf, "api/source-scans").ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.InsufficientStorage, denied.StatusCode);
        StringAssert.Contains(await denied.Content.ReadAsStringAsync().ConfigureAwait(false), "Free space");
        Assert.IsNull(engine.RequestedScan);
        using var status = await http.GetAsync("api/status").ConfigureAwait(false); status.EnsureSuccessStatusCode();
        Assert.AreEqual(0, diagnostics); Assert.AreEqual(0, host.FrameworkDiagnostics.Count);
    }

    private static async Task<HttpResponseMessage> SourceMutationAsync(HttpClient http, Uri endpoint, HttpMethod method, object body, string? csrf, string path = "api/sources")
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
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
        public Mpa.Processor.AppServices.ApproveLibraryWorkspace? WorkspaceApproval { get; private set; }
        public bool RejectWorkspace { get; set; }
        public Exception? WorkspaceFailure { get; set; }
        public Task<Mpa.Processor.Core.LibraryControllerPin?> LibraryAsync(CancellationToken cancellationToken) => Task.FromResult<Mpa.Processor.Core.LibraryControllerPin?>(null);
        public Task<Mpa.Processor.AppServices.LibraryWorkspaceInspection> InspectWorkspaceAsync(string accountId, string folderId, CancellationToken cancellationToken) => Task.FromResult(
            new Mpa.Processor.AppServices.LibraryWorkspaceInspection(new(accountId, folderId, "Metadata", 7, false, true), 1, new(1, []),
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "synthetic-public-controller", new string('a', 64), Guid.NewGuid(), 1, "synthetic-public-publisher", new string('b', 64)), null));
        public Task<Mpa.Processor.Core.LibraryControllerPin> ApproveWorkspaceAsync(Mpa.Processor.AppServices.ApproveLibraryWorkspace request, CancellationToken cancellationToken)
        {
            if (RejectWorkspace) throw new Mpa.Processor.AppServices.LibraryWorkspaceApprovalException("Settings changed. Review them again.");
            if (WorkspaceFailure is not null) throw WorkspaceFailure;
            WorkspaceApproval = request; return Task.FromResult(new Mpa.Processor.Core.LibraryControllerPin(Guid.NewGuid(), Guid.NewGuid(), 1, request.AccountId, request.FolderId, "synthetic-public-controller", request.ControllerKeyId));
        }
        public List<(string Action, Guid Owner)> ViewerCalls { get; } = [];
        public bool Paused { get; private set; }
        public bool Hold { get; init; }
        public bool Unavailable { get; init; }
        public bool ControlConflict { get; init; }
        public DesktopAddSource? AddedSource { get; private set; }
        public bool StorageBlocked { get; init; }
        public Mpa.Processor.Core.SourceScanRequest? RequestedScan { get; private set; }
        public Mpa.Processor.Core.SourceRetryRequest? RequestedRetry { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DesktopSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new DesktopSnapshot(Guid.NewGuid(), "ready", []));
        public async Task<DesktopCatalogPage> CatalogAsync(string? accountId, string? position, bool newer, long? dateUtc, CancellationToken cancellationToken)
        {
            if (position == "invalid") throw new InvalidDesktopCatalogQueryException();
            if (Unavailable) throw new HttpRequestException("Synthetic Google outage.");
            Started.TrySetResult(); if (Hold) await Release.Task.ConfigureAwait(false);
            return new DesktopCatalogPage(1, 1, accountId is null ? [] : [new(Guid.Parse("00000000-0000-4000-8000-000000000001"), accountId,
                "synthetic.jpg", "image/jpeg", 40, 30, null, "Unknown", null)], null, null);
        }
        public Task<DesktopPreview?> CatalogPreviewAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<DesktopPreview?>(null);
        public Task<DesktopViewerWindow> OpenViewerAsync(Guid browser, Mpa.Processor.Core.CatalogViewerRequest request, CancellationToken cancellationToken)
        { ViewerCalls.Add(("open", browser)); return Task.FromResult(new DesktopViewerWindow(request.Id, 1, 0, [new(0, request.SelectedId, null)])); }
        public Task<DesktopViewerWindow> ReadViewerAsync(Guid browser, Guid id, long position, CancellationToken cancellationToken) => throw new Mpa.Processor.Core.CatalogViewerUnavailableException();
        public Task CloseViewerAsync(Guid browser, Guid id, CancellationToken cancellationToken) { ViewerCalls.Add(("close", browser)); return Task.CompletedTask; }
        public Task CloseBrowserViewersAsync(Guid browser, CancellationToken cancellationToken) { ViewerCalls.Add(("close-browser", browser)); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken) { if (ControlConflict) throw new Mpa.Processor.Core.ProcessorWorkStateConflictException(); Paused = true; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken) { Paused = false; return Task.CompletedTask; }
        public Task AccountAsync(DesktopAccountCommand command, CancellationToken cancellationToken) => throw new InvalidOperationException("This test must not perform Google account actions.");
        public Task<Mpa.Processor.Core.LibrarySourceSnapshot> SourcesAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("This test does not configure sources.");
        public Task<DesktopFolderLocation> FoldersAsync(string accountId, string folderId, string? cursor, CancellationToken cancellationToken) => throw new InvalidOperationException("This test does not read folders.");
        public Task<Mpa.Processor.Core.LibrarySourceSnapshot> AddSourceAsync(DesktopAddSource request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); AddedSource = request; return Task.FromResult(new Mpa.Processor.Core.LibrarySourceSnapshot(2, [])); }
        public Task<Mpa.Processor.Core.LibrarySourceSnapshot> UpdateSourceAsync(DesktopUpdateSource request, CancellationToken cancellationToken) => throw new Mpa.Processor.Core.StaleLibraryConfigurationException();
        public Task<Mpa.Processor.Core.SourceDiscoveryActivityPage> ActivityAsync(Guid? cursor, CancellationToken cancellationToken) => Task.FromResult(new Mpa.Processor.Core.SourceDiscoveryActivityPage([], null));
        public Task<Mpa.Processor.Core.SourceDiscoverySchedule> RequestScanAsync(Mpa.Processor.Core.SourceScanRequest request, CancellationToken cancellationToken)
        {
            if (StorageBlocked) throw new Mpa.Processor.Core.InsufficientStorageBudgetException(new(1, 1, 0, 0));
            RequestedScan = request; return Task.FromResult(new Mpa.Processor.Core.SourceDiscoverySchedule(request.RequestId, true));
        }
        public Task<int> RetrySourceAsync(Mpa.Processor.Core.SourceRetryRequest request, CancellationToken cancellationToken)
        { RequestedRetry = request; return Task.FromResult(1); }
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
