using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Mpa.Processor.Protocol;

/// <summary>Borrowed transport. The adapter must validate actual account/workspace membership
/// before downloading and retain its connection owner until the read completes. This
/// traversal separately verifies immutable lengths/hashes; no credential or URL is accepted.</summary>
public interface ICatalogObjectSource
{
    Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken);
}
public sealed record CatalogFactsLeaf(string Prefix, CatalogChunkReference Reference, DriveCatalogChunk Facts);
public sealed class CatalogTraversalLimitException() : IOException("This catalog exceeds the configured traversal budget and cannot be accepted by this reader.");

/// <summary>Streams the bounded facts tree of an already approved signed commit into staging.
/// Earlier leaves can be emitted before a later failure; only normal full enumeration proves
/// this tree walk completed. Source coverage, ancestry/epoch agreement, policy fences and
/// transactional generation activation are additional checks, never performed by this reader.</summary>
public sealed class CatalogFactsTraversal
{
    public const int MaximumObjectBudget = 65536;
    private readonly Guid _library;
    private readonly CatalogPublicationReader _schema;
    private readonly CatalogWorkspace _workspace;
    private readonly DriveCatalogFacts _facts;
    private readonly ICatalogObjectSource _source;
    private readonly int _budget;
    public CatalogFactsTraversal(Guid library, CatalogWorkspace workspace, ICatalogObjectSource source, int objectBudget = MaximumObjectBudget)
    {
        _facts = new(library); _library = library; _schema = new(workspace); _workspace = workspace;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (objectBudget is < 1 or > MaximumObjectBudget) throw new ArgumentOutOfRangeException(nameof(objectBudget));
        _budget = objectBudget;
    }
    public IAsyncEnumerable<CatalogFactsLeaf> ReadLeavesAsync(VerifiedProtocolDocument verified, CancellationToken cancellationToken = default)
        => ReadAsync(verified, null, cancellationToken);

