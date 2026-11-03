using System.Net;

namespace Mpa.Processor.DriveProbes;

/// <summary>Probe-only fault injection: drop exactly one successful final-upload response.
/// Owns/disposes that response before throwing, without logging its session URI or body.</summary>
internal sealed class CompletionLossHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int _remaining = 1;
    public bool DiscardedCompletion => Volatile.Read(ref _remaining) == 0;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var range = request.Content?.Headers.ContentRange;
        if (request.Method == HttpMethod.Put && range?.To is { } last && range.Length is { } length &&
            last == length - 1 && (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created) &&
            Interlocked.Exchange(ref _remaining, 0) == 1)
        {
            response.Dispose();
            throw new SyntheticCompletionLostException();
        }
        return response;
    }
}

internal sealed class SyntheticCompletionLostException : IOException
{
    public SyntheticCompletionLostException() : base("Synthetic successful completion response discarded before consumption.") { }
    public SyntheticCompletionLostException(string message) : base(message) { }
    public SyntheticCompletionLostException(string message, Exception innerException) : base(message, innerException) { }
}
