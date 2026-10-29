using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;

namespace Mpa.Processor.DriveProbes;

/// <summary>Fault injection checks against the same write/range implementation used by the live probe.</summary>
internal static class DriveRoundTripProtocolProbe
{
    public static async Task RunAsync()
    {
        await CheckDroppedCompletionAsync().ConfigureAwait(false);
        using var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = "synthetic", ClientSecret = "synthetic" },
            Scopes = ["synthetic"]
        });
        var credential = new UserCredential(flow, "synthetic", new TokenResponse
        {
            AccessToken = "synthetic-token", IssuedUtc = DateTime.UtcNow, ExpiresInSeconds = 3600
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        await CheckAsync(credential, [], async api =>
            await api.DownloadAsync("../outside", null, null, cancellationToken).ConfigureAwait(false), typeof(ArgumentException)).ConfigureAwait(false);
        await CheckAsync(credential, [], async api =>
            await api.UploadResumableAsync("payload", "unowned", "ignored.bin", [1], cancellationToken).ConfigureAwait(false), typeof(InvalidOperationException)).ConfigureAwait(false);

        var temporary = Path.Combine(Path.GetTempPath(), "mpa-synthetic-journal-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var journal = new ProbeCreateJournal(temporary);
            foreach (var location in new[] { "http://www.googleapis.com/upload/drive/v3/files", "https://example.org/upload/drive/v3/files", "https://www.googleapis.com/wrong", "https://www.googleapis.com:444/upload/drive/v3/files" })
            {
                await CheckWithFolderAsync(credential, journal,
                    [Step.CreateSession(location)], async api =>
                        await api.UploadResumableAsync("payload", "created-folder", "fixture.bin", [1], cancellationToken).ConfigureAwait(false),
                    typeof(InvalidDataException), cancellationToken).ConfigureAwait(false);
            }
            await CheckWithFolderAsync(credential, journal,
                [Step.CreateSession(Session), Step.Put(HttpStatusCode.PermanentRedirect, range: "bytes=0-3")], async api =>
                    await api.UploadResumableAsync("payload", "created-folder", "fixture.bin", new byte[262147], cancellationToken).ConfigureAwait(false),
                typeof(InvalidDataException), cancellationToken).ConfigureAwait(false);
            await CheckWithFolderAsync(credential, journal,
                [Step.CreateSession(Session), Step.Put(HttpStatusCode.PermanentRedirect, range: "bytes=0-262143"), Step.Put(HttpStatusCode.PermanentRedirect)], async api =>
                    await api.UploadResumableAsync("payload", "created-folder", "fixture.bin", new byte[262147], cancellationToken).ConfigureAwait(false),
                typeof(InvalidDataException), cancellationToken).ConfigureAwait(false);
            await CheckWithFolderAsync(credential, journal,
                [Step.CreateSession(Session), Step.Put(HttpStatusCode.PermanentRedirect, range: "bytes=0-262143"),
                    Step.Put(HttpStatusCode.PermanentRedirect, range: "bytes=0-262143"), Step.Put(HttpStatusCode.OK, "{\"id\":\"payload\"}")], async api =>
                    await api.UploadResumableAsync("payload", "created-folder", "fixture.bin", new byte[262147], cancellationToken).ConfigureAwait(false),
                null, cancellationToken).ConfigureAwait(false);
            await CheckWithFolderAsync(credential, journal,
                [Step.CreateSession(Session), Step.Put(HttpStatusCode.OK, "{\"id\":\"other\"}")], async api =>
                    await api.UploadResumableAsync("payload", "created-folder", "fixture.bin", [1], cancellationToken).ConfigureAwait(false),
                typeof(InvalidDataException), cancellationToken).ConfigureAwait(false);
            await CheckWithFolderAsync(credential, journal,
                [new Step(HttpMethod.Post, "/drive/v3/files", HttpStatusCode.OK, "{\"id\":\"duplicate\"}")], async api =>
                    await api.ExpectDuplicateCreateAsync("payload", "created-folder", cancellationToken).ConfigureAwait(false),
                typeof(HttpRequestException), cancellationToken).ConfigureAwait(false);
        }
        finally { File.Delete(temporary); }

        await CheckAsync(credential,
            [new Step(HttpMethod.Get, "/drive/v3/files/payload", HttpStatusCode.OK, "abcd")], async api =>
                await api.DownloadAsync("payload", 0, 3, cancellationToken).ConfigureAwait(false), typeof(HttpRequestException)).ConfigureAwait(false);
        await CheckAsync(credential,
            [new Step(HttpMethod.Get, "/drive/v3/files/payload", HttpStatusCode.PartialContent, "abcd", DownloadRange: new ContentRangeHeaderValue(1, 4, 8))], async api =>
                await api.DownloadAsync("payload", 0, 3, cancellationToken).ConfigureAwait(false), typeof(InvalidDataException)).ConfigureAwait(false);
        await CheckAsync(credential,
            [new Step(HttpMethod.Get, "/drive/v3/files/payload", HttpStatusCode.OK, new string('x', 1024 * 1024 + 1))], async api =>
                await api.DownloadAsync("payload", null, null, cancellationToken).ConfigureAwait(false), typeof(InvalidDataException)).ConfigureAwait(false);
        Console.WriteLine("Drive fixture fault checks passed: ID/parent boundaries, session destinations, upload/status ranges, final identity, conflict requirement, download ranges and response size.");
    }

    private const string Session = "https://www.googleapis.com/upload/drive/v3/files?upload_id=synthetic";

    private static async Task CheckDroppedCompletionAsync()
    {
        using var inner = new CompletionResponseHandler();
        using var transport = new CompletionLossHandler(inner);
        using var http = new HttpClient(transport, disposeHandler: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var rejected = false;
        using (var first = new HttpRequestMessage(HttpMethod.Put, Session) { Content = new ByteArrayContent([1]) })
        {
            first.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1);
            try { using var unexpected = await http.SendAsync(first, cancellation.Token).ConfigureAwait(false); }
            catch (SyntheticCompletionLostException) { rejected = true; }
        }
        if (!rejected || !transport.DiscardedCompletion || inner.LastContent?.WasDisposed != true)
            throw new InvalidDataException("Discarded completion did not fail or release its response body.");
        using var second = new HttpRequestMessage(HttpMethod.Put, Session) { Content = new ByteArrayContent([1]) };
        second.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 1);
        using var response = await http.SendAsync(second, cancellation.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || inner.Requests != 2)
            throw new InvalidDataException("Completion fault injection was not limited to exactly one response.");
    }

    private sealed class CompletionResponseHandler : HttpMessageHandler
    {
        public TrackedContent? LastContent { get; private set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Put || request.RequestUri?.AbsoluteUri != Session)
                throw new InvalidDataException("Completion fault test escaped its synthetic destination.");
            Requests++;
            LastContent = new TrackedContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = LastContent });
        }
    }

    private sealed class TrackedContent() : ByteArrayContent("{}"u8.ToArray())
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static async Task CheckWithFolderAsync(UserCredential credential, ProbeCreateJournal journal, Step[] additional,
        Func<ProbeDriveClient, Task> operation, Type? expected, CancellationToken cancellationToken)
    {
        Step[] setup =
        [
            new(HttpMethod.Get, "/drive/v3/files/root", HttpStatusCode.OK,
                "{\"id\":\"root\",\"mimeType\":\"application/vnd.google-apps.folder\",\"trashed\":false,\"ownedByMe\":true,\"capabilities\":{\"canAddChildren\":true}}"),
            new(HttpMethod.Get, "/drive/v3/files/generateIds", HttpStatusCode.OK, "{\"ids\":[\"created-folder\"]}"),
            new(HttpMethod.Post, "/drive/v3/files", HttpStatusCode.OK, "{\"id\":\"created-folder\"}")
        ];
        await CheckAsync(credential, [.. setup, .. additional], async api =>
        {
            await api.CreateFolderAsync("root", "synthetic", journal, cancellationToken).ConfigureAwait(false);
            await operation(api).ConfigureAwait(false);
        }, expected).ConfigureAwait(false);
    }

    private static async Task CheckAsync(UserCredential credential, Step[] steps, Func<ProbeDriveClient, Task> operation, Type? expected)
    {
        using var handler = new ScriptedHandler(steps);
        using var http = new HttpClient(handler, disposeHandler: false);
        var rejected = false;
        try { await operation(new ProbeDriveClient(http, credential)).ConfigureAwait(false); }
        catch (Exception exception) when (expected is not null && expected.IsInstanceOfType(exception)) { rejected = true; }
        if (expected is not null && !rejected) throw new InvalidDataException("Fixture client accepted an injected protocol failure.");
        if (handler.Remaining != 0) throw new InvalidDataException("Fixture fault check did not reach its scripted boundary.");
    }

    private sealed record Step(HttpMethod Method, string Path, HttpStatusCode Status, string Body = "{}",
        string? Location = null, string? Range = null, ContentRangeHeaderValue? DownloadRange = null)
    {
        public static Step CreateSession(string location) => new(HttpMethod.Post, "/upload/drive/v3/files", HttpStatusCode.OK, Location: location);
        public static Step Put(HttpStatusCode status, string body = "{}", string? range = null) =>
            new(HttpMethod.Put, "/upload/drive/v3/files", status, body, Range: range);
    }

    private sealed class ScriptedHandler(Step[] steps) : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new(steps);
        public int Remaining => _steps.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_steps.TryDequeue(out var step) || request.Method != step.Method ||
                request.RequestUri?.Scheme != "https" || request.RequestUri.Host != "www.googleapis.com" ||
                request.RequestUri.AbsolutePath != step.Path || request.Headers.Authorization?.ToString() != "Bearer synthetic-token")
                throw new InvalidDataException("Synthetic fixture request escaped its scripted contract.");
            var response = new HttpResponseMessage(step.Status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(step.Body)) };
            if (step.Location is not null) response.Headers.Location = new Uri(step.Location);
            if (step.Range is not null) response.Headers.Add("Range", step.Range);
            if (step.DownloadRange is not null) response.Content.Headers.ContentRange = step.DownloadRange;
            return Task.FromResult(response);
        }
    }
}
