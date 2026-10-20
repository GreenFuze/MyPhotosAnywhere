using System.Net;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.DriveProbes;

/// <summary>Fault injection against the real bounded reader, without Google access.</summary>
internal static class DriveReadProtocolProbe
{
    public static async Task RunAsync()
    {
        await CheckAsync(HttpStatusCode.OK, "{\"files\":[]}", allowInvisible: false).ConfigureAwait(false);
        await CheckAsync(HttpStatusCode.NotFound, Error("notFound"), allowInvisible: true).ConfigureAwait(false);
        await CheckAsync(HttpStatusCode.Forbidden, Error("insufficientPermissions"), allowInvisible: true).ConfigureAwait(false);
        await CheckAsync(HttpStatusCode.NotFound, Error("notFound"), allowInvisible: false, typeof(HttpRequestException)).ConfigureAwait(false);
        await CheckAsync(HttpStatusCode.Forbidden, Error("rateLimitExceeded"), allowInvisible: true, typeof(HttpRequestException)).ConfigureAwait(false);
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
            await CheckAsync(status, "{}", allowInvisible: true, typeof(HttpRequestException)).ConfigureAwait(false);
        await CheckAsync(HttpStatusCode.OK, "invalid-json", allowInvisible: false, typeof(JsonException)).ConfigureAwait(false);
        foreach (var streaming in new[] { false, true })
            await CheckAsync(HttpStatusCode.OK, new string('x', 65537), allowInvisible: false,
                typeof(InvalidDataException), streaming: streaming).ConfigureAwait(false);
        await CheckAsync(HttpStatusCode.OK, "{}", allowInvisible: false, typeof(OperationCanceledException), cancel: true).ConfigureAwait(false);
        Console.WriteLine("Drive protocol fault checks passed: permission boundaries, HTTP failures, redirects, malformed/oversized bodies and cancellation.");
    }

    private static string Error(string reason) => "{\"error\":{\"errors\":[{\"reason\":\"" + reason + "\"}]}}";

    private static async Task CheckAsync(HttpStatusCode status, string body, bool allowInvisible,
        Type? expectedFailure = null, bool streaming = false, bool cancel = false)
    {
        using var client = new HttpClient(new SyntheticResponseHandler(status, body, streaming));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (cancel) await cancellation.CancelAsync().ConfigureAwait(false);
        var rejected = false;
        try
        {
            var result = await DriveReadProbe.ReadAsync(client, "synthetic-token", "files/synthetic-folder",
                allowInvisible, cancellation.Token).ConfigureAwait(false);
            if (result.Status != (int)status) throw new InvalidDataException("Reader changed an observed HTTP status");
        }
        catch (Exception error) when (expectedFailure is not null && expectedFailure.IsInstanceOfType(error))
        {
            rejected = true;
        }
        if (expectedFailure is not null && !rejected)
            throw new InvalidDataException("The Drive reader accepted an injected failure");
    }

    private sealed class SyntheticResponseHandler(HttpStatusCode status, string body, bool streaming) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "www.googleapis.com" ||
                request.Headers.Authorization?.ToString() != "Bearer synthetic-token")
                throw new InvalidDataException("Synthetic probe request escaped its expected method/host/credential contract");
            var bytes = Encoding.UTF8.GetBytes(body);
            HttpContent content = streaming
                ? new StreamContent(new NonSeekableBody(bytes))
                : new ByteArrayContent(bytes);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class NonSeekableBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }
}
