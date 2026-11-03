using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core.Tests;

[TestClass]
public sealed class TakeoutWorkerTests
{
    private static readonly string Recipe = Convert.ToHexStringLower(SHA256.HashData("mpa.takeout-metadata:1:bounded-original-sidecar"u8));
    private static readonly string[] ExpectedImportedNames = ["Alice", "אליס"];
    private const string Valid = "{\"title\":\"שלום <tag>.jpg\",\"description\":\"Original description\",\"photoTakenTime\":{\"timestamp\":\"-1\"},\"geoData\":{\"latitude\":31.7,\"longitude\":35.2},\"people\":[{\"name\":\"Alice\"},{\"name\":\"אליס\"}],\"unknownFuture\":{\"keep\":[1,true]}}";

    [TestMethod]
    public async Task InstalledTakeoutWorkerRetainsExactSidecarAndExtractsFactsWithoutChangingInput()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = Encoding.UTF8.GetBytes(" \n" + Valid + "\n");
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "input.bin"), bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await TakeoutPeer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt(); await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        Assert.ThrowsExactly<IOException>(() => { using var held = File.Open(Path.Combine(root.Path, "input.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        await peer.SendAsync("run", new { }, attempt).ConfigureAwait(false);
        using var result = await ReadResultAsync(peer, root.Path, attempt, deadline.Token).ConfigureAwait(false);
        var facts = result.RootElement;
        Assert.AreEqual("mpa-takeout-metadata", facts.GetProperty("format").GetString());
        Assert.AreEqual("שלום <tag>.jpg", facts.GetProperty("title").GetString());
        Assert.AreEqual("Original description", facts.GetProperty("description").GetString());
        Assert.AreEqual(-1000L, facts.GetProperty("capturedUtc").GetInt64());
        Assert.AreEqual(31.7, facts.GetProperty("latitude").GetDouble());
        Assert.AreEqual(35.2, facts.GetProperty("longitude").GetDouble());
        CollectionAssert.AreEqual(ExpectedImportedNames, facts.GetProperty("importedNames").EnumerateArray().Select(value => value.GetString()).ToArray());
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(facts.GetProperty("rawSidecarBase64").GetString()!));
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), facts.GetProperty("sourceSha256").GetString());
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(root.Path, "input.bin"), deadline.Token).ConfigureAwait(false));
        await peer.ExitAsync().ConfigureAwait(false); Assert.AreEqual(0L, new FileInfo(peer.Diagnostics).Length);
    }

    [TestMethod]
    [DataRow("{\"title\":\"album\",\"description\":\"Not a media sidecar\"}", "unsupported-metadata")]
    [DataRow("{\"title\":\"a.jpg\",\"title\":\"b.jpg\",\"creationTime\":{}}", "invalid-metadata")]
    [DataRow("{\"title\":\"a.jpg\",\"creationTime\":{},\"future\":{\"a\":1,\"a\":2}}", "invalid-metadata")]
    [DataRow("{\"title\":\"../a.jpg\",\"creationTime\":{}}", "invalid-metadata")]
    [DataRow("{\"title\":\"a.jpg\",\"photoTakenTime\":{\"timestamp\":\"9223372036854775807\"}}", "invalid-metadata")]
    [DataRow("{\"title\":\"a.jpg\",\"geoData\":{\"latitude\":31}}", "invalid-metadata")]
    public async Task InstalledTakeoutWorkerReportsMetadataIssueAndProcessesTheNextItem(string bad, string code)
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = Encoding.UTF8.GetBytes(bad); var path = Path.Combine(root.Path, "input.bin");
        await File.WriteAllBytesAsync(path, bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await TakeoutPeer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt(); await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        await peer.SendAsync("run", new { }, attempt).ConfigureAwait(false); var issue = await peer.ReceiveAsync().ConfigureAwait(false);
        Assert.AreEqual("failure", issue.Type); Assert.AreEqual(attempt, issue.Attempt);
        Assert.AreEqual(code, issue.Payload.GetProperty("code").GetString()); Assert.IsFalse(issue.Payload.GetProperty("retryable").GetBoolean());
        Assert.AreEqual("run", issue.Payload.GetProperty("stage").GetString()); Assert.AreEqual(0, Directory.GetFiles(root.Path, "facts-*.json").Length);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, deadline.Token).ConfigureAwait(false));
        bytes = Encoding.UTF8.GetBytes(Valid); await File.WriteAllBytesAsync(path, bytes, deadline.Token).ConfigureAwait(false);
        attempt = Attempt(); await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        await peer.SendAsync("run", new { }, attempt).ConfigureAwait(false);
        using var result = await ReadResultAsync(peer, root.Path, attempt, deadline.Token).ConfigureAwait(false);
        Assert.AreEqual("שלום <tag>.jpg", result.RootElement.GetProperty("title").GetString());
        await peer.ExitAsync().ConfigureAwait(false); Assert.AreEqual(0L, new FileInfo(peer.Diagnostics).Length);
    }

    [TestMethod]
    public async Task InstalledTakeoutWorkerRejectsOversizedMetadataBeforeHoldingInput()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = new byte[128 * 1024 + 1]; var path = Path.Combine(root.Path, "input.bin");
        await File.WriteAllBytesAsync(path, bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await TakeoutPeer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt(); await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        var issue = await peer.ReceiveAsync().ConfigureAwait(false); Assert.AreEqual("failure", issue.Type);
        Assert.AreEqual("metadata-too-large", issue.Payload.GetProperty("code").GetString()); Assert.AreEqual("prepare", issue.Payload.GetProperty("stage").GetString());
        using (var released = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) Assert.AreEqual(bytes.Length, released.Length);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path, deadline.Token).ConfigureAwait(false));
        Assert.AreEqual(0, Directory.GetFiles(root.Path, "facts-*.json").Length); await peer.ExitAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InstalledTakeoutWorkerCancelReleasesThePreparedSidecarBeforeAcknowledgement()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = Encoding.UTF8.GetBytes(Valid); var path = Path.Combine(root.Path, "input.bin");
        await File.WriteAllBytesAsync(path, bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await TakeoutPeer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt(); await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        await peer.SendAsync("cancel", new { }, attempt).ConfigureAwait(false); Assert.AreEqual("canceled", (await peer.ReceiveAsync().ConfigureAwait(false)).Type);
        using (var released = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) Assert.AreEqual(bytes.Length, released.Length);
        Assert.AreEqual(0, Directory.GetFiles(root.Path, "facts-*.json").Length); await peer.ExitAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InstalledTakeoutWorkerDoesNotInventCaptureTimeFromCreationTime()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = "{\"title\":\"a.jpg\",\"creationTime\":{\"timestamp\":\"1767225600\"}}"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "input.bin"), bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await TakeoutPeer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt(); await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type); await peer.SendAsync("run", new { }, attempt).ConfigureAwait(false);
        using var result = await ReadResultAsync(peer, root.Path, attempt, deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(JsonValueKind.Null, result.RootElement.GetProperty("capturedUtc").ValueKind);
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(result.RootElement.GetProperty("rawSidecarBase64").GetString()!));
        await peer.ExitAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task InstalledTakeoutWorkerPreservesBoundedMetadataBeyondTheProtocolStringLimit()
    {
        using var root = new WorkerProcessTests.WorkerTestDirectory(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var description = new string('x', 70 * 1024);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { title = "a.jpg", description, creationTime = new { timestamp = "1767225600" } });
        Assert.IsTrue(bytes.Length < 128 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "input.bin"), bytes, deadline.Token).ConfigureAwait(false);
        await using var peer = await TakeoutPeer.StartAsync(root.Path, deadline.Token).ConfigureAwait(false);
        var attempt = Attempt(); await peer.SendAsync("prepare", Preparation(bytes), attempt).ConfigureAwait(false);
        Assert.AreEqual("prepared", (await peer.ReceiveAsync().ConfigureAwait(false)).Type); await peer.SendAsync("run", new { }, attempt).ConfigureAwait(false);
        using var result = await ReadResultAsync(peer, root.Path, attempt, deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(description, result.RootElement.GetProperty("description").GetString());
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(result.RootElement.GetProperty("rawSidecarBase64").GetString()!));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(root.Path, "input.bin"), deadline.Token).ConfigureAwait(false));
        await peer.ExitAsync().ConfigureAwait(false); Assert.AreEqual(0L, new FileInfo(peer.Diagnostics).Length);
    }

    private static WorkerAttempt Attempt() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static object Preparation(byte[] bytes) => new { recipeSha256 = Recipe, input = new { relativePath = "input.bin",
        bytes = bytes.Length.ToString(CultureInfo.InvariantCulture), sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } };
    private static async Task<JsonDocument> ReadResultAsync(TakeoutPeer peer, string root, WorkerAttempt attempt, CancellationToken token)
    {
        var message = await peer.ReceiveAsync().ConfigureAwait(false); Assert.AreEqual("result", message.Type); Assert.AreEqual(attempt, message.Attempt);
        var artifact = message.Payload.GetProperty("artifacts").EnumerateArray().Single();
        var expected = new ArtifactDescriptor(long.Parse(artifact.GetProperty("bytes").GetString()!, CultureInfo.InvariantCulture), artifact.GetProperty("sha256").GetString()!, artifact.GetProperty("relativePath").GetString()!);
        await using (var stream = File.OpenRead(Path.Combine(root, expected.RelativePath)))
            await new ArtifactVerifier(1024 * 1024).VerifyAsync(stream, expected, token).ConfigureAwait(false);
        return JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, expected.RelativePath), token).ConfigureAwait(false));
    }
    private sealed class TakeoutPeer : IAsyncDisposable
    {
        private readonly FramedWorkerProcess _process;
        private readonly WorkerMessageCodec _codec;
        private readonly WorkerConversation _state = new();
        private readonly IAsyncEnumerator<byte[]> _frames;
        private readonly CancellationToken _token;
        public string Diagnostics { get; }
        private TakeoutPeer(FramedWorkerProcess process, Guid session, string diagnostics, CancellationToken token)
        {
            _process = process; _codec = new(session); Diagnostics = diagnostics; _token = token;
            _frames = process.ReadFramesAsync(token).GetAsyncEnumerator(token);
        }
        public static async Task<TakeoutPeer> StartAsync(string root, CancellationToken token)
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
            var peer = new TakeoutPeer(process, session, diagnostics, token);
            try
            {
                await peer.SendAsync("hello", new { pluginId = "mpa.takeout-metadata", pluginVersion = "0.6.0" }).ConfigureAwait(false);
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
