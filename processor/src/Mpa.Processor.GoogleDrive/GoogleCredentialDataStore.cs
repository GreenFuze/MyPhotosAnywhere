using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Mpa.Processor.Credentials;

namespace Mpa.Processor.GoogleDrive;

/// <summary>A Google SDK data store scoped to exactly one connection. It never clears another
/// account or discards an expired access token's still-usable refresh credential.</summary>
public sealed class GoogleCredentialDataStore : IDataStore
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive";
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly ICredentialVault _vault;
    private readonly Func<CancellationToken> _cancellation;
    private readonly string _address;
    public string UserId { get; }

    public GoogleCredentialDataStore(ICredentialVault vault, Guid connectionId, Func<CancellationToken> cancellation)
    {
        ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(cancellation);
        if (connectionId == Guid.Empty) throw new ArgumentException("A connection identity is required.", nameof(connectionId));
        _vault = vault; _cancellation = cancellation;
        UserId = connectionId.ToString("N");
        _address = "google-oauth-v1:" + UserId;
    }

    public async Task StoreAsync<T>(string key, T value)
    {
        CheckKey<T>(key);
        if (value is not TokenResponse response) throw new ArgumentException("A Google token response is required.", nameof(value));
        var previous = await GetAsync<TokenResponse>(key).ConfigureAwait(false);
        // Refresh responses may omit an unchanged refresh token or scope. Only this same
        // connection's existing grant may supply them; a different connection cannot do so.
        var record = new StoredToken("mpa-google-token", 1, response.AccessToken,
            response.RefreshToken ?? previous?.RefreshToken, response.TokenType ?? previous?.TokenType,
            response.Scope ?? previous?.Scope, response.ExpiresInSeconds, response.IssuedUtc);
        Validate(record);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Options);
        try { await _vault.WriteAsync(_address, bytes, _cancellation()).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<T> GetAsync<T>(string key)
    {
        CheckKey<T>(key);
        using var secret = await _vault.ReadAsync(_address, _cancellation()).ConfigureAwait(false);
        if (secret is null) return default!;
        using var json = JsonDocument.Parse(secret.Bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 8 ||
            json.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 8)
            throw new InvalidDataException("Stored Google credential has an invalid record shape.");
        var record = json.RootElement.Deserialize<StoredToken>(Options) ?? throw new InvalidDataException("Stored Google credential is empty.");
        Validate(record);
        return (T)(object)new TokenResponse { AccessToken = record.AccessToken, RefreshToken = record.RefreshToken,
            TokenType = record.TokenType, Scope = record.Scope, ExpiresInSeconds = record.ExpiresInSeconds, IssuedUtc = record.IssuedUtc };
    }

    public Task DeleteAsync<T>(string key) { CheckKey<T>(key); return ClearAsync(); }
    public Task ClearAsync() => _vault.DeleteAsync(_address, _cancellation());

    private void CheckKey<T>(string key)
    {
        if (typeof(T) != typeof(TokenResponse) || key != UserId) throw new ArgumentException("This Google data store accepts only its own connection's token record.", nameof(key));
    }
    private static void Validate(StoredToken record)
    {
        if (record.Format != "mpa-google-token" || record.Version != 1 || !ValidToken(record.AccessToken) || !ValidToken(record.RefreshToken) ||
            !string.Equals(record.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) || record.ExpiresInSeconds is not (> 0 and <= 86400) ||
            record.IssuedUtc.Kind != DateTimeKind.Utc || record.IssuedUtc == default || record.Scope is null || record.Scope.Length > 8192 ||
            !record.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(DriveScope, StringComparer.Ordinal))
            throw new InvalidDataException("Google credential is incomplete, unsupported or lacks the requested Drive grant. Existing protected credentials were preserved.");
    }
    private static bool ValidToken(string? token) => token is { Length: > 0 and <= 32768 } && token.All(character => character is >= '!' and <= '~');
    private sealed record StoredToken(string Format, int Version, string? AccessToken, string? RefreshToken,
        string? TokenType, string? Scope, long? ExpiresInSeconds, DateTime IssuedUtc);
}
