using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Mpa.Processor.Web;

/// <summary>Retains warning/error messages with endpoint/capability redaction. No request-body
/// logging. Diagnostics remain blocking and observable, including during startup.</summary>
internal sealed class WebDiagnostics : ILoggerProvider, ILogger
{
    private readonly object _gate = new();
    private readonly List<Exception> _failures = [];
    public event Action<Exception>? Reported;
    public IReadOnlyList<Exception> Failures { get { lock (_gate) return _failures.ToArray(); } }
    public ILogger CreateLogger(string categoryName) => this;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        // Preserve the diagnostic, not just its numeric event ID. Framework message text
        // belongs to host diagnostics only. Redact URLs/capabilities before retaining it.
        var message = formatter(state, null);
        message = Regex.Replace(message, @"https?://\S+", "[redacted endpoint]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        message = Regex.Replace(message, @"\b[0-9a-fA-F]{64}\b", "[redacted capability/digest]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (message.Length > 8192) message = message[..8192] + " [truncated]";
        var diagnostic = new InvalidOperationException("MPA web host reported " + logLevel + " event " + eventId.Id + ": " + message, exception);
        lock (_gate)
        {
            if (_failures.Count == 128) throw new InvalidOperationException("Web diagnostic capacity exceeded; stop the host and inspect its retained failures.");
            _failures.Add(diagnostic);
        }
        Reported?.Invoke(diagnostic);
    }
    public void Dispose() { }
}
