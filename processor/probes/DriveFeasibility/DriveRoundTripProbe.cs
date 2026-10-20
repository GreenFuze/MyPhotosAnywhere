using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.DriveProbes;

/// <summary>Creates only synthetic fixtures beneath the explicitly selected disposable folder.
/// No update/delete endpoint is exposed. A private journal is durable before each create.</summary>
internal sealed class DriveRoundTripProbe(UserCredential credential)
{
    public async Task RunAsync(string folderId, string reportDirectory, CancellationToken cancellationToken)
    {
        ProbeDriveClient.CheckId(folderId);
        using var transport = new CompletionLossHandler(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var http = new HttpClient(transport, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        var api = new ProbeDriveClient(http, credential);
        var root = await api.GetMetadataAsync(folderId, cancellationToken).ConfigureAwait(false);
        if (root.GetProperty("mimeType").GetString() != ProbeDriveClient.FolderMime ||
            root.GetProperty("trashed").GetBoolean() || !root.GetProperty("ownedByMe").GetBoolean() ||
            !root.GetProperty("capabilities").GetProperty("canAddChildren").GetBoolean())
            throw new InvalidDataException("Disposable test root must be an owned writable non-trashed folder.");

        var run = Guid.NewGuid().ToString("N");
        var journal = new ProbeCreateJournal(Path.Combine(reportDirectory, "round-trip-" + run + ".journal.jsonl"));
        using (journal)
        {
            var runFolder = await api.CreateFolderAsync(folderId, "MPA synthetic probe " + run, journal, cancellationToken).ConfigureAwait(false);
            var nested = await api.CreateFolderAsync(runFolder, "Nested fixtures", journal, cancellationToken).ConfigureAwait(false);
            // 512 KiB + 137 exercises a non-aligned final chunk without using any user media.
            var bytes = new byte[512 * 1024 + 137];
            new Random(6045).NextBytes(bytes);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var payloadId = await api.ReserveIdAsync(cancellationToken).ConfigureAwait(false);
            journal.Record(new { operation = "before-payload-create", id = payloadId, parent = nested, sha256 = digest, size = bytes.Length });
            var completionLost = false;
            try
            {
                await api.UploadResumableAsync(payloadId, nested, "synthetic-payload.bin", bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (SyntheticCompletionLostException) { completionLost = true; }
            if (!completionLost || !transport.DiscardedCompletion)
                throw new InvalidDataException("Synthetic lost-completion transport boundary was not exercised.");

            // The transport disposed the real successful HTTP response before the uploader could
            // consume it. Reconcile the journaled identity; never invent a fresh replacement ID.
            var metadata = await api.GetMetadataAsync(payloadId, cancellationToken).ConfigureAwait(false);
            ValidatePayload(metadata, payloadId, nested, bytes.Length, digest);
            await api.ExpectDuplicateCreateAsync(payloadId, nested, cancellationToken).ConfigureAwait(false);
            var afterReplay = await api.GetMetadataAsync(payloadId, cancellationToken).ConfigureAwait(false);
            ValidatePayload(afterReplay, payloadId, nested, bytes.Length, digest);

            var downloaded = await api.DownloadAsync(payloadId, null, null, cancellationToken).ConfigureAwait(false);
            if (!downloaded.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Full download differs from uploaded bytes.");
            var range = await api.DownloadAsync(payloadId, 123, 4567, cancellationToken).ConfigureAwait(false);
            if (!range.AsSpan().SequenceEqual(bytes.AsSpan(123, 4567 - 123 + 1)))
                throw new InvalidDataException("Drive range download differs from the expected byte slice.");

            // Chunk content is verified before publishing a separate immutable completion manifest.
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "mpa-feasibility-only", protocolMajor = 1, run,
                complete = true, payload = new { id = payloadId, sha256 = digest, size = bytes.Length }
            });
            var manifestId = await api.ReserveIdAsync(cancellationToken).ConfigureAwait(false);
            journal.Record(new { operation = "before-manifest-create", id = manifestId, parent = runFolder,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), size = manifestBytes.Length });
            await api.UploadResumableAsync(manifestId, runFolder, "complete-manifest.json", manifestBytes, cancellationToken).ConfigureAwait(false);
            var manifestRead = await api.DownloadAsync(manifestId, null, null, cancellationToken).ConfigureAwait(false);
            if (!manifestRead.AsSpan().SequenceEqual(manifestBytes)) throw new InvalidDataException("Published manifest did not round trip exactly.");
            var descendants = await api.ListTreeAsync(runFolder, cancellationToken).ConfigureAwait(false);
            if (!descendants.SetEquals([nested, payloadId, manifestId]))
                throw new InvalidDataException("Recursive paged discovery lost or added fixture identities.");

            var report = new
            {
                operation = "disposable-drive-round-trip", observedAtUtc = DateTimeOffset.UtcNow,
                grantedScopes = credential.Token.Scope, runFolder, nested, payloadId, manifestId,
                payloadBytes = bytes.Length, payloadSha256 = digest,
                reservedIdReplayStatus = 409, fullDownloadVerified = true, rangeVerified = true,
                chunkResumeStatusVerified = true, completionManifestVerified = true,
                recursivePagedDiscoveryVerified = true, fixtureCount = descendants.Count,
                injectedCompletionTransportFailure = true, actualSocketLossTested = false,
                crossClientTested = false, originalsTouched = false, fixturesRetained = true
            };
            journal.Record(new { operation = "complete", runFolder });
            await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "round-trip-" + run + ".json"),
                JsonSerializer.SerializeToUtf8Bytes(report), cancellationToken).ConfigureAwait(false);
        }
        Console.WriteLine("Synthetic Drive round trip passed: reserved IDs, chunk status/resume, duplicate reconciliation, full/range hashes, manifest and paged nested discovery. Fixtures retained; no existing content changed.");
    }

