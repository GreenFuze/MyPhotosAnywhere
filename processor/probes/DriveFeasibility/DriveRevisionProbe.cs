using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.DriveProbes;

/// <summary>Read only; accepts only the exact generated synthetic payload from the round-trip probe.</summary>
internal sealed class DriveRevisionProbe(UserCredential credential)
{
    public async Task RunAsync(string fileId, string reportDirectory, CancellationToken cancellationToken)
    {
        ProbeDriveClient.CheckId(fileId);
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var token = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = await DriveReadProbe.ReadAsync(client, token, "files/" + fileId +
            "?supportsAllDrives=true&fields=id,name,mimeType,version,size,sha256Checksum,trashed,ownedByMe,parents",
            allowInvisible: false, cancellationToken).ConfigureAwait(false);
        var body = result.Body;
        if (body.GetProperty("id").GetString() != fileId || body.GetProperty("name").GetString() != "synthetic-payload.bin" ||
            body.GetProperty("size").GetString() != "524425" || body.GetProperty("trashed").GetBoolean() || !body.GetProperty("ownedByMe").GetBoolean() ||
            body.GetProperty("sha256Checksum").GetString() != "7cd2545de4849c065b059d99b524abd21ccb49314bf443f046aba28feeb06a62")
            throw new InvalidDataException("Revision observation is restricted to the generated synthetic payload.");
        var report = new { operation = "read-only-synthetic-revision", observedAtUtc = DateTimeOffset.UtcNow,
            version = body.GetProperty("version").GetString(), etag = result.Etag, etagPresent = result.Etag is not null,
            strongEtag = result.Etag is not null && !result.Etag.StartsWith("W/", StringComparison.Ordinal), writesPerformed = false };
        await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "revision-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(report), cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Synthetic metadata revision/ETag observation recorded. No content or metadata was changed.");
    }
}
