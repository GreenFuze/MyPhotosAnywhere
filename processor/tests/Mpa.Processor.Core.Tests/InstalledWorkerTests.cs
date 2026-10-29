using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class InstalledWorkerTests
{
    private static readonly string Recipe = Convert.ToHexStringLower(SHA256.HashData("mpa.byte-facts:1:verified-snapshot"u8));

    [TestMethod]
    public async Task ProductWorkerVerifiesSnapshotAndProducesIndependentlyVerifiedFactsWithoutChangingInput()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = "Synthetic media snapshot for the harmless worker integration test."u8.ToArray();
        var input = Path.Combine(root.Path, "input.bin");
        await File.WriteAllBytesAsync(input, bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await Peer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt();
        await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        Assert.ThrowsExactly<IOException>(() => { using var blocked = File.Open(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        await peer.SendAsync("run", new { }, attempt).ConfigureAwait(false);
        var result = await peer.ReceiveAsync().ConfigureAwait(false);
        Assert.AreEqual("result", result.Type);
        Assert.AreEqual(attempt, result.Attempt);
        var artifact = result.Payload.GetProperty("artifacts").EnumerateArray().Single();
        var path = artifact.GetProperty("relativePath").GetString()!;
        var expected = new ArtifactDescriptor(long.Parse(artifact.GetProperty("bytes").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            artifact.GetProperty("sha256").GetString()!, path);
        using (var facts = File.OpenRead(Path.Combine(root.Path, expected.RelativePath)))
            await new ArtifactVerifier(64 * 1024).VerifyAsync(facts, expected, deadline.Token).ConfigureAwait(false);
        using var decoded = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root.Path, path), deadline.Token).ConfigureAwait(false));
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), decoded.RootElement.GetProperty("sourceSha256").GetString());
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(input, deadline.Token).ConfigureAwait(false));
        await peer.ExitAsync().ConfigureAwait(false);
        Assert.AreEqual(0L, new FileInfo(peer.Diagnostics).Length);
    }

    [TestMethod]
    public async Task ProductWorkerCancellationReleasesPreparedInputAndProducesNoResult()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = new byte[] { 1, 3, 5, 7 };
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "input.bin"), bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await Peer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt();
        await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        await peer.SendAsync("cancel", new { }, attempt).ConfigureAwait(false);
        Assert.AreEqual("canceled", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        using (var released = File.Open(Path.Combine(root.Path, "input.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.AreEqual(1, released.ReadByte());
        Assert.AreEqual(0, Directory.GetFiles(root.Path, "facts-*.json").Length);
        await peer.ExitAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ProductWorkerRejectsChangedInputButCanContinueWithAnotherItem()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = new byte[] { 2, 4, 6 };
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "input.bin"), bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await Peer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var wrong = new byte[] { 2, 4, 7 };
        await peer.SendAsync("prepare", Preparation(wrong), Attempt()).ConfigureAwait(false);
        var failure = await peer.ReceiveAsync().ConfigureAwait(false);
        Assert.AreEqual("failure", failure.Type);
        Assert.AreEqual("snapshot-integrity", failure.Payload.GetProperty("code").GetString());
        var next = Attempt();
        await peer.SendAsync("prepare", Preparation(bytes), next).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        await peer.SendAsync("cancel", new { }, next).ConfigureAwait(false);
        Assert.AreEqual("canceled", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        await peer.ExitAsync().ConfigureAwait(false);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(root.Path, "input.bin"), deadline.Token).ConfigureAwait(false));
    }

    private static WorkerAttempt Attempt() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static object Preparation(byte[] bytes) => new { recipeSha256 = Recipe, input = new { relativePath = "input.bin",
        bytes = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } };

    private sealed class Peer : IAsyncDisposable
    {
        private readonly FramedWorkerProcess _process;
        private readonly WorkerMessageCodec _codec;
        private readonly WorkerConversation _state = new();
        private readonly IAsyncEnumerator<byte[]> _frames;
        private readonly CancellationToken _token;
        public string Diagnostics { get; }
        private Peer(FramedWorkerProcess process, Guid session, string diagnostics, CancellationToken token)
        {
            _process = process; _codec = new(session); Diagnostics = diagnostics; _token = token;
            _frames = process.ReadFramesAsync(token).GetAsyncEnumerator(token);
        }
        public static async Task<Peer> StartAsync(string root, CancellationToken token)
        {
            var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            using var executable = File.OpenRead(host);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(executable, token).ConfigureAwait(false));
            var fixture = Path.Combine(AppContext.BaseDirectory, "ProductWorker", "Mpa.Processor.WorkerHost.dll");
            var session = Guid.NewGuid(); var diagnostics = Path.Combine(root, "product-diagnostics.log");
            FramedWorkerProcess process;
            try
            {
                process = await FramedWorkerProcess.StartAsync(new(host, digest, [fixture, "--session", session.ToString("D"), "--staging", root],
                    Path.GetDirectoryName(fixture)!, diagnostics), token).ConfigureAwait(false);
            }
            catch (WorkerStartCleanupException error)
            {
                Console.WriteLine("Product fixture startup retained its actual failed-stop owner: " + error);
                try { await error.RetainedWorker.JoinAfterFailedStopAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Product fixture startup and actual recovery join both failed.", error, cleanup); }
                throw;
            }
            var peer = new Peer(process, session, diagnostics, token);
            try
            {
                await peer.SendAsync("hello", new { pluginId = "mpa.byte-facts", pluginVersion = "0.6.0" }).ConfigureAwait(false);
                var reply = await peer.ReceiveAsync().ConfigureAwait(false);
                Assert.AreEqual("capabilities", reply.Type);
                Assert.AreEqual(Recipe, reply.Payload.GetProperty("recipeSha256").GetString());
                return peer;
            }
            catch (Exception error)
            {
                try { await peer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Product fixture handshake and joined disposal both failed.", error, cleanup); }
                throw;
            }
        }
        public async Task SendAsync<T>(string type, T payload, WorkerAttempt? attempt = null)
        {
            var value = JsonSerializer.SerializeToElement(payload);
            _state.Observe(WorkerEndpoint.Core, new(type, value, attempt));
            await _process.SendAsync(_codec.Encode(type, value, attempt), _token).ConfigureAwait(false);
        }
        public async Task<WorkerMessage> ReceiveAsync()
        {
            if (!await _frames.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("Product worker exited without an expected message.");
            var reply = _codec.Read(_frames.Current);
            _state.Observe(WorkerEndpoint.Worker, reply);
            return reply;
        }
        public async Task ExitAsync()
        {
            await SendAsync("exit", new { }).ConfigureAwait(false);
            Assert.AreEqual("exited", (await ReceiveAsync().ConfigureAwait(false)).Type);
            await _process.Completion.WaitAsync(_token).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            try { await _process.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                failures.Add(error);
                Console.WriteLine("Product fixture stop failure retained before directory cleanup: " + error);
                try { await _process.JoinAfterFailedStopAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failures.Add(cleanup); }
            }
            try { await _frames.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("Product fixture stop/frame retirement failures are retained.", failures);
        }
    }
}
