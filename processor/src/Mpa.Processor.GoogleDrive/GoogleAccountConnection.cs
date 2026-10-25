using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Mpa.Processor.Credentials;

namespace Mpa.Processor.GoogleDrive;

public sealed record ConnectedDriveAccount(Guid ConnectionId, string PermissionId, string DisplayName, string? EmailAddress);

public interface IGoogleAccountConnection : IAsyncDisposable
{
    Task<ConnectedDriveAccount> AuthorizeAsync(IAuthorizationBrowser browser, CancellationToken cancellationToken);
    Task<ConnectedDriveAccount?> CheckAsync(CancellationToken cancellationToken);
}

public interface IGoogleAccountConnectionFactory
{
    IGoogleAccountConnection Create(Guid connectionId, string? expectedPermissionId);
}

public sealed class GoogleAccountConnectionFactory(DesktopOAuthConfiguration configuration, ICredentialVault vault) : IGoogleAccountConnectionFactory
{
    public IGoogleAccountConnection Create(Guid connectionId, string? expectedPermissionId) => new GoogleAccountConnection(configuration, vault, connectionId, expectedPermissionId);
}

/// <summary>Owns one connection's SDK flow, browser callback and bounded read-only identity
/// requests. No caller receives a raw UserCredential or token. The vault is borrowed.</summary>
public sealed partial class GoogleAccountConnection : IGoogleAccountConnection, IGoogleLibraryConnection, IGoogleWorkspacePublication
{
    private readonly Guid _connectionId;
    private readonly string? _expectedPermissionId;
    private readonly GoogleCredentialDataStore _store;
    private readonly PkceGoogleAuthorizationCodeFlow _flow;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationToken _activeCancellation;
    private bool _stopping;
    private Task? _disposal;

    public GoogleAccountConnection(DesktopOAuthConfiguration configuration, ICredentialVault vault,
        Guid connectionId, string? expectedPermissionId = null) : this(configuration, vault, connectionId, expectedPermissionId, null) { }

