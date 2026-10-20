using System.Net;
using System.Text;

namespace Mpa.Processor.DriveProbes;

internal static class DriveConditionalProtocolProbe
{
    internal static async Task RunAsync(string root)
    {
        var journalPath = Path.Combine(root, "synthetic-condition-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var journal = new ProbeCreateJournal(journalPath);
        try
        {
            using (var http = new HttpClient(new ConditionalHandler(request =>
            {
                if (request.RequestUri?.Host != "www.googleapis.com" || request.RequestUri.AbsolutePath != "/drive/v2/files/new-synthetic-id" ||
                    request.Method != HttpMethod.Patch || request.Headers.IfMatch.Single().Tag != "\"stale-valid-tag\"")
                    throw new InvalidOperationException("Conditional request boundary changed.");
                return Response(HttpStatusCode.PreconditionFailed, "{\"error\":{\"code\":412}}");
            })))
            {
                var result = await new ConditionalSyntheticClient(http, "synthetic-token", "new-synthetic-id", journal)
                    .ChangeAsync(2, "description", "\"stale-valid-tag\"", CancellationToken.None).ConfigureAwait(false);
                if (result.Status != 412) throw new InvalidOperationException("A precondition failure was not retained.");
            }
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            {
                using var http = new HttpClient(new ConditionalHandler(_ => Response(status, "{}")));
                var client = new ConditionalSyntheticClient(http, "synthetic-token", "new-synthetic-id", journal);
                await FailsAsync<HttpRequestException>(() => client.ReadV2Async(CancellationToken.None)).ConfigureAwait(false);
            }
            using (var http = new HttpClient(new ConditionalHandler(_ => Response(HttpStatusCode.OK,
                "{\"id\":\"unrelated-id\",\"etag\":\"\\\"tag\\\"\",\"labels\":{\"trashed\":false}}"))))
                await FailsAsync<InvalidDataException>(() => new ConditionalSyntheticClient(http, "synthetic-token", "new-synthetic-id", journal)
                    .ReadV2Async(CancellationToken.None)).ConfigureAwait(false);
            using (var http = new HttpClient(new ConditionalHandler(_ => Response(HttpStatusCode.OK, new string('x', 65537)))))
                await FailsAsync<InvalidDataException>(() => new ConditionalSyntheticClient(http, "synthetic-token", "new-synthetic-id", journal)
                    .ReadV2Async(CancellationToken.None)).ConfigureAwait(false);
            using (var http = new HttpClient(new ConditionalHandler(_ => throw new InvalidOperationException("Unsupported operation reached HTTP."))))
                await FailsAsync<InvalidOperationException>(() => new ConditionalSyntheticClient(http, "synthetic-token", "new-synthetic-id", journal)
                    .ChangeAsync(3, "delete-permanently", "\"tag\"", CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            // Close the owned journal before removing this synthetic-only file on Windows.
            journal.Dispose();
            File.Delete(journalPath);
        }
        Console.WriteLine("Synthetic conditional transport status/identity/size/operation boundaries passed; no Google request occurred.");
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static async Task FailsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException("Conditional protocol did not reject " + typeof(T).Name);
    }

    private sealed class ConditionalHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
