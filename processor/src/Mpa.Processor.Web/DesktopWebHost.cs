using System.Net;
using System.Security.Authentication;
using Mpa.Processor.AppServices;
using Mpa.Processor.GoogleDrive;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mpa.Processor.Lan;
using Mpa.Processor.Core;
using Google.Apis.Auth.OAuth2.Responses;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace Mpa.Processor.Web;

/// <summary>Owns listeners and browser sessions, borrows the engine/identity. Closing a tab
/// does not stop this owner. Dispose joins request handlers before releasing borrowed owners.</summary>
public sealed class DesktopWebHost : IAsyncDisposable
{
    private const string CookieName = "mpa_browser";
    private readonly WebApplication _host;
    private readonly BrowserSessionAuthority _access = new();
    private readonly IDesktopWebApplication _application;
    private readonly HashSet<string> _endpoints = new(StringComparer.Ordinal);
    private readonly string _assets;
    private readonly WebDiagnostics _diagnostics;
    private readonly object _gate = new();
    private Task? _disposal;
    private int _requests;
    private bool _stopping;
    private bool _exitConfirmed;
    private readonly TaskCompletionSource _requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ExitConfirmed { get { lock (_gate) return _exitConfirmed; } }
    public Uri LocalEndpoint { get; private set; } = null!;
    public Uri? HttpsEndpoint { get; private set; }
    public IReadOnlyList<Uri> ListeningEndpoints { get; private set; } = [];
    public event Action<Exception>? Diagnostic;
    public IReadOnlyList<Exception> FrameworkDiagnostics => _diagnostics.Failures;

    private DesktopWebHost(WebApplication host, IDesktopWebApplication application, string assets, WebDiagnostics diagnostics)
    {
        _host = host; _application = application; _assets = assets; _diagnostics = diagnostics;
        diagnostics.Reported += error => Diagnostic?.Invoke(error);
    }

