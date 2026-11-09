using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.WorkerHost;

/// <summary>First harmless installed worker: verifies an assigned immutable snapshot and writes
/// byte facts into its assigned staging directory. No Drive, credentials, original writes or inference.</summary>
internal sealed class ByteFactsWorker(Guid session, string staging, Stream input, Stream output)
{
    public const string PluginId = "mpa.byte-facts";
    public const string PluginVersion = "0.6.0";
    private static readonly string[] Capabilities = ["verified-byte-facts"];
    public static string RecipeSha256 { get; } = Convert.ToHexStringLower(SHA256.HashData("mpa.byte-facts:1:verified-snapshot"u8));
    private readonly WorkerMessageCodec _messages = new(session);
    private readonly WorkerConversation _conversation = new();
    private readonly WorkerFrameReader _reader = new(input);
    private readonly string _root = Path.GetFullPath(staging);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        CheckDirectory(_root);
        using var writer = new WorkerFrameWriter(output);
        FileStream? snapshot = null;
        ArtifactDescriptor? descriptor = null;
        try
        {
            while (true)
            {
                var frame = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new ProtocolFormatException("Worker received EOF before an acknowledged exit.");
                var message = _messages.Read(frame);
                _conversation.Observe(WorkerEndpoint.Core, message);
                switch (message.Type)
                {
                    case "hello":
                        ExactFields(message.Payload, "pluginId", "pluginVersion");
                        if (Text(message.Payload, "pluginId") != PluginId || Text(message.Payload, "pluginVersion") != PluginVersion)
                            throw new ProtocolCompatibilityException("Installed byte-facts worker identity differs from the requested plugin.");
                        await SendAsync("capabilities", new { pluginId = PluginId, pluginVersion = PluginVersion, recipeSha256 = RecipeSha256,
                            capabilities = Capabilities }, null).ConfigureAwait(false);
                        break;
                    case "prepare":
                        ExactFields(message.Payload, "input", "recipeSha256");
                        if (Text(message.Payload, "recipeSha256") != RecipeSha256) throw new ProtocolCompatibilityException("Worker recipe changed.");
                        var source = message.Payload.GetProperty("input");
                        ExactFields(source, "bytes", "sha256", "relativePath");
                        if (!long.TryParse(Text(source, "bytes"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                            size.ToString(CultureInfo.InvariantCulture) != Text(source, "bytes")) throw new ProtocolFormatException("Invalid snapshot byte length.");
                        descriptor = new(size, Text(source, "sha256"), Text(source, "relativePath"));
                        try
                        {
                            var path = SnapshotPath(descriptor.RelativePath);
                            snapshot = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await new ArtifactVerifier().VerifyAsync(snapshot, descriptor, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ProtocolFormatException)
                        {
                            if (snapshot is not null) { await snapshot.DisposeAsync().ConfigureAwait(false); snapshot = null; }
                            descriptor = null;
                            await SendAsync("failure", new { code = error is ProtocolFormatException ? "snapshot-integrity" : "snapshot-unavailable",
                                retryable = error is not ProtocolFormatException, stage = "prepare" }, message.Attempt).ConfigureAwait(false);
                            break;
                        }
                        await SendAsync("prepared", new { }, message.Attempt).ConfigureAwait(false);
                        break;
                    case "run":
                        ExactFields(message.Payload);
                        if (snapshot is null || descriptor is null) throw new InvalidOperationException("Prepared worker lost its snapshot lease.");
                        var resultName = "facts-" + message.Attempt!.LeaseId.ToString("N") + ".json";
                        var result = new CanonicalJson(64 * 1024).Encode(JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            format = "mpa-byte-facts", major = 1, sourceBytes = descriptor.Bytes.ToString(CultureInfo.InvariantCulture),
                            sourceSha256 = descriptor.Sha256, recipeSha256 = RecipeSha256
                        }));
                        CheckDirectory(_root);
                        await using (var destination = new FileStream(Path.Combine(_root, resultName), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            await destination.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                        await snapshot.DisposeAsync().ConfigureAwait(false); snapshot = null; descriptor = null;
                        await SendAsync("result", new { artifacts = new[] { new { relativePath = resultName, bytes = result.Length.ToString(CultureInfo.InvariantCulture),
                            sha256 = Convert.ToHexStringLower(SHA256.HashData(result)) } } }, message.Attempt).ConfigureAwait(false);
                        break;
                    case "cancel":
                        ExactFields(message.Payload);
                        if (snapshot is not null) { await snapshot.DisposeAsync().ConfigureAwait(false); snapshot = null; }
                        descriptor = null;
                        await SendAsync("canceled", new { }, message.Attempt).ConfigureAwait(false);
                        break;
                    case "exit":
                        ExactFields(message.Payload);
                        await SendAsync("exited", new { }, null).ConfigureAwait(false);
                        return;
                    default: throw new ProtocolFormatException("Unexpected Core message for the byte-facts worker.");
                }
            }
        }
        finally { if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false); }

        async Task SendAsync<T>(string type, T payload, WorkerAttempt? attempt)
        {
            var value = JsonSerializer.SerializeToElement(payload);
            _conversation.Observe(WorkerEndpoint.Worker, new(type, value, attempt));
            await writer.WriteAsync(_messages.Encode(type, value, attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private string SnapshotPath(string relative)
    {
        var components = relative.Split('/');
        var current = _root;
        CheckDirectory(current);
        for (var i = 0; i < components.Length; i++)
        {
            current = Path.Combine(current, components[i]);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProtocolFormatException("Snapshot path crosses a link/reparse point.");
            if (i < components.Length - 1) CheckDirectory(current);
        }
        return current;
    }
    private static void CheckDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ProtocolFormatException("Assigned staging directory is missing or redirected.");
    }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
        ? field.GetString()! : throw new ProtocolFormatException("Missing worker payload string: " + name);
    private static void ExactFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length || names.Any(name => !value.TryGetProperty(name, out _)))
            throw new ProtocolFormatException("Unexpected worker payload fields.");
    }
}