    /// <summary>Validate complete bounded coverage before reading photo facts. The owning
    /// generation must compare the supplied source/account policies against its enrolled
    /// approved configuration in the required callback. Normal enumeration is still only
    /// a staging read, not authority/ancestry verification or an active-generation switch.</summary>
    public IAsyncEnumerable<CatalogFactsLeaf> ReadCoveredLeavesAsync(VerifiedProtocolDocument verified,
        Func<CatalogSourceCoverageSet, CancellationToken, Task> validateConfiguration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateConfiguration);
        return ReadAsync(verified, validateConfiguration, cancellationToken);
    }
    /// <summary>Validate outgoing dependencies before signing. This does not construct
    /// a verified document or establish incoming authority, ancestry or activation.
    /// The owner must independently bind the candidate to its durable capture/current
    /// source policy and finish the actual read/change fences after full enumeration.</summary>
    public IAsyncEnumerable<CatalogFactsLeaf> ReadPreparedLeavesAsync(CatalogCommit candidate, long sequence,
        Func<CatalogSourceCoverageSet, CancellationToken, Task> validateConfiguration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validateConfiguration);
        _ = new CatalogPublicationWriter(_workspace).EncodeCommit(candidate, sequence);
        return ReadGraphAsync(candidate, validateConfiguration, cancellationToken);
    }
    private IAsyncEnumerable<CatalogFactsLeaf> ReadAsync(VerifiedProtocolDocument verified,
        Func<CatalogSourceCoverageSet, CancellationToken, Task>? validateConfiguration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verified); cancellationToken.ThrowIfCancellationRequested();
        if (verified.LibraryId != _library || !verified.RequiredCapabilities.Contains(DriveCatalogFacts.Capability, StringComparer.Ordinal))
            throw new ProtocolFormatException("The approved catalog must name this library and the observed Drive facts capability.");
        var commit = _schema.ReadCommit(verified);
        return ReadGraphAsync(commit, validateConfiguration, cancellationToken);
    }
    private async IAsyncEnumerable<CatalogFactsLeaf> ReadGraphAsync(CatalogCommit commit,
        Func<CatalogSourceCoverageSet, CancellationToken, Task>? validateConfiguration, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Reserve(CatalogObjectReference reference)
        {
            if (!visited.Add(reference.FileId)) throw new ProtocolFormatException("Catalog graph contains an aliased object, role or cycle.");
            if (visited.Count > _budget) throw new CatalogTraversalLimitException();
        }
        // These roles belong to the enclosing generation but cannot be reused as tree nodes.
        Reserve(commit.SourceCoverage); if (commit.Parent is not null) Reserve(commit.Parent);
        Reserve(commit.RootIndex);
        CatalogSourceCoverageSet? coverage = null;
        if (validateConfiguration is not null)
        {
            var root = _schema.ReadCoverageRoot(await ReadMetadataAsync(commit.SourceCoverage, cancellationToken).ConfigureAwait(false), _library);
            foreach (var descriptor in root.Pages) Reserve(descriptor.Reference);
            var pages = new List<CatalogCoveragePage>();
            foreach (var descriptor in root.Pages)
            {
                var bytes = await ReadMetadataAsync(descriptor.Reference, cancellationToken).ConfigureAwait(false);
                pages.Add(_schema.ReadCoveragePage(bytes, root, descriptor));
            }
            coverage = new(root, pages); cancellationToken.ThrowIfCancellationRequested();
            await validateConfiguration(coverage, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        var pending = new Stack<Pending>(); pending.Push(new("", commit.RootIndex, null));
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Chunk is not null)
            {
                var facts = await ReadChunkAsync(entry.Chunk, entry.Prefix, cancellationToken).ConfigureAwait(false);
                if (coverage is not null) foreach (var asset in facts.Assets) coverage.ValidateAsset(asset);
                cancellationToken.ThrowIfCancellationRequested(); yield return new(entry.Prefix, entry.Chunk, facts);
            }
            else
            {
                var node = await ReadIndexAsync(entry.Reference, entry.Prefix, cancellationToken).ConfigureAwait(false);
                // Reverse push preserves ordinal prefix/identity order with a depth-first
                // stack bounded by four levels of at most 256 children, not library size.
                for (var index = node.Children.Count - 1; index >= 0; index--)
                {
                    var child = node.Children[index]; Reserve(child.Reference);
                    pending.Push(new(entry.Prefix + child.Edge, child.Reference, child.Chunk));
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
    private async Task<CatalogIndexNode> ReadIndexAsync(CatalogObjectReference reference, string prefix, CancellationToken token)
    {
        var owned = await ReadMetadataAsync(reference, token).ConfigureAwait(false);
        var node = _schema.ReadIndex(owned, prefix); token.ThrowIfCancellationRequested(); return node;
    }
    private async Task<byte[]> ReadMetadataAsync(CatalogObjectReference reference, CancellationToken token)
    {
        var borrowed = await _source.ReadAsync(reference, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (borrowed.Length != reference.Bytes || borrowed.Length > CatalogPublicationReader.MaximumIndexBytes)
            throw new ProtocolFormatException("Catalog metadata does not match its bounded immutable length.");
        var owned = borrowed.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(owned), Convert.FromHexString(reference.Sha256)))
            throw new ProtocolFormatException("Catalog metadata does not match its immutable SHA-256.");
        token.ThrowIfCancellationRequested(); return owned;
    }
    private async Task<DriveCatalogChunk> ReadChunkAsync(CatalogChunkReference reference, string prefix, CancellationToken token)
    {
        var compressed = await _source.ReadAsync(reference.Encoded, token).ConfigureAwait(false);
        var canonical = await new CatalogChunkDecoder().DecodeAsync(compressed, reference, token).ConfigureAwait(false);
        var facts = _facts.Read(canonical, prefix); token.ThrowIfCancellationRequested(); return facts;
    }
    private sealed record Pending(string Prefix, CatalogObjectReference Reference, CatalogChunkReference? Chunk);
}
