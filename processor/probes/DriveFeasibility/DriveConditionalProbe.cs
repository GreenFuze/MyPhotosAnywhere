using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.DriveProbes;

/// <summary>Mutates ONLY the new tiny synthetic file created and journaled by this invocation.
/// No external file target, original-content update or permanent-delete endpoint is accepted.</summary>
internal sealed class DriveConditionalProbe(UserCredential credential)
{
    public async Task RunAsync(string rootId, string reportDirectory, CancellationToken cancellationToken)
    {
        ProbeDriveClient.CheckId(rootId);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var api = new ProbeDriveClient(http, credential);
        var run = Guid.NewGuid().ToString("N");
        using var journal = new ProbeCreateJournal(Path.Combine(reportDirectory, "conditional-" + run + ".journal.jsonl"));
        var folder = await api.CreateFolderAsync(rootId, "MPA conditional synthetic probe " + run, journal, cancellationToken).ConfigureAwait(false);
        var id = await api.ReserveIdAsync(cancellationToken).ConfigureAwait(false);
        var bytes = "MPA synthetic conditional metadata fixture; no user content.\n"u8.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        journal.Record(new { operation = "before-conditional-fixture-create", id, parent = folder, size = bytes.Length, sha256 = digest });
        await api.UploadResumableAsync(id, folder, "conditional-fixture.txt", bytes, cancellationToken).ConfigureAwait(false);
        var initial = await api.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
        if (initial.GetProperty("sha256Checksum").GetString() != digest || initial.GetProperty("trashed").GetBoolean() ||
            !initial.GetProperty("parents").EnumerateArray().Select(parent => parent.GetString()).SequenceEqual([folder]))
            throw new InvalidDataException("New conditional fixture failed identity/content/parent validation.");
        var token = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var conditional = new ConditionalSyntheticClient(http, token, id, journal);
        var initialV2 = await conditional.ReadV2Async(cancellationToken).ConfigureAwait(false);
        var observations = new List<object>();
        var invalid = "\"mpa-intentionally-invalid-precondition-" + run + "\"";
        var wrong = await conditional.ChangeAsync(2, "description", invalid, cancellationToken).ConfigureAwait(false);
        observations.Add(new { operation = "v2-invalid-etag-description", status = wrong.Status });
        // An arbitrary opaque ETag can expose server-side parsing errors. Real concurrency
        // support is established with a formerly valid ETag after an actual accepted change.
        var v2Supported = initialV2.Etag is not null;
        if (v2Supported)
        {
            var changed = await conditional.ChangeAsync(2, "description", initialV2.Etag!, cancellationToken).ConfigureAwait(false);
            if (changed.Status != 200) throw new InvalidDataException("Valid v2 ETag did not permit the synthetic metadata change.");
            var stale = await conditional.ChangeAsync(2, "description", initialV2.Etag!, cancellationToken).ConfigureAwait(false);
            observations.Add(new { operation = "v2-stale-etag-description", status = stale.Status });
            if (stale.Status != 412) v2Supported = false;
            if (v2Supported)
            {
                var current = await conditional.ReadV2Async(cancellationToken).ConfigureAwait(false);
                var wrongTrash = await conditional.ChangeAsync(2, "trash", initialV2.Etag!, cancellationToken).ConfigureAwait(false);
                observations.Add(new { operation = "v2-invalid-etag-trash", status = wrongTrash.Status });
                if (wrongTrash.Status != 412) v2Supported = false;
                else
                {
                    var beforeTrashEtag = current.Etag!;
                    var trash = await conditional.ChangeAsync(2, "trash", beforeTrashEtag, cancellationToken).ConfigureAwait(false);
                    if (trash.Status != 200 || !(await conditional.ReadV2Async(cancellationToken).ConfigureAwait(false)).Trashed)
                        throw new InvalidDataException("Conditional synthetic Trash failed.");
                    current = await conditional.ReadV2Async(cancellationToken).ConfigureAwait(false);
                    var wrongRestore = await conditional.ChangeAsync(2, "untrash", beforeTrashEtag, cancellationToken).ConfigureAwait(false);
                    observations.Add(new { operation = "v2-invalid-etag-restore", status = wrongRestore.Status });
                    if (wrongRestore.Status != 412) v2Supported = false;
                    else
                    {
                        var restore = await conditional.ChangeAsync(2, "untrash", current.Etag!, cancellationToken).ConfigureAwait(false);
                        if (restore.Status != 200 || (await conditional.ReadV2Async(cancellationToken).ConfigureAwait(false)).Trashed)
                            throw new InvalidDataException("Conditional synthetic restore failed.");
                    }
                }
            }
        }
        var wrongV3 = await conditional.ChangeAsync(3, "description", invalid, cancellationToken).ConfigureAwait(false);
        observations.Add(new { operation = "v3-invalid-etag-description", status = wrongV3.Status });
        var downloaded = await api.DownloadAsync(id, null, null, cancellationToken).ConfigureAwait(false);
        if (!downloaded.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Synthetic content changed during metadata-only testing.");
        var report = new { operation = "synthetic-conditional-metadata", observedAtUtc = DateTimeOffset.UtcNow,
            v2EtagPresent = initialV2.Etag is not null, v2ConditionalMetadataAndTrashSupported = v2Supported,
            v3RejectedInvalidCondition = wrongV3.Status == 412, observations, originalBytesUnchanged = true,
            externalTargetsAccepted = false, fixtureRetained = true, permanentDeleteExposed = false };
        await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "conditional-" + run + ".json"), JsonSerializer.SerializeToUtf8Bytes(report), cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Synthetic-only conditional metadata observation recorded; no existing originals changed. Inspect capability results before enabling product mutations.");
    }
}