    internal GoogleAccountConnection(DesktopOAuthConfiguration configuration, ICredentialVault vault,
        Guid connectionId, string? expectedPermissionId, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (connectionId == Guid.Empty) throw new ArgumentException("A connection identity is required.", nameof(connectionId));
        if (expectedPermissionId is not null && !ValidIdentity(expectedPermissionId)) throw new ArgumentException("Invalid captured Google account identity.", nameof(expectedPermissionId));
        _connectionId = connectionId; _expectedPermissionId = expectedPermissionId;
        _store = new GoogleCredentialDataStore(vault, connectionId, () => _activeCancellation);
        _flow = new PkceGoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = configuration.Secrets, Scopes = [GoogleCredentialDataStore.DriveScope], DataStore = _store
        });
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public Task<ConnectedDriveAccount> AuthorizeAsync(IAuthorizationBrowser browser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return ExecuteAsync(async token =>
        {
            // Never let an interactive retry overwrite an existing connection's credential
            // before account identity is verified. Reconnection uses a new pending connection
            // with the old permission ID, then promotes that new ID only after this check.
            var existing = await _flow.LoadTokenAsync(_store.UserId, token).ConfigureAwait(false);
            if (existing is not null) return await ReadIdentityAsync(new UserCredential(_flow, _store.UserId, existing), token).ConfigureAwait(false);
            var credential = await new AuthorizationCodeInstalledApp(_flow, new DesktopCodeReceiver(browser))
                .AuthorizeAsync(_store.UserId, token).ConfigureAwait(false);
            return await ReadIdentityAsync(credential, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<ConnectedDriveAccount?> CheckAsync(CancellationToken cancellationToken) => ExecuteAsync<ConnectedDriveAccount?>(async token =>
    {
        var grant = await _flow.LoadTokenAsync(_store.UserId, token).ConfigureAwait(false);
        if (grant is null) return null;
        return await ReadIdentityAsync(new UserCredential(_flow, _store.UserId, grant), token).ConfigureAwait(false);
    }, cancellationToken);

    public Task ForgetAsync(CancellationToken cancellationToken) => ExecuteAsync(async _ =>
    {
        // Local disconnect only. Do not revoke the shared Google client grant or touch media.
        await _store.ClearAsync().ConfigureAwait(false);
        return true;
    }, cancellationToken);

    private async Task<ConnectedDriveAccount> ReadIdentityAsync(UserCredential credential, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var access = await AccessOwnedAsync(credential,cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/drive/v3/about?fields=user(permissionId,displayName,emailAddress)");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await SendOwnedAsync(request,cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                if (!await RefreshOwnedAsync(credential,cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Google authorization needs reconnection; the protected grant was preserved.");
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Google Drive account check failed with HTTP " + (int)response.StatusCode + ". The account and library were preserved.", null, response.StatusCode);
            CheckNetworkActive(cancellationToken);
            if (response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("Google account response exceeds its bound.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[2048];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (body.Length + count > 16384) throw new InvalidDataException("Google account response exceeds its bound.");
                body.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
            var user = json.RootElement.GetProperty("user");
            var permissionId = user.GetProperty("permissionId").GetString();
            if (!ValidIdentity(permissionId)) throw new InvalidDataException("Google did not return a stable account identity.");
            if (_expectedPermissionId is not null && permissionId != _expectedPermissionId)
                throw new InvalidDataException("This authorization belongs to a different Google account. Existing source bindings and jobs were not changed.");
            var name = user.TryGetProperty("displayName", out var display) ? display.GetString() : null;
            var email = user.TryGetProperty("emailAddress", out var mail) ? mail.GetString() : null;
            if (name?.Length > 512 || email?.Length > 512) throw new InvalidDataException("Google account display fields exceed their bounds.");
            return new(_connectionId, permissionId!, name ?? "Google Drive account", email);
        }
        throw new InvalidOperationException("Google account authentication retry was exhausted.");
    }

    private static bool ValidIdentity(string? identity) => identity is { Length: > 0 and <= 256 } && identity.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    // CancelAsync dispatches linked-token callbacks asynchronously. The owner's
    // closed admission is authoritative immediately, even while token delivery lags.
    // Initiate SDK/HTTP tasks under that same gate; join already admitted work in
    // ExecuteAsync/DisposeCoreAsync, without starting a later request or refresh.
    private T AdmitNetwork<T>(Func<T> start,CancellationToken token)
    {
        lock(_gate)
        {
            if (_stopping) throw new OperationCanceledException("The Google connection closed network admission.",_stop.Token);
            token.ThrowIfCancellationRequested();return start();
        }
    }
    private void CheckNetworkActive(CancellationToken token)
    {
        lock(_gate)
        {
            if (_stopping) throw new OperationCanceledException("The Google connection retired before its next network step.",_stop.Token);
            token.ThrowIfCancellationRequested();
        }
    }
    private Task<HttpResponseMessage> SendOwnedAsync(HttpRequestMessage request,CancellationToken token)=>
        AdmitNetwork(()=>_http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token),token);
    private Task<string> AccessOwnedAsync(UserCredential credential,CancellationToken token)=>
        AdmitNetwork(()=>credential.GetAccessTokenForRequestAsync(cancellationToken:token),token);
    private Task<bool> RefreshOwnedAsync(UserCredential credential,CancellationToken token)=>
        AdmitNetwork(()=>credential.RefreshTokenAsync(token),token);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        await _serial.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
            _activeCancellation = deadline.Token;
            var result = await operation(deadline.Token).ConfigureAwait(false);
            // CancelAsync may still be dispatching linked-token callbacks when a
            // transport completes. Serialize result admission against shutdown,
            // rather than treating callback completion as the ownership boundary.
            lock (_gate)
            {
                if (_stopping) throw new OperationCanceledException("The account owner stopped before admitting this result.", _stop.Token);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
        }
        finally { _activeCancellation = default; _serial.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return new(_disposal ??= DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _flow.Dispose(); _http.Dispose(); }
        finally { _serial.Release(); _stop.Dispose(); }
    }
}
