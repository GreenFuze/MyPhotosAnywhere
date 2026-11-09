using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.GoogleDrive;

/// <summary>Installed-client identity supplied by packaging, never a user's refresh grant.</summary>
public sealed class DesktopOAuthConfiguration
{
    internal ClientSecrets Secrets { get; }
    private DesktopOAuthConfiguration(ClientSecrets secrets) => Secrets = secrets;

    public static DesktopOAuthConfiguration Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 16384) throw new InvalidDataException("Desktop OAuth configuration exceeds its bound.");
        using var json = JsonDocument.Parse(stream);
        if (!json.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object ||
            json.RootElement.TryGetProperty("web", out _)) throw new InvalidDataException("An installed desktop OAuth client is required.");
        var id = installed.GetProperty("client_id").GetString();
        var secret = installed.GetProperty("client_secret").GetString();
        if (id is not { Length: > 0 and <= 256 } || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) ||
            secret is not { Length: > 0 and <= 256 } || id.Any(char.IsControl) || secret.Any(char.IsControl))
            throw new InvalidDataException("Invalid desktop OAuth client identity.");
        return new(new ClientSecrets { ClientId = id, ClientSecret = secret });
    }
    public override string ToString() => "[MPA desktop OAuth client]";
}
