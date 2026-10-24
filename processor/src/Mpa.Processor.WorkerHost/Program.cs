using System.Globalization;
using Mpa.Processor.WorkerHost;
using Mpa.Processor.WorkerRuntime;

if (args.Length != 4 || args[0] != "--session" || !Guid.TryParseExact(args[1], "D", out var session) || session == Guid.Empty || args[2] != "--staging")
    throw new ArgumentException("Expected an assigned worker session and staging directory.");
var key = Environment.GetEnvironmentVariable("MPA_PARENT_KEY") ?? throw new InvalidOperationException("Missing owning Core connection.");
var port = int.Parse(Environment.GetEnvironmentVariable("MPA_PARENT_PORT") ?? "0", CultureInfo.InvariantCulture);
Environment.SetEnvironmentVariable("MPA_PARENT_KEY", null);
Environment.SetEnvironmentVariable("MPA_PARENT_PORT", null);
await using var parent = await ParentLifetimeGuard.ConnectAsync(new(port, key), CancellationToken.None).ConfigureAwait(false);
using var cancellation = new CancellationTokenSource();
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var worker = new ByteFactsWorker(session, args[3], input, output);
var work = worker.RunAsync(cancellation.Token);
if (await Task.WhenAny(work, parent.Completion).ConfigureAwait(false) == work)
{
    await work.ConfigureAwait(false);
    return 0;
}
await cancellation.CancelAsync().ConfigureAwait(false);
try { await work.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { /* Parent loss fences every result. */ }
catch (TimeoutException) { Environment.Exit(86); } // OS releases readers if a future backend ignores cancellation.
return 86;