    public static async Task<DesktopWebHost> StartAsync(IDesktopWebApplication application, string assetsDirectory,
        ProcessorLanIdentity? lanIdentity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        var assets = Path.GetFullPath(assetsDirectory);
        if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("The complete MPA web interface is missing from this package.");
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = typeof(DesktopWebHost).Assembly.FullName,
            ContentRootPath = assets, EnvironmentName = Environments.Production });
        var diagnostics = new WebDiagnostics();
        builder.Logging.AddProvider(diagnostics);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxRequestBodySize = 16384;
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            // Development includes the HTTPS acceptance fixture, but never a wildcard or
            // LAN listener. Remote binding is deferred until explicit LAN onboarding exists.
            if (lanIdentity is not null) options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(lanIdentity.ServerCertificate, https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
            });
        });
        var host = builder.Build(); var owner = new DesktopWebHost(host, application, assets, diagnostics);
        try
        {
            owner.MapRequests();
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            if (diagnostics.Failures.Count != 0) throw new AggregateException("Web host startup diagnostics are blocking.", diagnostics.Failures);
            var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("Web listener did not publish its actual bound endpoints.");
            var boundEndpoints = addresses.Select(address => new Uri(address)).ToArray();
            if (boundEndpoints.Any(endpoint => endpoint.Host != "127.0.0.1")) throw new InvalidOperationException("Development web listeners must bind exclusively to 127.0.0.1.");
            owner.ListeningEndpoints = Array.AsReadOnly(boundEndpoints);
            owner.LocalEndpoint = new(addresses.Single(address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
            owner._endpoints.Add(owner.LocalEndpoint.GetLeftPart(UriPartial.Authority));
            if (lanIdentity is not null)
            {
                var bound = new Uri(addresses.Single(address => address.StartsWith("https://", StringComparison.Ordinal)));
                owner.HttpsEndpoint = new UriBuilder(Uri.UriSchemeHttps, lanIdentity.DnsName, bound.Port).Uri;
                owner._endpoints.Add(owner.HttpsEndpoint.GetLeftPart(UriPartial.Authority));
            }
            return owner;
        }
        catch { await owner.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public BrowserInvitation OpenLocalBrowser() => _access.Invite(LocalEndpoint, hostOwner: true);
    public BrowserInvitation InviteBrowser() => _access.Invite(HttpsEndpoint ?? throw new InvalidOperationException("HTTPS access is not enabled."), hostOwner: false);

    private void MapRequests()
    {
        _host.Run(async context =>
        {
            lock (_gate)
            {
                if (_stopping) { context.Response.StatusCode = 503; return; }
                _requests = checked(_requests + 1);
            }
            try
            {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            try
            {
                var endpoint = RequestEndpoint(context);
                if (endpoint is null) { context.Response.StatusCode = 403; return; }
                var path = context.Request.Path.Value;
                var mutation = context.Request.Method != "GET";
                if (mutation && (!SameOrigin(context, endpoint) || !context.Request.HasJsonContentType()))
                { context.Response.StatusCode = 403; return; }
                if (path == "/api/session" && context.Request.Method == "POST")
                {
                    var body = await context.Request.ReadFromJsonAsync<SessionRequest>(context.RequestAborted).ConfigureAwait(false);
                    var grant = _access.Exchange(body?.Secret ?? "", endpoint, context.Request.Cookies[CookieName]);
                    context.Response.Cookies.Append(CookieName, grant.Cookie, CookieOptions(endpoint));
                    var approved = _access.Authenticate(grant.Cookie, endpoint, null, false);
                    await context.Response.WriteAsJsonAsync(new { csrf = grant.Csrf, hostOwner = approved.HostOwner }, context.RequestAborted).ConfigureAwait(false); return;
                }
                if (path?.StartsWith("/api/", StringComparison.Ordinal) == true)
                {
                    if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") { context.Response.StatusCode = 403; return; }
                    var session = _access.Authenticate(context.Request.Cookies[CookieName], endpoint, context.Request.Headers["X-MPA-CSRF"], mutation);
                    await ApiAsync(context, session).ConfigureAwait(false); return;
                }
                if (context.Request.Method != "GET") { context.Response.StatusCode = 405; return; }
                // Explicit allowlist; no user-supplied path is ever mapped to the filesystem.
                var asset = path switch { "/" => ("index.html", "text/html; charset=utf-8"), "/app.js" => ("app.js", "text/javascript; charset=utf-8"),
                    "/app.css" => ("app.css", "text/css; charset=utf-8"), "/mpa.svg" => ("mpa.svg", "image/svg+xml"), _ => (null, null) };
                if (asset.Item1 is null) { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = asset.Item2;
                await context.Response.SendFileAsync(Path.Combine(_assets, asset.Item1), context.RequestAborted).ConfigureAwait(false);
            }
            catch (BrowserAuthenticationException) { if (!context.Response.HasStarted) context.Response.StatusCode = 401; }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { /* Kestrel joins this read; no durable work is canceled. */ }
            catch (BadHttpRequestException error) { if (!context.Response.HasStarted) context.Response.StatusCode = error.StatusCode; }
            catch (System.Text.Json.JsonException) { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
            catch (StaleDriveConnectionException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "This account changed. Refresh settings before trying again." }, context.RequestAborted).ConfigureAwait(false); }
            catch (DriveAccountAlreadyConnectedException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "This Google account already exists. Reconnect its existing entry." }, context.RequestAborted).ConfigureAwait(false); }
            catch (StaleLibraryConfigurationException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "Library settings changed. Refresh Sources before applying this change." }, context.RequestAborted).ConfigureAwait(false); }
            catch (ExistingLibrarySourceException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "This account and folder are already configured. Update their existing source entry." }, context.RequestAborted).ConfigureAwait(false); }
            catch (ManagedWorkspaceSourceException error)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (SourceWorkspaceLimitException error)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (LibraryWorkspaceApprovalException error)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (DriveWorkspaceSelectionException error)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (DriveWorkspaceObjectChangedException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "The metadata folder changed or is no longer available. Choose it again; saved work was retained." }, context.RequestAborted).ConfigureAwait(false); }
            catch (ControllerAuthorityConflictException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "This desktop already has a different approved library workspace. Its saved identity was retained." }, context.RequestAborted).ConfigureAwait(false); }
            catch (LibraryBootstrapApprovalConflictException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "The original source approval was retained. Review later changes in Sources." }, context.RequestAborted).ConfigureAwait(false); }
            catch (ProcessorWorkStateConflictException)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = "Processing state changed. Refresh Activity before trying again." }, context.RequestAborted).ConfigureAwait(false); }
            catch (InvalidDesktopCatalogQueryException error)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (CatalogSessionSelectionUnavailableException error)
            { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (CatalogViewerUnavailableException error)
            { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (CatalogViewerCapacityException error)
            { context.Response.StatusCode = 429; await context.Response.WriteAsJsonAsync(new { message = error.Message }, context.RequestAborted).ConfigureAwait(false); }
            catch (InsufficientStorageBudgetException)
            { context.Response.StatusCode = 507; await context.Response.WriteAsJsonAsync(new { message = "Free space on the computer running this library, then retry. Existing photos and progress were retained." }, context.RequestAborted).ConfigureAwait(false); }
            catch (TokenResponseException)
            { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { message = "Google authorization could not finish. Reconnect or resume account setup on the host computer." }, context.RequestAborted).ConfigureAwait(false); }
            catch (HttpRequestException)
            {
                if (!context.Response.HasStarted)
                { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { message = "Google Drive is unavailable. Your library and saved work were preserved." }, context.RequestAborted).ConfigureAwait(false); }
            }
            catch (Exception error)
            {
                Diagnostic?.Invoke(error);
                if (context.Response.HasStarted) context.Abort();
                else { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { message = "MPA encountered an unexpected failure. Open host diagnostics; existing photos and saved work were preserved." }, context.RequestAborted).ConfigureAwait(false); }
            }
            }
            finally
            {
                lock (_gate) { _requests--; if (_stopping && _requests == 0) _requestsDrained.TrySetResult(); }
            }
        });
    }

    private async Task ApiAsync(HttpContext context, BrowserSession session)
    {
        var path = context.Request.Path.Value; var token = context.RequestAborted;
        switch (context.Request.Method, path)
        {
            case ("GET", "/api/session"):
                await context.Response.WriteAsJsonAsync(new { csrf = session.Csrf, hostOwner = session.HostOwner }, token).ConfigureAwait(false); break;
            case ("DELETE", "/api/session"):
                _access.Revoke(session.Id); await _application.CloseBrowserViewersAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
                context.Response.Cookies.Delete(CookieName, CookieOptions(RequestEndpoint(context)!)); context.Response.StatusCode = 204; break;
            case ("GET", "/api/status"):
                await context.Response.WriteAsJsonAsync(await _application.SnapshotAsync(token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("GET", "/api/catalog"):
                var filter = OptionalQuery(context, "account", 256);
                if (filter is not null && !Identity(filter)) throw new BadHttpRequestException("Invalid account filter.");
                var direction = OptionalQuery(context, "direction", 5);
                if (direction is not (null or "older" or "newer")) throw new BadHttpRequestException("Invalid gallery direction.");
                var rawDate = OptionalQuery(context, "date", 16); long? date = null;
                if (rawDate is not null)
                {
                    if (!long.TryParse(rawDate, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var dateValue) || dateValue.ToString(System.Globalization.CultureInfo.InvariantCulture) != rawDate)
                        throw new BadHttpRequestException("Invalid gallery date.");
                    date = dateValue;
                }
                await context.Response.WriteAsJsonAsync(await _application.CatalogAsync(filter, OptionalQuery(context, "cursor", 340), direction == "newer", date, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("GET", "/api/catalog-preview"):
                if (!Guid.TryParseExact(RequiredQuery(context, "asset"), "D", out var assetId) || assetId == Guid.Empty) throw new BadHttpRequestException("Invalid media identity.");
                await WritePreviewAsync(context, await _application.CatalogPreviewAsync(assetId, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("POST", "/api/viewers"):
                var viewer = await context.Request.ReadFromJsonAsync<CatalogViewerRequest>(token).ConfigureAwait(false) ?? throw new BadHttpRequestException("A captured viewer request is required.");
                if (viewer.Id == Guid.Empty || viewer.SelectedId == Guid.Empty || viewer.AccountId is not null && !Identity(viewer.AccountId)) throw new BadHttpRequestException("Invalid viewer context.");
                _access.Authenticate(context.Request.Cookies[CookieName], RequestEndpoint(context)!, context.Request.Headers["X-MPA-CSRF"], mutation: true);
                var opened = await _application.OpenViewerAsync(session.Id, viewer, token).ConfigureAwait(false);
                try { _access.Authenticate(context.Request.Cookies[CookieName], RequestEndpoint(context)!, null, mutation: false); }
                catch (BrowserAuthenticationException)
                {
                    try { await _application.CloseViewerAsync(session.Id, viewer.Id, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cleanup) { Diagnostic?.Invoke(cleanup); }
                    throw;
                }
                await context.Response.WriteAsJsonAsync(opened, token).ConfigureAwait(false); break;
            case ("GET", "/api/viewers"):
                var viewerId = ViewerId(context); var rawPosition = RequiredQuery(context, "position");
                if (!long.TryParse(rawPosition, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position) || position.ToString(System.Globalization.CultureInfo.InvariantCulture) != rawPosition)
                    throw new BadHttpRequestException("Invalid viewer position.");
                await context.Response.WriteAsJsonAsync(await _application.ReadViewerAsync(session.Id, viewerId, position, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("DELETE", "/api/viewers"):
                await _application.CloseViewerAsync(session.Id, ViewerId(context), token).ConfigureAwait(false); context.Response.StatusCode = 204; break;
            case ("GET", "/api/sources"):
                await context.Response.WriteAsJsonAsync(await _application.SourcesAsync(token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("GET", "/api/library"):
                if (!session.HostOwner) { context.Response.StatusCode = 403; break; }
                await context.Response.WriteAsJsonAsync(new { controller = await _application.LibraryAsync(token).ConfigureAwait(false) }, token).ConfigureAwait(false); break;
            case ("GET", "/api/library-connection"):
                if (!session.HostOwner) { context.Response.StatusCode = 403; break; }
                var connection = (await _application.LibraryAsync(token).ConfigureAwait(false))?.ConnectionDescriptor();
                await context.Response.WriteAsJsonAsync(new { connection = connection is null ? null : new { code = connection.Encode(),
                    documentSha256 = connection.DocumentSha256, controllerKeyId = connection.ControllerKeyId } }, token).ConfigureAwait(false); break;
            case ("GET", "/api/library-publication"):
                if (!session.HostOwner) { context.Response.StatusCode = 403; break; }
                await context.Response.WriteAsJsonAsync(await _application.LibraryPublicationAsync(token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("POST", "/api/library-publication"):
                if (!session.HostOwner) { context.Response.StatusCode = 403; break; }
                await context.Response.WriteAsJsonAsync(await _application.RequestLibraryPublicationAsync(token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("GET", "/api/workspace-inspection"):
                if (!session.HostOwner) { context.Response.StatusCode = 403; break; }
                var workspaceAccount = RequiredQuery(context, "account"); var workspaceFolder = RequiredQuery(context, "folder");
                if (!Identity(workspaceAccount) || !Identity(workspaceFolder) || workspaceFolder == "root") throw new BadHttpRequestException("Choose a dedicated metadata workspace by its actual folder identity.");
                await context.Response.WriteAsJsonAsync(await _application.InspectWorkspaceAsync(workspaceAccount, workspaceFolder, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("POST", "/api/library"):
                if (!session.HostOwner) { context.Response.StatusCode = 403; break; }
                var approval = await context.Request.ReadFromJsonAsync<ApproveLibraryWorkspace>(token).ConfigureAwait(false) ?? throw new BadHttpRequestException("Workspace approval is required.");
                if (!Identity(approval.AccountId) || !Identity(approval.FolderId) || approval.FolderId == "root" || approval.AccountRevision < 1 || approval.FolderVersion < 1 ||
                    approval.ConfigurationRevision < 1 || approval.ControllerKeyId is not { Length: 64 } || approval.ControllerKeyId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                    throw new BadHttpRequestException("Invalid reviewed workspace approval.");
                await context.Response.WriteAsJsonAsync(new { controller = await _application.ApproveWorkspaceAsync(approval, token).ConfigureAwait(false) }, token).ConfigureAwait(false); break;
            case ("GET", "/api/activity"):
                var rawCursor = OptionalQuery(context, "cursor", 36); Guid? activityCursor = null;
                if (rawCursor is not null)
                {
                    if (!Guid.TryParseExact(rawCursor, "D", out var parsed) || parsed == Guid.Empty) throw new BadHttpRequestException("Invalid activity cursor.");
                    activityCursor = parsed;
                }
                await context.Response.WriteAsJsonAsync(await _application.ActivityAsync(activityCursor, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("POST", "/api/source-scans"):
                var scan = await context.Request.ReadFromJsonAsync<SourceScanRequest>(token).ConfigureAwait(false) ?? throw new BadHttpRequestException("A source scan request is required.");
                if (scan.RequestId == Guid.Empty || scan.SourceId == Guid.Empty || scan.SourceRevision < 1 || scan.AccountRevision < 1) throw new BadHttpRequestException("Invalid captured source scan.");
                await context.Response.WriteAsJsonAsync(await _application.RequestScanAsync(scan, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("POST", "/api/source-retries"):
                var retry = await context.Request.ReadFromJsonAsync<SourceRetryRequest>(token).ConfigureAwait(false) ?? throw new BadHttpRequestException("A captured retry request is required.");
                if (retry.JobId == Guid.Empty || retry.SourceId == Guid.Empty || retry.SourceRevision < 1 || retry.AccountRevision < 1) throw new BadHttpRequestException("Invalid captured source retry.");
                await context.Response.WriteAsJsonAsync(new { queued = await _application.RetrySourceAsync(retry, token).ConfigureAwait(false) }, token).ConfigureAwait(false); break;
            case ("GET", "/api/folders"):
                await context.Response.WriteAsJsonAsync(await _application.FoldersAsync(RequiredQuery(context, "account"), RequiredQuery(context, "folder"), OptionalQuery(context, "cursor", 4096), token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("POST", "/api/sources"):
                var addition = await context.Request.ReadFromJsonAsync<DesktopAddSource>(token).ConfigureAwait(false) ?? throw new BadHttpRequestException("Source selection is required.");
                if (!Identity(addition.AccountId) || !Identity(addition.FolderId) || addition.ExpectedRevision < 1) throw new BadHttpRequestException("Invalid captured source selection.");
                await context.Response.WriteAsJsonAsync(await _application.AddSourceAsync(addition, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("PUT", "/api/sources"):
                var change = await context.Request.ReadFromJsonAsync<DesktopUpdateSource>(token).ConfigureAwait(false) ?? throw new BadHttpRequestException("Source policy is required.");
                if (change.Id == Guid.Empty || change.ExpectedRevision < 1 || change.ExpectedSourceRevision < 1) throw new BadHttpRequestException("Invalid captured source policy.");
                await context.Response.WriteAsJsonAsync(await _application.UpdateSourceAsync(change, token).ConfigureAwait(false), token).ConfigureAwait(false); break;
            case ("POST", "/api/pause"):
                await _application.PauseAsync(token).ConfigureAwait(false); context.Response.StatusCode = 204; break;
            case ("POST", "/api/resume"):
                await _application.ResumeAsync(token).ConfigureAwait(false); context.Response.StatusCode = 204; break;
            case ("POST", "/api/accounts"):
                if (!session.HostOwner) { context.Response.StatusCode = 403; break; }
                var account = await context.Request.ReadFromJsonAsync<AccountRequest>(token).ConfigureAwait(false) ?? throw new BadHttpRequestException("An account operation is required.");
                var operation = account.Operation switch { "connect" => DesktopAccountOperation.Connect, "check" => DesktopAccountOperation.Check,
                    "disconnect" => DesktopAccountOperation.Disconnect, "resume" => DesktopAccountOperation.Resume, "abandon" => DesktopAccountOperation.Abandon,
                    "cleanup" => DesktopAccountOperation.Cleanup, _ => throw new BadHttpRequestException("Unknown account operation.") };
                if (account.AccountId is not null && !Identity(account.AccountId) || account.PendingId == Guid.Empty || account.ExpectedRevision < 1 ||
                    operation is DesktopAccountOperation.Check or DesktopAccountOperation.Disconnect && account.AccountId is null ||
                    operation == DesktopAccountOperation.Disconnect && account.ExpectedRevision is null ||
                    operation is DesktopAccountOperation.Resume or DesktopAccountOperation.Abandon && account.PendingId is null)
                    throw new BadHttpRequestException("Invalid captured account operation fields.");
                await _application.AccountAsync(new(operation, account.AccountId, account.PendingId, account.ExpectedRevision), token).ConfigureAwait(false);
                context.Response.StatusCode = 204; break;
            default: context.Response.StatusCode = 404; break;
        }
    }
    private static async Task WritePreviewAsync(HttpContext context, DesktopPreview? preview, CancellationToken cancellationToken)
    {
        if (preview is null) { context.Response.StatusCode = 404; return; }
        if (preview.Bytes.Length is < 1 or > 4 * 1024 * 1024 || preview.MimeType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new InvalidDataException("Preview provider returned an unsupported or oversized result.");
        context.Response.ContentType = preview.MimeType; await context.Response.Body.WriteAsync(preview.Bytes, cancellationToken).ConfigureAwait(false);
    }
    private static bool Identity(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static Guid ViewerId(HttpContext context) => Guid.TryParseExact(RequiredQuery(context, "viewer"), "D", out var id) && id != Guid.Empty ? id : throw new BadHttpRequestException("Invalid viewer session identity.");
    private static string RequiredQuery(HttpContext context, string key) => OptionalQuery(context, key, 256) is { } value && Identity(value) ? value : throw new BadHttpRequestException("Invalid query identity.");
    private static string? OptionalQuery(HttpContext context, string key, int maximum)
    {
        var values = context.Request.Query[key];
        if (values.Count > 1 || values.ToString().Length > maximum || values.ToString().Any(char.IsControl)) throw new BadHttpRequestException("Query field exceeds its bound.");
        return values.Count == 0 ? null : values.ToString();
    }
    private Uri? RequestEndpoint(HttpContext context)
    {
        if (!Uri.TryCreate(context.Request.Scheme + "://" + context.Request.Host + "/", UriKind.Absolute, out var endpoint) ||
            !_endpoints.Contains(endpoint.GetLeftPart(UriPartial.Authority))) return null;
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !PrivateAddress(remote)) return null;
        return endpoint;
    }
    internal static bool PrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }
    private static bool SameOrigin(HttpContext context, Uri endpoint) => context.Request.Headers.Origin.Count == 1 && context.Request.Headers.Origin == endpoint.GetLeftPart(UriPartial.Authority);
    private static CookieOptions CookieOptions(Uri endpoint) => new() { HttpOnly = true, Secure = endpoint.Scheme == Uri.UriSchemeHttps,
        SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true; if (_requests == 0) _requestsDrained.TrySetResult();
            return new(_disposal ??= StopAsync());
        }
    }
    private async Task StopAsync()
    {
        try { await _host.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            try
            {
                await _host.DisposeAsync().ConfigureAwait(false);
                // Host timeout/abort cannot release borrowed accounts while a handler
                // still owns a real read that ignores or has not joined cancellation.
                await _requestsDrained.Task.ConfigureAwait(false);
                lock (_gate) _exitConfirmed = true;
            }
            finally { _access.Dispose(); }
        }
    }
    private sealed record SessionRequest(string Secret);
    private sealed record AccountRequest(string Operation, string? AccountId, Guid? PendingId, long? ExpectedRevision);
}
