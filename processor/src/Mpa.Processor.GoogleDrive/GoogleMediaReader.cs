using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveMediaFile(string Id, string Name, string MimeType, int? Width, int? Height, string? CapturedTime, string? ModifiedTime);
public sealed record DriveMediaPage(IReadOnlyList<DriveMediaFile> Files, string? NextPageToken, bool IncompleteSearch);
public sealed record DriveMediaPreview(byte[] Bytes, string MimeType);

public sealed partial class GoogleAccountConnection
{
    private bool _mediaIdentityVerified;

    /// <summary>Read-only discovery slice, not the canonical capture-time catalog. Page size
    /// and selected fields are fixed; short-lived Google thumbnail URLs never reach clients.</summary>
    public Task<DriveMediaPage> ReadMediaPageAsync(string? pageToken, CancellationToken cancellationToken)
    {
        if (pageToken is not null && (pageToken.Length is < 1 or > 4096 || pageToken.Any(char.IsControl))) throw new ArgumentException("Invalid Drive page token.", nameof(pageToken));
        return ExecuteAsync(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var query = "trashed = false and (mimeType contains 'image/' or mimeType contains 'video/')";
            var uri = "https://www.googleapis.com/drive/v3/files?pageSize=60&spaces=drive&orderBy=modifiedTime%20desc,name" +
                "&q=" + Uri.EscapeDataString(query) + "&fields=nextPageToken,incompleteSearch,files(id,name,mimeType,modifiedTime,imageMediaMetadata(width,height,time),videoMediaMetadata(width,height))" +
                (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var bytes = await MediaGetAsync(credential, new(uri), 256 * 1024, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var next = root.TryGetProperty("nextPageToken", out var nextValue) ? Text(nextValue, 4096) : null;
            var files = new List<DriveMediaFile>();
            foreach (var file in root.GetProperty("files").EnumerateArray())
            {
                if (files.Count == 60) throw new InvalidDataException("Drive returned more media than the requested page bound.");
                files.Add(MediaFile(file));
            }
            return new DriveMediaPage(files.AsReadOnly(), next, root.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean());
        }, cancellationToken);
    }

    public Task<DriveMediaPreview?> ReadMediaPreviewAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(fileId)) throw new ArgumentException("Invalid media identity.", nameof(fileId));
        return ExecuteAsync<DriveMediaPreview?>(async token =>
        {
            var credential = await MediaCredentialAsync(token).ConfigureAwait(false);
            var bytes = await MediaGetAsync(credential, new("https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(fileId) + "?fields=id,trashed,thumbnailLink"), 16384, token).ConfigureAwait(false);
            using var metadata = JsonDocument.Parse(bytes);
            var root = metadata.RootElement;
            if (Text(root.GetProperty("id"), 256) != fileId) throw new InvalidDataException("Drive preview metadata returned a different media identity.");
            if (root.TryGetProperty("trashed", out var trashed) && trashed.GetBoolean() || !root.TryGetProperty("thumbnailLink", out var link)) return null;
            if (!Uri.TryCreate(Text(link, 4096), UriKind.Absolute, out var thumbnail) || thumbnail.Scheme != Uri.UriSchemeHttps ||
                thumbnail.Host != "lh3.googleusercontent.com" || !thumbnail.IsDefaultPort || thumbnail.UserInfo.Length != 0 ||
                thumbnail.Fragment.Length != 0)
                throw new InvalidDataException("Drive returned an unsupported thumbnail origin. No credential was sent to it.");
            var preview = await MediaGetAsync(credential, thumbnail, 4 * 1024 * 1024, token).ConfigureAwait(false);
            var type = PreviewType(preview) ?? throw new InvalidDataException("Drive returned unsupported thumbnail bytes.");
            return new(preview, type);
        }, cancellationToken);
    }

    private async Task<UserCredential> MediaCredentialAsync(CancellationToken token)
    {
        var grant = await _flow.LoadTokenAsync(_store.UserId, token).ConfigureAwait(false)
            ?? throw new HttpRequestException("Google account requires reconnection.", null, HttpStatusCode.Unauthorized);
        var credential = new UserCredential(_flow, _store.UserId, grant);
        if (!_mediaIdentityVerified) { await ReadIdentityAsync(credential, token).ConfigureAwait(false); _mediaIdentityVerified = true; }
        return credential;
    }
    private async Task<byte[]> MediaGetAsync(UserCredential credential, Uri uri, int maximum, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessOwnedAsync(credential,token).ConfigureAwait(false));
            using var response = await SendOwnedAsync(request,token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await RefreshOwnedAsync(credential,token).ConfigureAwait(false)) continue;
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Drive media request failed with HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".", null, response.StatusCode);
            CheckNetworkActive(token);
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Drive media response exceeds its bound.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            { if (output.Length + count > maximum) throw new InvalidDataException("Drive media response exceeds its bound."); output.Write(buffer, 0, count); }
            return output.ToArray();
        }
        throw new InvalidOperationException("Drive media authorization retry was exhausted.");
    }
    private static string Text(JsonElement value, int maximum) => value.GetString() is { } text && text.Length <= maximum ? text : throw new InvalidDataException("Drive media text exceeds its bound.");
    private static DriveMediaFile MediaFile(JsonElement file)
    {
        var id = Text(file.GetProperty("id"), 256);
        if (!ValidIdentity(id)) throw new InvalidDataException("Drive returned an invalid media identity.");
        var mime = Text(file.GetProperty("mimeType"), 256);
        if (!mime.StartsWith("image/", StringComparison.Ordinal) && !mime.StartsWith("video/", StringComparison.Ordinal)) throw new InvalidDataException("Drive media query returned an unsupported media type.");
        var image = file.TryGetProperty("imageMediaMetadata", out var imageValue) ? imageValue : default;
        var metadata = image.ValueKind == JsonValueKind.Object ? image : file.TryGetProperty("videoMediaMetadata", out var video) ? video : default;
        return new(id, Text(file.GetProperty("name"), 4096), mime, Dimension(metadata, "width"), Dimension(metadata, "height"),
            image.ValueKind == JsonValueKind.Object && image.TryGetProperty("time", out var captured) ? Text(captured, 128) : null,
            file.TryGetProperty("modifiedTime", out var modified) ? Text(modified, 128) : null);
    }
    private static int? Dimension(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        ? value.TryGetInt32(out var dimension) && dimension is > 0 and <= 1000000 ? dimension : null : null;
    private static string? PreviewType(ReadOnlySpan<byte> bytes) => bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 255, 216, 255 }) ? "image/jpeg" :
        bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png" :
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp" : null;
}