    private static void ValidatePayload(JsonElement metadata, string id, string parent, int size, string digest)
    {
        if (metadata.GetProperty("id").GetString() != id || metadata.GetProperty("trashed").GetBoolean() ||
            metadata.GetProperty("size").GetString() != size.ToString(CultureInfo.InvariantCulture) ||
            metadata.GetProperty("sha256Checksum").GetString() != digest ||
            !metadata.GetProperty("parents").EnumerateArray().Select(item => item.GetString()).SequenceEqual([parent]))
            throw new InvalidDataException("Uploaded fixture identity/parent/size/SHA-256 invariant failed.");
    }
}

internal sealed class ProbeCreateJournal : IDisposable
{
    private readonly FileStream _stream;

    public ProbeCreateJournal(string path) => _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

    public void Record<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        _stream.Write(bytes);
        _stream.WriteByte((byte)'\n');
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Probe-only API, bounded to 100 requests/1 MiB responses. Creates require parents
/// previously checked or created during this invocation; never follows a server redirect.</summary>
internal sealed class ProbeDriveClient(HttpClient http, UserCredential credential)
{
    public const string FolderMime = "application/vnd.google-apps.folder";
    private const string Fields = "id,mimeType,trashed,ownedByMe,parents,size,sha256Checksum,capabilities(canAddChildren)";
    private readonly HashSet<string> _createdFolders = new(StringComparer.Ordinal);
    private int _requests;