internal sealed class ConditionalSyntheticClient(HttpClient http, string token, string createdId, ProbeCreateJournal journal)
{
    private int _requests;
    private const string V2Fields = "id,etag,version,labels(trashed)";

    internal async Task<ConditionalObservation> ReadV2Async(CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, 2, "", null, null, cancellationToken).ConfigureAwait(false);

    // Only this invocation's newly created synthetic fixture is admitted. No caller
    // supplied target, original replacement or v3 conditional fallback is exposed.
    internal Task<ConditionalObservation> WriteMediaAsync(ReadOnlyMemory<byte> bytes,string capturedEtag,CancellationToken cancellationToken)
    {
        if (bytes.Length is < 1 or > 65536 || !EntityTagHeaderValue.TryParse(capturedEtag,out var tag) || tag.IsWeak || tag.Tag == "*")
            throw new ArgumentException("Synthetic media needs bounded bytes and an actual strong captured entity tag.");
        var owned=bytes.ToArray();
        journal.Record(new { operation="before-synthetic-media-update",id=createdId,bytes=owned.Length,
            sha256=Convert.ToHexStringLower(SHA256.HashData(owned)),condition=capturedEtag });
        return SendAsync(HttpMethod.Put,2,"",capturedEtag,null,cancellationToken,owned);
    }

    internal Task<ConditionalObservation> ChangeAsync(int version, string operation, string etag, CancellationToken cancellationToken)
    {
        if (version is not (2 or 3) || operation is not ("description" or "trash" or "untrash") ||
            version == 3 && operation != "description") throw new InvalidOperationException("Unsupported synthetic metadata probe operation.");
        ProbeDriveClient.CheckId(createdId);
        journal.Record(new { operation = "before-synthetic-metadata-change", id = createdId, apiVersion = version, action = operation, condition = etag });
        return SendAsync(operation == "description" ? HttpMethod.Patch : HttpMethod.Post, version,
            operation == "description" ? "" : "/" + operation, etag,
            operation == "description" ? "{\"description\":\"MPA synthetic conditional test only\"}" : null, cancellationToken);
    }

    private async Task<ConditionalObservation> SendAsync(HttpMethod method, int version, string suffix, string? etag, string? json, CancellationToken cancellationToken,byte[]? media=null)
    {
        if (++_requests > 20) throw new InvalidOperationException("Synthetic conditional request budget exceeded.");
        var fields = version == 2 ? V2Fields : "id,version,trashed";
        ProbeDriveClient.CheckId(createdId);
        var address = new Uri("https://www.googleapis.com/"+(media is null ? "" : "upload/")+"drive/v" + version + "/files/" + createdId + suffix + "?"+
            (media is null ? "" : "uploadType=media&supportsAllDrives=true&")+"fields=" + Uri.EscapeDataString(fields));
        using var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (etag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (media is not null) { request.Content=new ByteArrayContent(media);request.Content.Headers.ContentType=new("application/octet-stream"); }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var arbitraryConditionServerError = method == HttpMethod.Patch && etag is not null &&
            etag.StartsWith("\"mpa-intentionally-invalid-precondition-", StringComparison.Ordinal) && response.StatusCode == HttpStatusCode.InternalServerError;
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PreconditionFailed) && !arbitraryConditionServerError)
            throw new HttpRequestException("Unexpected synthetic conditional status: " + (int)response.StatusCode, null, response.StatusCode);
        if (response.Content.Headers.ContentLength > 64 * 1024) throw new InvalidDataException("Conditional response exceeds its bound.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(block, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > 64 * 1024) throw new InvalidDataException("Conditional response exceeds its bound.");
            buffer.Write(block, 0, count);
        }
        using var body = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        if (response.StatusCode == HttpStatusCode.PreconditionFailed || arbitraryConditionServerError) return new((int)response.StatusCode, null, false);
        if (body.RootElement.GetProperty("id").GetString() != createdId) throw new InvalidDataException("Conditional response changed identity.");
        var tag = version == 2 && body.RootElement.TryGetProperty("etag", out var value) ? value.GetString() : response.Headers.ETag?.ToString();
        var trashed = version == 2 ? body.RootElement.GetProperty("labels").GetProperty("trashed").GetBoolean() : body.RootElement.GetProperty("trashed").GetBoolean();
        var revision=body.RootElement.TryGetProperty("version",out var actualVersion) && actualVersion.ValueKind==JsonValueKind.String &&
            long.TryParse(actualVersion.GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out var number) ? number : 0;
        return new(200, tag, trashed,revision);
    }
}

internal sealed record ConditionalObservation(int Status, string? Etag, bool Trashed,long Version=0);
