using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.DriveProbes;

/// <summary>Bounded, read-only discovery in the separately selected disposable folder.</summary>
internal sealed class DriveReadProbe(UserCredential credential)
{
    private const int MaximumResponseBytes = 64 * 1024;

    public async Task RunAsync(string folderId, string reportDirectory, CancellationToken cancellationToken)
    {
        if (folderId.Length is < 1 or > 256 ||
            folderId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Invalid disposable folder identity", nameof(folderId));

        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        var token = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var folder = await ReadAsync(client, token,
            "files/" + folderId + "?supportsAllDrives=true&fields=id,mimeType,trashed,ownedByMe,capabilities(canListChildren)",
            allowInvisible: true, cancellationToken).ConfigureAwait(false);
        var query = Uri.EscapeDataString("'" + folderId + "' in parents and trashed = false");
        var children = await ReadAsync(client, token,
            "files?q=" + query + "&pageSize=10&supportsAllDrives=true&includeItemsFromAllDrives=true&fields=files(id,mimeType),nextPageToken",
            allowInvisible: false, cancellationToken).ConfigureAwait(false);
        if (folder.Status == 200 &&
            (folder.Body.GetProperty("id").GetString() != folderId ||
             folder.Body.GetProperty("mimeType").GetString() != "application/vnd.google-apps.folder" ||
             folder.Body.GetProperty("trashed").GetBoolean()))
            throw new InvalidDataException("Selected test folder identity/type/visibility invariant failed");
        if (!children.Body.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Drive did not return the requested file-list contract");
        var observation = new
        {
            operation = "read-only-disposable-folder", observedAtUtc = DateTimeOffset.UtcNow,
            grantedScopes = credential.Token.Scope,
            folderStatus = folder.Status, folderFailureReasons = folder.Reasons,
            folderVisible = folder.Status == 200, childrenStatus = children.Status,
            visibleChildren = files.GetArrayLength(),
            moreChildren = children.Body.TryGetProperty("nextPageToken", out _),
            writesPerformed = false,
            scope = "Permission visibility observation only; not recursive/cross-client/write acceptance"
        };
        var path = Path.Combine(reportDirectory, "inspect-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(observation), cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Read-only Drive observation recorded: folder HTTP {folder.Status}, child list HTTP {children.Status}, {files.GetArrayLength()} visible children. No writes performed.");
    }

    internal static async Task<ReadResult> ReadAsync(HttpClient client, string token, string relativePath,
        bool allowInvisible, CancellationToken cancellationToken)
    {
        var address = new Uri("https://www.googleapis.com/drive/v3/" + relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Drive probe response exceeds its bound");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaximumResponseBytes)
                throw new InvalidDataException("Drive probe response exceeds its bound");
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        var body = document.RootElement.Clone();
        var reasons = Array.Empty<string>();
        if (!response.IsSuccessStatusCode)
        {
            if (body.TryGetProperty("error", out var error) && error.TryGetProperty("errors", out var errors))
                reasons = errors.EnumerateArray().Select(item => item.GetProperty("reason").GetString() ?? "missing-reason").ToArray();
            var invisible = response.StatusCode == HttpStatusCode.NotFound ||
                (response.StatusCode == HttpStatusCode.Forbidden && reasons.Length > 0 &&
                 reasons.All(reason => reason is "insufficientPermissions" or "insufficientFilePermissions"));
            if (!allowInvisible || !invisible)
                throw new HttpRequestException($"Drive read probe failed with HTTP {(int)response.StatusCode}; no success or empty library is inferred.", null, response.StatusCode);
        }
        return new ReadResult((int)response.StatusCode, body, reasons, response.Headers.ETag?.ToString());
    }

    internal sealed record ReadResult(int Status, JsonElement Body, string[] Reasons, string? Etag);
}
