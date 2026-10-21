using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.Web;

namespace Mpa.Processor.DesktopAcceptance;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--workspace-read")
        {
            await WorkspaceReadAcceptance.RunAsync(args[1..]).ConfigureAwait(false); return;
        }
        if (args.Length != 5) throw new ArgumentException("Expected packaged OAuth configuration, isolated account state, packaged web assets, disposable source folder and sanitized report.");
        var root = Path.GetFullPath(args[1]);
        if (!Path.GetFileName(root).StartsWith("desktop-account-", StringComparison.Ordinal) || Path.GetFileName(Path.GetDirectoryName(root)) != "build" ||
            !File.Exists(Path.Combine(root, "account-acceptance.json"))) throw new ArgumentException("Only the existing isolated product acceptance state is authorized.");
        using var database = new ProcessorStateDatabase(root);
        await using var state = new ProcessorStateCoordinator(database);
        var sources = await state.ExecuteAsync(db => new LibrarySourceRepository(db).Snapshot(), CancellationToken.None).ConfigureAwait(false);
        if (sources.Sources.Count != 1 || !sources.Sources[0].Enabled || sources.Sources[0].FolderId != args[3]) throw new InvalidOperationException("Only the explicitly identified disposable source may be used.");
        await using var native = new ProcessorWorkSupervisor();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var runtime = await DesktopWebRuntime.StartAsync(state, database.InstallationId, root, native, args[0], args[2], enableLan: false, deadline.Token).ConfigureAwait(false);
        if (runtime.Host.ListeningEndpoints.Any(endpoint => endpoint.Host != "127.0.0.1")) throw new InvalidOperationException("Acceptance requires loopback-only listeners.");
        using (var first = Client(runtime.Host.LocalEndpoint))
        using (var other = Client(runtime.Host.LocalEndpoint))
        {
            var csrf = await LoginAsync(first, runtime.Host.OpenLocalBrowser(), deadline.Token).ConfigureAwait(false);
            var page = await first.GetFromJsonAsync<DesktopCatalogPage>("api/catalog", deadline.Token).ConfigureAwait(false) ?? throw new InvalidDataException("Catalog response is missing.");
            if (page.Photos.Count != 1 || page.Photos[0].Name != "ocr-latin.png") throw new InvalidOperationException("The selected-source fixture must contain only the synthetic OCR image.");
            var request = new CatalogViewerRequest(Guid.NewGuid(), page.Photos[0].Id, null);
            var opened = await OpenAsync(first, request, csrf, deadline.Token).ConfigureAwait(false);
            if (opened.Count != 1 || opened.Entries.Single().AssetId != request.SelectedId || opened.Entries[0].Photo is null) throw new InvalidDataException("The frozen viewer changed its selected identity.");
            var reopenedCsrf = await LoginAsync(first, runtime.Host.OpenLocalBrowser(), deadline.Token).ConfigureAwait(false);
            if (csrf != reopenedCsrf) throw new InvalidOperationException("Reopening the same browser stranded its existing controls.");
            var replayed = await OpenAsync(first, request, csrf, deadline.Token).ConfigureAwait(false);
            if (replayed.Id != opened.Id || replayed.Count != opened.Count || replayed.Position != opened.Position ||
                !replayed.Entries.Select(entry => entry.AssetId).SequenceEqual(opened.Entries.Select(entry => entry.AssetId)))
                throw new InvalidOperationException("An identical viewer request did not retain its session.");
            var otherCsrf = await LoginAsync(other, runtime.Host.OpenLocalBrowser(), deadline.Token).ConfigureAwait(false);
            using (var denied = await other.GetAsync("api/viewers?viewer=" + opened.Id.ToString("D") + "&position=0", deadline.Token).ConfigureAwait(false))
                if (denied.StatusCode != HttpStatusCode.NotFound) throw new InvalidOperationException("Another browser could read the first browser's viewer.");
            var independent = await OpenAsync(other, request with { Id = Guid.NewGuid() }, otherCsrf, deadline.Token).ConfigureAwait(false);
            using (var preview = await first.GetAsync("api/catalog-preview?asset=" + request.SelectedId.ToString("D"), deadline.Token).ConfigureAwait(false))
            {
                preview.EnsureSuccessStatusCode(); var bytes = await preview.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
                if (bytes.Length is < 1 or > 4 * 1024 * 1024 || preview.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp")) throw new InvalidDataException("An actual bounded thumbnail is required.");
            }
            await SignOutAsync(first, csrf, deadline.Token).ConfigureAwait(false);
            using (var locked = await first.GetAsync("api/status", deadline.Token).ConfigureAwait(false))
                if (locked.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("Sign-out did not revoke browser access.");
            var unaffected = await other.GetFromJsonAsync<DesktopViewerWindow>("api/viewers?viewer=" + independent.Id.ToString("D") + "&position=0", deadline.Token).ConfigureAwait(false);
            if (unaffected?.Entries.Single().AssetId != request.SelectedId) throw new InvalidOperationException("One browser's sign-out affected another viewer.");
            await SignOutAsync(other, otherCsrf, deadline.Token).ConfigureAwait(false);
        }
        await runtime.DisposeAsync().ConfigureAwait(false);
        if (!runtime.ExitConfirmed || runtime.Failure is not null || runtime.Host.FrameworkDiagnostics.Count != 0 || Directory.Exists(Path.Combine(root, "viewer-sessions")))
            throw new InvalidOperationException("The actual engine did not confirm clean viewer/account shutdown.");
        await File.WriteAllBytesAsync(args[4], JsonSerializer.SerializeToUtf8Bytes(new { observedAtUtc = DateTimeOffset.UtcNow,
            scope = "Native protected-account/HTTP acceptance only; actual Chrome UI and final joint release acceptance remain separate",
            actualSelectedSourceCatalog = true, actualBoundedThumbnail = true, immutableViewerIdentity = true, sameBrowserReopen = true,
            independentBrowserIsolation = true, signOutCleanup = true, joinedEngineExit = true, generatedIndexesRemoved = true,
            originalDownloaded = false, driveWritesPerformed = false, newConsentOccurred = false, frameworkDiagnostics = 0 }), deadline.Token).ConfigureAwait(false);
        Console.WriteLine("Native protected-account, actual HTTP viewer/thumbnail, browser isolation and joined cleanup passed; no credentials or media identities were logged.");
    }
    private static HttpClient Client(Uri endpoint) => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = endpoint, Timeout = TimeSpan.FromMinutes(2) };
    private static async Task<string> LoginAsync(HttpClient http, BrowserInvitation invitation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/session") { Content = JsonContent.Create(new { secret = invitation.Secret }) };
        request.Headers.Add("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false); return body.GetProperty("csrf").GetString() ?? throw new InvalidDataException("Browser CSRF is missing.");
    }
    private static async Task<DesktopViewerWindow> OpenAsync(HttpClient http, CatalogViewerRequest viewer, string csrf, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/viewers") { Content = JsonContent.Create(viewer) };
        request.Headers.Add("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority)); request.Headers.Add("X-MPA-CSRF", csrf);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopViewerWindow>(cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Viewer response is missing.");
    }
    private static async Task SignOutAsync(HttpClient http, string csrf, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "api/session") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Origin", http.BaseAddress!.GetLeftPart(UriPartial.Authority)); request.Headers.Add("X-MPA-CSRF", csrf);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
    }
}
