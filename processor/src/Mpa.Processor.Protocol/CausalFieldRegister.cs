using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

/// <summary>A validated human edit to one field. Actor authority is checked before this domain layer.</summary>
public sealed class HumanFieldOperation
{
    public Guid Id { get; }
    public Guid ActorId { get; }
    public long Sequence { get; }
    public Guid TargetId { get; }
    public string Field { get; }
    public ReadOnlyCollection<Guid> Parents { get; }
    public string CanonicalValue { get; }
    public string Fingerprint { get; }

    public HumanFieldOperation(Guid id, Guid actorId, long sequence, Guid targetId, string field,
        IEnumerable<Guid> parents, ReadOnlySpan<byte> value)
    {
        ArgumentNullException.ThrowIfNull(parents);
        if (id == Guid.Empty || actorId == Guid.Empty || targetId == Guid.Empty || sequence < 1)
            throw new ProtocolFormatException("Operation identities and positive actor sequence are required.");
        if (string.IsNullOrEmpty(field) || field.Length > 64 || field.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ProtocolFormatException("Invalid field identifier.");
        var parentArray = parents.Take(65).ToArray();
        if (parentArray.Length > 64 || parentArray.Any(parent => parent == Guid.Empty || parent == id) || parentArray.Distinct().Count() != parentArray.Length)
            throw new ProtocolFormatException("Invalid causal parent set.");
        Array.Sort(parentArray, (left, right) => StringComparer.Ordinal.Compare(left.ToString("D"), right.ToString("D")));
        Id = id; ActorId = actorId; Sequence = sequence; TargetId = targetId; Field = field;
        Parents = Array.AsReadOnly(parentArray);
        var codec = new CanonicalJson(64 * 1024);
        CanonicalValue = Encoding.UTF8.GetString(codec.Encode(value));
        // Sequence is a decimal STRING on the wire, so it is exact on every client.
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = id.ToString("D"), actorId = actorId.ToString("D"), sequence = sequence.ToString(CultureInfo.InvariantCulture),
            targetId = targetId.ToString("D"), field, parents = parentArray.Select(parent => parent.ToString("D")),
            value = JsonSerializer.Deserialize<JsonElement>(CanonicalValue)
        });
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(new CanonicalJson(256 * 1024).Encode(identity)));
    }
}

public sealed record CausalFieldSnapshot(ReadOnlyCollection<HumanFieldOperation> Alternatives,
    ReadOnlyCollection<HumanFieldOperation> CommonPriorValues, ReadOnlyCollection<Guid> PendingIds)
{
    public bool HasConflict => Alternatives.Count > 1;
}

/// <summary>Bounded one-field causal register, never a clock-based winner. The durable owner commits
/// operations and its outbox transactionally. Compaction is a separate, acknowledged-history protocol.</summary>
public sealed class CausalFieldRegister
{
    public const int MaximumOperations = 4096;
    private readonly Guid _target;
    private readonly string _field;
    private Dictionary<Guid, HumanFieldOperation> _operations = [];
    public CausalFieldSnapshot Snapshot { get; private set; } = new(Array.AsReadOnly(Array.Empty<HumanFieldOperation>()),
        Array.AsReadOnly(Array.Empty<HumanFieldOperation>()), Array.AsReadOnly(Array.Empty<Guid>()));

    public CausalFieldRegister(Guid target, string field)
    {
        if (target == Guid.Empty) throw new ArgumentException("A target identity is required.", nameof(target));
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        _target = target;
        _field = field;
    }

    /// <returns>False for an exact replay. Invalid input leaves the prior accepted state intact.</returns>
    public bool Apply(HumanFieldOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.TargetId != _target || operation.Field != _field) throw new ProtocolFormatException("Causal operation belongs to another field.");
        if (_operations.TryGetValue(operation.Id, out var existing))
        {
            if (existing.Fingerprint != operation.Fingerprint) throw new ProtocolFormatException("Operation ID was reused with different content.");
            return false;
        }
        if (_operations.Count == MaximumOperations) throw new ProtocolFormatException("Field history requires acknowledged compaction before more edits.");
        if (_operations.Values.Any(prior => prior.ActorId == operation.ActorId && prior.Sequence == operation.Sequence))
            throw new ProtocolFormatException("Actor sequence was reused.");
        var candidate = new Dictionary<Guid, HumanFieldOperation>(_operations) { [operation.Id] = operation };
        var snapshot = Evaluate(candidate);
        _operations = candidate;
        Snapshot = snapshot;
        return true;
    }

    private static CausalFieldSnapshot Evaluate(Dictionary<Guid, HumanFieldOperation> operations)
    {
        var remaining = operations.ToDictionary(pair => pair.Key, pair => pair.Value.Parents.Count(operations.ContainsKey));
        var children = operations.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var operation in operations.Values)
            foreach (var parentId in operation.Parents)
                if (operations.TryGetValue(parentId, out var parent))
                {
                    if (parent.ActorId == operation.ActorId && parent.Sequence >= operation.Sequence)
                        throw new ProtocolFormatException("A same-actor causal parent must precede its child.");
                    children[parentId].Add(operation.Id);
                }
        var ready = new Queue<Guid>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var active = new HashSet<Guid>();
        var seen = 0;
        while (ready.TryDequeue(out var id))
        {
            seen++;
            var operation = operations[id];
            if (operation.Parents.All(active.Contains)) active.Add(id);
            foreach (var child in children[id]) if (--remaining[child] == 0) ready.Enqueue(child);
        }
        if (seen != operations.Count) throw new ProtocolFormatException("Causal parent cycle.");
        var frontier = new HashSet<Guid>(active);
        foreach (var id in active) frontier.ExceptWith(operations[id].Parents);
        HashSet<Guid>? common = null;
        foreach (var id in frontier)
        {
            var ancestors = Ancestors(id, operations);
            if (common is null) common = ancestors; else common.IntersectWith(ancestors);
        }
        var priorFrontier = new HashSet<Guid>(common ?? []);
        foreach (var id in common ?? []) priorFrontier.ExceptWith(Ancestors(id, operations));
        HumanFieldOperation[] Values(IEnumerable<Guid> ids) => ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).Select(id => operations[id]).ToArray();
        var pending = operations.Keys.Where(id => !active.Contains(id)).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        return new(Array.AsReadOnly(Values(frontier)), Array.AsReadOnly(Values(priorFrontier)), Array.AsReadOnly(pending));
    }

    private static HashSet<Guid> Ancestors(Guid id, Dictionary<Guid, HumanFieldOperation> operations)
    {
        var found = new HashSet<Guid>();
        var pending = new Stack<Guid>(operations[id].Parents);
        while (pending.TryPop(out var parent))
            if (found.Add(parent)) foreach (var ancestor in operations[parent].Parents) pending.Push(ancestor);
        return found;
    }
}
