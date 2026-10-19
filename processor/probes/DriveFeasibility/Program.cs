using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace Mpa.Processor.DriveProbes;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This Stage 0 credential probe requires Windows DPAPI; product vault adapters remain separate work.");
        if (args.Length != 4 || args[0] is not ("authorize" or "refresh" or "vault-check"))
            throw new ArgumentException("Usage: DriveFeasibility <authorize|refresh|vault-check> <desktop-client-json> <private-directory> <file|drive>");
        var scope = args[3] switch
        {
            "file" => "https://www.googleapis.com/auth/drive.file",
            "drive" => "https://www.googleapis.com/auth/drive",
            _ => throw new ArgumentException("Unsupported probe scope")
        };
        var root = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(root);
        var vault = new WindowsProtectedDataStore(Path.Combine(root, "vault-" + args[3]));
        if (args[0] == "vault-check")
        {
            await new ProtectedStoreProbe(vault).RunAsync().ConfigureAwait(false);
            Console.WriteLine("Synthetic credential-store round trip, ciphertext, tamper rejection and deletion checks passed.");
            return 0;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var clientStream = File.OpenRead(args[1]);
        if (clientStream.Length is <= 0 or > 16 * 1024)
            throw new InvalidDataException("Desktop client configuration exceeds the probe bound");
        var secrets = GoogleClientSecrets.FromStream(clientStream).Secrets;
        if (!secrets.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            throw new InvalidDataException("Expected Google desktop client configuration");
        using var flow = new PkceGoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = [scope],
            DataStore = vault
        });
        const string user = "stage-zero-account";
        UserCredential credential;
        if (args[0] == "authorize")
        {
            var receiver = new StateCheckingCodeReceiver(Path.Combine(root, "authorization-url.txt"));
            credential = await new AuthorizationCodeInstalledApp(flow, receiver)
                .AuthorizeAsync(user, deadline.Token).ConfigureAwait(false);
        }
        else
        {
            var token = await flow.LoadTokenAsync(user, deadline.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No protected authorization exists; interactive consent is required.");
            credential = new UserCredential(flow, user, token);
            if (!await credential.RefreshTokenAsync(deadline.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Google did not refresh the authorization.");
        }
        if (string.IsNullOrWhiteSpace(credential.Token.RefreshToken) ||
            string.IsNullOrWhiteSpace(credential.Token.AccessToken))
            throw new InvalidDataException("Authorization did not return durable refresh/access credentials");
        var granted = (credential.Token.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!granted.Contains(scope, StringComparer.Ordinal))
            throw new InvalidDataException("Google did not return the requested scope; do not infer permission from the request");
        var observation = new
        {
            operation = args[0], observedAtUtc = DateTimeOffset.UtcNow,
            grantedScopes = granted.Order(StringComparer.Ordinal).ToArray(),
            protectedRefreshTokenPresent = true,
            accessTokenLifetimeSeconds = credential.Token.ExpiresInSeconds,
            scope = "Authorization/short refresh only; not elapsed-time, Drive access or cross-client acceptance"
        };
        var report = Path.Combine(root, args[0] + "-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(report, JsonSerializer.SerializeToUtf8Bytes(observation), deadline.Token).ConfigureAwait(false);
        Console.WriteLine("Authorization probe completed; credentials were not logged. Inspect the private observation report.");
        return 0;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsProtectedDataStore : IDataStore
{
    private readonly string _root;
    private readonly byte[] _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("MPA Stage 0 credential probe v1"));

    public WindowsProtectedDataStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task StoreAsync<T>(string key, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
            var path = Address<T>(key);
            var pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await output.WriteAsync(encrypted).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                File.Move(pending, path, overwrite: true);
            }
            finally
            {
                File.Delete(pending);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<T> GetAsync<T>(string key)
    {
        var path = Address<T>(key);
        if (!File.Exists(path)) return default!;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("Protected credential record has invalid length");
        var encrypted = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(encrypted).ConfigureAwait(false);
        var plain = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("Protected credential record is empty");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        File.Delete(Address<T>(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.dpapi")) File.Delete(file);
        return Task.CompletedTask;
    }

    internal void CheckCiphertext<T>(string key, string marker)
    {
        var bytes = File.ReadAllBytes(Address<T>(key));
        if (bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0)
            throw new InvalidDataException("Synthetic token marker appeared in the protected file");
    }

    internal void CorruptSyntheticRecord<T>(string key)
    {
        using var file = new FileStream(Address<T>(key), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length < 1) throw new InvalidDataException("Synthetic record is empty");
        file.Position = file.Length - 1;
        var value = file.ReadByte();
        file.Position--;
        file.WriteByte((byte)(value ^ 1));
        file.Flush(flushToDisk: true);
    }

    private string Address<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var identity = typeof(T).FullName + ":" + key;
        return Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".dpapi");
    }
}

internal sealed class StateCheckingCodeReceiver(string reviewPath) : ICodeReceiver
{
    private readonly ReviewCodeReceiver _receiver = new(reviewPath);
    public string RedirectUri => _receiver.RedirectUri;

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken taskCancellationToken)
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        url.State = state;
        if (url is GoogleAuthorizationCodeRequestUrl google)
        {
            google.Prompt = "consent";
            google.IncludeGrantedScopes = "false";
        }
        try
        {
            var response = await _receiver.ReceiveCodeAsync(url, taskCancellationToken).ConfigureAwait(false);
            if (response.State is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(response.State)))
                throw new InvalidDataException("OAuth state did not match the outstanding request");
            return response;
        }
        finally
        {
            File.Delete(reviewPath);
        }
    }
}

internal sealed class ReviewCodeReceiver(string reviewPath) : LocalServerCodeReceiver(
    "<html><head><title>MPA authorization</title></head><body>MPA received the response. You may close this page.</body></html>",
    LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp)
{
    protected override bool OpenBrowser(string url)
    {
        var address = new Uri(url);
        if (address.Scheme != "https" || address.Host != "accounts.google.com")
            throw new InvalidDataException("Unexpected authorization destination");
        using var output = new FileStream(reviewPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(url);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        Console.WriteLine("Authorization URL prepared privately; browser consent is required. No grant is assumed.");
        return true;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ProtectedStoreProbe(WindowsProtectedDataStore vault)
{
    public async Task RunAsync()
    {
        var key = "synthetic-check-" + Guid.NewGuid().ToString("N");
        var marker = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var record = new TokenResponse { AccessToken = marker, RefreshToken = marker, ExpiresInSeconds = 3600,
            Scope = "synthetic", IssuedUtc = DateTime.UtcNow };
        try
        {
            await vault.StoreAsync(key, record).ConfigureAwait(false);
            vault.CheckCiphertext<TokenResponse>(key, marker);
            var restored = await vault.GetAsync<TokenResponse>(key).ConfigureAwait(false);
            if (restored.AccessToken != marker || restored.RefreshToken != marker || restored.ExpiresInSeconds != 3600 ||
                restored.IssuedUtc != record.IssuedUtc)
                throw new InvalidDataException("Protected token round trip failed");
            if (await vault.GetAsync<string>(key).ConfigureAwait(false) is not null)
                throw new InvalidDataException("Protected record type isolation failed");
            vault.CorruptSyntheticRecord<TokenResponse>(key);
            var rejected = false;
            try { await vault.GetAsync<TokenResponse>(key).ConfigureAwait(false); }
            catch (CryptographicException) { rejected = true; }
            if (!rejected) throw new InvalidDataException("Corrupted credential was accepted");
            await vault.DeleteAsync<TokenResponse>(key).ConfigureAwait(false);
            if (await vault.GetAsync<TokenResponse>(key).ConfigureAwait(false) is not null)
                throw new InvalidDataException("Protected token deletion failed");
        }
        finally
        {
            await vault.DeleteAsync<TokenResponse>(key).ConfigureAwait(false);
        }
    }
}
