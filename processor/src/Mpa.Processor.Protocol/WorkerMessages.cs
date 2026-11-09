using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public sealed record WorkerAttempt(Guid JobId, Guid AttemptId, Guid LeaseId)
{
    internal void Validate()
    {
        if (JobId == Guid.Empty || AttemptId == Guid.Empty || LeaseId == Guid.Empty)
            throw new ProtocolFormatException("Worker attempt requires three nonzero identities.");
    }
}
public sealed record WorkerMessage(string Type, JsonElement Payload, WorkerAttempt? Attempt);

/// <summary>One serial conversation's independent send/receive counters. Failed input poisons
/// the conversation; never consume later messages after a bad identity or sequence.</summary>
public sealed class WorkerMessageCodec
{
    private readonly Guid _session;
    private long _sent;
    private long _received;
    private bool _poisoned;
    private readonly CanonicalJson _canonical = new(1024 * 1024);
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "hello", "capabilities", "prepare", "prepared", "run", "progress", "checkpoint", "result", "failure", "cancel", "canceled", "exit", "exited"
    };
    private static readonly HashSet<string> SessionTypes = new(StringComparer.Ordinal) { "hello", "capabilities", "exit", "exited" };

    public WorkerMessageCodec(Guid session)
    {
        if (session == Guid.Empty) throw new ArgumentException("Worker session identity is required.", nameof(session));
        _session = session;
    }

    public byte[] Encode(string type, JsonElement payload, WorkerAttempt? attempt = null)
    {
        CheckUsable();
        Validate(type, payload, attempt);
        var sequence = checked(_sent + 1);
        var fields = new Dictionary<string, object?>
        {
            ["format"] = "mpa-worker", ["major"] = 1, ["minor"] = 0, ["type"] = type,
            ["sessionId"] = _session.ToString("D"), ["sequence"] = sequence.ToString(CultureInfo.InvariantCulture), ["payload"] = payload
        };
        if (attempt is not null)
        {
            fields["jobId"] = attempt.JobId.ToString("D"); fields["attemptId"] = attempt.AttemptId.ToString("D"); fields["leaseId"] = attempt.LeaseId.ToString("D");
        }
        var bytes = _canonical.Encode(JsonSerializer.SerializeToUtf8Bytes(fields));
        _sent = sequence;
        return bytes;
    }

    public WorkerMessage Read(ReadOnlySpan<byte> bytes)
    {
        CheckUsable();
        try
        {
            var canonical = _canonical.Encode(bytes);
            if (!bytes.SequenceEqual(canonical)) throw new ProtocolFormatException("Worker message must already be canonical.");
            using var document = JsonDocument.Parse(canonical);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != "mpa-worker")
                throw new ProtocolFormatException("Invalid worker message format.");
            if (!root.TryGetProperty("major", out var major) || major.ValueKind != JsonValueKind.Number || !major.TryGetInt32(out var majorValue) || majorValue != 1 ||
                !root.TryGetProperty("minor", out var minor) || minor.ValueKind != JsonValueKind.Number || !minor.TryGetInt32(out var minorValue) || minorValue != 0)
                throw new ProtocolCompatibilityException("Unsupported worker protocol version.");
            if (Uuid(root, "sessionId") != _session) throw new ProtocolFormatException("Worker message belongs to another launch.");
            var sequenceText = Text(root, "sequence");
            if (!long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence <= 0 ||
                sequence.ToString(CultureInfo.InvariantCulture) != sequenceText || sequence != checked(_received + 1))
                throw new ProtocolFormatException("Worker sequence is missing, repeated, or out of order.");
            var type = Text(root, "type");
            WorkerAttempt? attempt = SessionTypes.Contains(type) ? null : new(Uuid(root, "jobId"), Uuid(root, "attemptId"), Uuid(root, "leaseId"));
            if (!root.TryGetProperty("payload", out var payload)) throw new ProtocolFormatException("Worker message is missing its payload.");
            Validate(type, payload, attempt);
            var expectedFields = attempt is null ? 7 : 10;
            if (root.EnumerateObject().Count() != expectedFields) throw new ProtocolFormatException("Unexpected worker envelope fields.");
            _received = sequence;
            return new(type, payload.Clone(), attempt);
        }
        catch { _poisoned = true; throw; }
    }

    private void CheckUsable()
    {
        if (_poisoned) throw new InvalidOperationException("Worker conversation is faulted and must be replaced.");
    }
    private static void Validate(string type, JsonElement payload, WorkerAttempt? attempt)
    {
        if (!Types.Contains(type) || payload.ValueKind != JsonValueKind.Object || (SessionTypes.Contains(type) != (attempt is null)))
            throw new ProtocolFormatException("Invalid worker type, payload, or attempt scope.");
        attempt?.Validate();
    }
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ProtocolFormatException("Missing worker string: " + name);
    private static Guid Uuid(JsonElement root, string name)
    {
        var text = Text(root, name);
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.ToString("D") != text)
            throw new ProtocolFormatException("Invalid worker identity: " + name);
        return id;
    }
}
