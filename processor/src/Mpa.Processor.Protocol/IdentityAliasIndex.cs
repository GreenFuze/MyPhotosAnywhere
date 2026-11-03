namespace Mpa.Processor.Protocol;

public sealed record IdentityAliasLink(Guid AliasId, Guid PreferredId);

/// <summary>A bounded reference projection for ALREADY validated transfer/explicit-association
/// records. Equal bytes/names are never inputs. The durable owner validates receipt authority,
/// captured approval, representation and content evidence before calling this class.</summary>
public sealed class IdentityAliasIndex
{
    private readonly int _maximumAliases;
    private Dictionary<Guid, Guid> _links = [];
    private Dictionary<Guid, Guid> _resolved = [];

    public IdentityAliasIndex(int maximumAliases = 100_000)
    {
        if (maximumAliases is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumAliases));
        _maximumAliases = maximumAliases;
    }

    public Guid Resolve(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Identity is required.", nameof(id));
        return _resolved.GetValueOrDefault(id, id);
    }

    /// <summary>Atomic with respect to this serially owned projection. The caller separately
    /// commits its source facts/outbox. All old IDs remain usable. At most 64 links per batch.</summary>
    public bool ApplyValidatedBatch(IEnumerable<IdentityAliasLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var batch = links.Take(65).ToArray();
        if (batch.Length is < 1 or > 64) throw new ProtocolFormatException("Invalid alias batch bound.");
        var candidate = new Dictionary<Guid, Guid>(_links);
        var changed = false;
        foreach (var link in batch)
        {
            if (link is null || link.AliasId == Guid.Empty || link.PreferredId == Guid.Empty || link.AliasId == link.PreferredId)
                throw new ProtocolFormatException("Invalid identity alias association.");
            if (candidate.TryGetValue(link.AliasId, out var preferred))
            {
                if (preferred != link.PreferredId) throw new ProtocolFormatException("Conflicting preferred identities require explicit reconciliation.");
                continue;
            }
            if (candidate.Count == _maximumAliases) throw new ProtocolFormatException("Alias projection exceeds its configured budget.");
            candidate.Add(link.AliasId, link.PreferredId);
            changed = true;
        }
        if (!changed) return false;
        var resolved = new Dictionary<Guid, Guid>(candidate.Count);
        foreach (var alias in candidate.Keys)
        {
            if (resolved.ContainsKey(alias)) continue;
            var path = new List<Guid>();
            var visited = new HashSet<Guid>();
            var current = alias;
            while (candidate.TryGetValue(current, out var next) && !resolved.ContainsKey(current))
            {
                if (!visited.Add(current)) throw new ProtocolFormatException("Identity alias cycle.");
                path.Add(current);
                current = next;
            }
            var canonical = resolved.GetValueOrDefault(current, current);
            foreach (var previous in path) resolved.Add(previous, canonical);
        }
        _links = candidate;
        _resolved = resolved;
        return true;
    }
}