    internal static void CheckId(string id)
    {
        if (id.Length is < 1 or > 256 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid Drive fixture identity", nameof(id));
    }

    public async Task<JsonElement> GetMetadataAsync(string id, CancellationToken cancellationToken)
    {
        CheckId(id);
        var result = await JsonAsync(HttpMethod.Get, "drive/v3/files/" + id + "?fields=" + Uri.EscapeDataString(Fields),
            null, cancellationToken).ConfigureAwait(false);
        if (result.GetProperty("id").GetString() != id) throw new InvalidDataException("Drive returned another identity.");
        return result;
    }

    public async Task<string> ReserveIdAsync(CancellationToken cancellationToken)
    {
        var body = await JsonAsync(HttpMethod.Get, "drive/v3/files/generateIds?count=1&space=drive&type=files", null, cancellationToken).ConfigureAwait(false);
        var ids = body.GetProperty("ids");
        if (ids.GetArrayLength() != 1) throw new InvalidDataException("Drive did not reserve exactly one ID.");
        var id = ids[0].GetString() ?? throw new InvalidDataException("Reserved ID missing.");
        CheckId(id);
        return id;
    }

    public async Task<string> CreateFolderAsync(string parent, string name, ProbeCreateJournal journal, CancellationToken cancellationToken)
    {
        if (_createdFolders.Count == 0)
        {
            // The sole external root is independently checked here too, before admitting write authority.
            var root = await GetMetadataAsync(parent, cancellationToken).ConfigureAwait(false);
            if (root.GetProperty("mimeType").GetString() != FolderMime || root.GetProperty("trashed").GetBoolean() ||
                !root.GetProperty("ownedByMe").GetBoolean() || !root.GetProperty("capabilities").GetProperty("canAddChildren").GetBoolean())
                throw new InvalidDataException("Selected root is not an owned writable folder.");
        }
        else RequireCreatedParent(parent);
        var id = await ReserveIdAsync(cancellationToken).ConfigureAwait(false);
        journal.Record(new { operation = "before-folder-create", id, parent, name });
        using var content = JsonContent(new { id, name, mimeType = FolderMime, parents = new[] { parent } });
        var result = await JsonAsync(HttpMethod.Post, "drive/v3/files?fields=id", content, cancellationToken).ConfigureAwait(false);
        if (result.GetProperty("id").GetString() != id || !_createdFolders.Add(id))
            throw new InvalidDataException("Folder create identity invariant failed.");
        return id;
    }

    public async Task UploadResumableAsync(string id, string parent, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        CheckId(id);
        RequireCreatedParent(parent);
        if (bytes.Length is < 1 or > 1024 * 1024) throw new ArgumentException("Fixture upload exceeds its bound", nameof(bytes));
        using var metadata = JsonContent(new { id, name, parents = new[] { parent }, mimeType = "application/octet-stream" });
        using var request = await RequestAsync(HttpMethod.Post, new Uri("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id"),
            metadata, cancellationToken).ConfigureAwait(false);
        request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        request.Headers.Add("X-Upload-Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RequireStatus(response, HttpStatusCode.OK);
        var session = response.Headers.Location ?? throw new InvalidDataException("Upload session URI missing.");
        if (session.Scheme != "https" || session.Host != "www.googleapis.com" || session.Port != 443 ||
            session.AbsolutePath != "/upload/drive/v3/files" || session.UserInfo.Length != 0 || session.Fragment.Length != 0)
            throw new InvalidDataException("Upload session URI escaped its approved HTTPS destination.");
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(256 * 1024, bytes.Length - offset);
            using var chunk = new ByteArrayContent(bytes, offset, count);
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            chunk.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, bytes.Length);
            using var put = await RequestAsync(HttpMethod.Put, session, chunk, cancellationToken).ConfigureAwait(false);
            using var receipt = await http.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (offset + count == bytes.Length)
            {
                if (receipt.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                    throw new HttpRequestException("Final synthetic upload failed.", null, receipt.StatusCode);
                using var body = JsonDocument.Parse(await ReadBoundedAsync(receipt, cancellationToken).ConfigureAwait(false));
                if (body.RootElement.GetProperty("id").GetString() != id) throw new InvalidDataException("Upload completed with another ID.");
            }
            else
            {
                RequireStatus(receipt, (HttpStatusCode)308);
                RequireRange(receipt, offset + count - 1);
                // Query server state before the next chunk, as after a resumed client process.
                using var empty = new ByteArrayContent([]);
                empty.Headers.ContentRange = new ContentRangeHeaderValue(bytes.Length);
                using var statusRequest = await RequestAsync(HttpMethod.Put, session, empty, cancellationToken).ConfigureAwait(false);
                using var status = await http.SendAsync(statusRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                RequireStatus(status, (HttpStatusCode)308);
                RequireRange(status, offset + count - 1);
            }
            offset += count;
        }
    }

    public async Task ExpectDuplicateCreateAsync(string id, string parent, CancellationToken cancellationToken)
    {
        CheckId(id);
        RequireCreatedParent(parent);
        using var content = JsonContent(new { id, name = "synthetic-payload.bin", parents = new[] { parent }, mimeType = "application/octet-stream" });
        using var request = await RequestAsync(HttpMethod.Post, new Uri("https://www.googleapis.com/drive/v3/files?fields=id"), content, cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RequireStatus(response, HttpStatusCode.Conflict);
        _ = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadAsync(string id, int? from, int? to, CancellationToken cancellationToken)
    {
        CheckId(id);
        using var request = await RequestAsync(HttpMethod.Get, new Uri("https://www.googleapis.com/drive/v3/files/" + id + "?alt=media"), null, cancellationToken).ConfigureAwait(false);
        if (from.HasValue != to.HasValue || from < 0 || to < from) throw new ArgumentException("Invalid range.");
        if (from.HasValue) request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RequireStatus(response, from.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
        if (from.HasValue && (response.Content.Headers.ContentRange?.From != from || response.Content.Headers.ContentRange?.To != to))
            throw new InvalidDataException("Drive returned an unexpected byte range.");
        return await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HashSet<string>> ListTreeAsync(string root, CancellationToken cancellationToken)
    {
        CheckId(root);
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var parent))
        {
            string? page = null;
            var pages = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var path = "drive/v3/files?q=" + Uri.EscapeDataString("'" + parent + "' in parents and trashed=false") +
                    "&pageSize=1&fields=files(id,mimeType),nextPageToken" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
                var body = await JsonAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
                foreach (var file in body.GetProperty("files").EnumerateArray())
                {
                    var id = file.GetProperty("id").GetString() ?? throw new InvalidDataException("Listed file identity missing.");
                    CheckId(id);
                    if (found.Count >= 20 || !found.Add(id)) throw new InvalidDataException("Fixture traversal exceeds its bound or repeats an identity.");
                    if (file.GetProperty("mimeType").GetString() == FolderMime) pending.Enqueue(id);
                }
                page = body.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                if (page is not null && (page.Length is < 1 or > 4096 || !pages.Add(page)))
                    throw new InvalidDataException("Drive paging token is repeated or out of bounds.");
            } while (page is not null);
        }
        return found;
    }

    private void RequireCreatedParent(string id)
    {
        CheckId(id);
        if (!_createdFolders.Contains(id)) throw new InvalidOperationException("Create escaped the folders owned by this probe invocation.");
    }

    private async Task<JsonElement> JsonAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(method, new Uri("https://www.googleapis.com/" + path), content, cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RequireStatus(response, HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, Uri address, HttpContent? content, CancellationToken cancellationToken)
    {
        if (++_requests > 100) throw new InvalidOperationException("Drive fixture request budget exceeded.");
        var token = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, address) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static ByteArrayContent JsonContent<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected) throw new HttpRequestException("Unexpected Drive fixture HTTP status.", null, response.StatusCode);
    }

    private static void RequireRange(HttpResponseMessage response, int last)
    {
        if (!response.Headers.TryGetValues("Range", out var values) ||
            !values.SequenceEqual(["bytes=0-" + last.ToString(CultureInfo.InvariantCulture)]))
            throw new InvalidDataException("Upload receipt/status did not confirm the exact received range.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int limit = 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Fixture response exceeds its bound.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Streamed fixture response exceeds its bound.");
            output.Write(bytes, 0, count);
        }
        return output.ToArray();
    }
}
