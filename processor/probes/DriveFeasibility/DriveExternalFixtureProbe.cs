using System.Security.Cryptography;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace Mpa.Processor.DriveProbes;

/// <summary>Read-only independent-client discovery of a previously uploaded synthetic fixture.</summary>
internal sealed class DriveExternalFixtureProbe(UserCredential credential)
{
    public async Task RunAsync(string rootId, string localFixture, string reportDirectory, CancellationToken cancellationToken)
    {
        ProbeDriveClient.CheckId(rootId);
        await using var input = File.OpenRead(localFixture);
        if (input.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("Local synthetic reference exceeds its bound.");
        var reference = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(reference, cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexStringLower(SHA256.HashData(reference));
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var api = new ProbeDriveClient(http, credential);
        var root = await api.GetMetadataAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (root.GetProperty("mimeType").GetString() != ProbeDriveClient.FolderMime || root.GetProperty("trashed").GetBoolean())
            throw new InvalidDataException("Selected discovery root is not an available folder.");
        var found = await api.ListTreeAsync(rootId, cancellationToken).ConfigureAwait(false);
        var matches = new List<string>();
        foreach (var id in found.Order(StringComparer.Ordinal))
        {
            var metadata = await api.GetMetadataAsync(id, cancellationToken).ConfigureAwait(false);
            if (metadata.GetProperty("mimeType").GetString() == ProbeDriveClient.FolderMime) continue;
            if (!metadata.TryGetProperty("sha256Checksum", out var checksum) || checksum.GetString() != digest) continue;
            var downloaded = await api.DownloadAsync(id, null, null, cancellationToken).ConfigureAwait(false);
            if (!downloaded.AsSpan().SequenceEqual(reference)) throw new InvalidDataException("Matching checksum did not yield the exact reference bytes.");
            matches.Add(id);
        }
        if (matches.Count != 1) throw new InvalidDataException("Discovery must find exactly one independently uploaded fixture.");
        var report = new
        {
            operation = "external-fixture-discovery", observedAtUtc = DateTimeOffset.UtcNow,
            grantedScopes = credential.Token.Scope, rootId, descendants = found.Count, matchedIds = matches,
            sha256 = digest, referenceBytes = reference.Length, exactDownloadVerified = true, writesPerformed = false,
            androidClientTested = false
        };
        await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "discover-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(report), cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Read-only discovery found and byte-verified exactly one independent synthetic fixture through paged nested traversal; no Drive writes performed.");
    }
}
