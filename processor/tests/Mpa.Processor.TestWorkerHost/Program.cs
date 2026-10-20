using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;
using Mpa.Processor.WorkerRuntime;
using Mpa.Processor.TestSupport;

// Synthetic child-process fixture only. Never included in the product installer/plugin registry.
if (args.Length != 2 || args[0] is not ("echo" or "ignore-eof" or "malformed" or "exit-error" or "exit-clean" or "parent-launcher"))
    throw new ArgumentException("Expected a known synthetic scenario and an owned test directory.");
var root = Path.GetFullPath(args[1]);
if (!Directory.Exists(root) || !Path.GetFileName(root).StartsWith("MPA-Worker-Test-", StringComparison.Ordinal))
    throw new ArgumentException("Synthetic worker requires its dedicated test directory.");
if (args[0] == "parent-launcher")
{
    Environment.SetEnvironmentVariable("MPA_SYNTHETIC_PARENT_SECRET", "test-marker-must-not-cross-worker-boundary");
    var host = Environment.ProcessPath ?? throw new InvalidOperationException("Missing dotnet host identity.");
    using var source = File.OpenRead(host);
    var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(source).ConfigureAwait(false));
    var fixture = Path.Combine(AppContext.BaseDirectory, "Mpa.Processor.TestWorkerHost.dll");
    await using var child = await FramedWorkerProcess.StartAsync(new(host, digest, [fixture, "ignore-eof", root],
        AppContext.BaseDirectory, Path.Combine(root, "orphan-diagnostic.log")), CancellationToken.None).ConfigureAwait(false);
    await using var frames = child.ReadFramesAsync(CancellationToken.None).GetAsyncEnumerator();
    if (!await frames.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("Synthetic child never acknowledged readiness.");
    using var childProcess = Process.GetProcessById(child.ProcessId);
    var pendingMarker = Path.Combine(root, "parent-ready-" + Guid.NewGuid().ToString("N") + ".pending");
    using (var marker = new FileStream(pendingMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        JsonSerializer.Serialize(marker, new { childPid = child.ProcessId, instance = TestProcessIdentity.Read(childProcess) });
        marker.Flush(flushToDisk: true);
    }
    File.Move(pendingMarker, Path.Combine(root, "parent-ready.json"), overwrite: false);
    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false); // Parent integration test deliberately terminates this fixture.
    return 0;
}

if (Environment.GetEnvironmentVariable("MPA_SYNTHETIC_PARENT_SECRET") is not null)
    throw new InvalidOperationException("Synthetic parent environment marker crossed the worker boundary.");

var secret = Environment.GetEnvironmentVariable("MPA_PARENT_KEY") ?? throw new InvalidOperationException("No owning controller key.");
var port = int.Parse(Environment.GetEnvironmentVariable("MPA_PARENT_PORT") ?? "0", CultureInfo.InvariantCulture);
Environment.SetEnvironmentVariable("MPA_PARENT_KEY", null);
Environment.SetEnvironmentVariable("MPA_PARENT_PORT", null);
await using var guard = await ParentLifetimeGuard.ConnectAsync(new(port, secret), CancellationToken.None).ConfigureAwait(false);
using var held = new FileStream(Path.Combine(root, "worker-read-lease.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
if (held.Length != 1 || held.ReadByte() != 42) throw new InvalidDataException("Synthetic preexisting input changed before worker readiness.");
using (var started = new FileStream(Path.Combine(root, "worker-started.marker"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
{ started.WriteByte(1); started.Flush(flushToDisk: true); }
using var output = Console.OpenStandardOutput();
using var writer = new WorkerFrameWriter(output);
await writer.WriteAsync("{\"type\":\"hello\"}"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
if (args[0] == "exit-error") return 7;
if (args[0] == "exit-clean") return 0;
if (args[0] == "malformed")
{
    await output.WriteAsync(new byte[4]).ConfigureAwait(false); // Deliberately invalid zero-length frame.
    await output.FlushAsync().ConfigureAwait(false);
}
if (args[0] is "ignore-eof" or "malformed")
{
    var end = await guard.Completion.ConfigureAwait(false);
    return end.ParentLost ? 86 : 0;
}
using var input = Console.OpenStandardInput();
var reader = new WorkerFrameReader(input);
var work = EchoAsync();
var first = await Task.WhenAny(work, guard.Completion).ConfigureAwait(false);
return first == guard.Completion ? 86 : await work.ConfigureAwait(false);

async Task<int> EchoAsync()
{
    byte[]? frame;
    while ((frame = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
        await writer.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
    return 0;
}
