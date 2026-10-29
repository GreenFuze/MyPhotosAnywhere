using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Mpa.Processor.Protocol;

public sealed record CatalogHistoryCommit(CatalogObjectReference Reference, ReadOnlyMemory<byte> Envelope,
    VerifiedProtocolDocument Verified, CatalogCommit Commit);

/// <summary>Reads one enrolled publisher's complete signed predecessor chain, newest
/// first. Each yielded envelope is independently verified against the captured actor,
/// workspace, length and digest. Consumers must retain observations before requesting
/// another entry; only normal full enumeration proves ancestry completed. This is not
/// current policy, local signing-history, dependency coverage or activation authority.
/// The borrowed source owner must join actual transport before retiring its credentials.</summary>
public sealed class CatalogCommitHistoryTraversal
{
    public const int MaximumCommits = 4096;
    private readonly CatalogWorkspace _workspace;
    private readonly TrustedProtocolActor _publisher;
    private readonly ICatalogObjectSource _source;
    private readonly int _maximum;
    private readonly SignedProtocolDocuments _signed = new([CatalogPublicationReader.Capability, DriveCatalogFacts.Capability]);
    private readonly CatalogPublicationReader _schema;

    public CatalogCommitHistoryTraversal(CatalogWorkspace workspace, TrustedProtocolActor publisher,
        ICatalogObjectSource source, int maximumCommits = MaximumCommits)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCommits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCommits, MaximumCommits);
        _maximum = maximumCommits; _schema = new(workspace);
    }

    public async IAsyncEnumerable<CatalogHistoryCommit> ReadAsync(CatalogObjectReference head,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(head);
        var commits = new HashSet<string>(StringComparer.Ordinal);
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        var generations = new HashSet<Guid>();
        CatalogObjectReference? reference = head; long? expectedSequence = null;
        while (reference is not null)
        {
            cancellationToken.ThrowIfCancellationRequested(); ValidateReference(reference);
            if (dependencies.Contains(reference.FileId) || !commits.Add(reference.FileId))
                throw new ProtocolFormatException("Catalog history contains a cycle or an aliased document role.");
            if (commits.Count > _maximum) throw new CatalogTraversalLimitException();
            var borrowed = await _source.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (borrowed.Length != reference.Bytes) throw new ProtocolFormatException("Catalog predecessor length differs from its immutable reference.");
            var envelope = borrowed.ToArray();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(envelope), Convert.FromHexString(reference.Sha256)))
                throw new ProtocolFormatException("Catalog predecessor digest differs from its immutable reference.");
            var verified = _signed.VerifyAndRead(envelope, _publisher);
            var commit = _schema.ReadCommit(verified);
            if (!verified.RequiredCapabilities.Contains(DriveCatalogFacts.Capability, StringComparer.Ordinal))
                throw new ProtocolFormatException("Catalog history requires the observed Drive facts capability.");
            if (verified.Sequence > _maximum) throw new CatalogTraversalLimitException();
            if (expectedSequence is not null && verified.Sequence != expectedSequence || !generations.Add(commit.GenerationId))
                throw new ProtocolFormatException("Catalog predecessor changed its stream sequence or reused a generation identity.");
            if (commits.Contains(commit.RootIndex.FileId) || commits.Contains(commit.SourceCoverage.FileId) ||
                commit.Parent is not null && (commits.Contains(commit.Parent.FileId) || dependencies.Contains(commit.Parent.FileId)))
                throw new ProtocolFormatException("Catalog history aliases a commit with its dependencies or predecessor.");
            dependencies.Add(commit.RootIndex.FileId); dependencies.Add(commit.SourceCoverage.FileId);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(reference, envelope, verified, commit);
            cancellationToken.ThrowIfCancellationRequested();
            expectedSequence = verified.Sequence - 1; reference = commit.Parent;
        }
    }

    private void ValidateReference(CatalogObjectReference reference)
    {
        CatalogPublicationReader.CheckOpaque(reference.FileId);
        if (_workspace.FolderId == "root" || reference.AccountId != _workspace.AccountId || reference.WorkspaceId != _workspace.FolderId ||
            reference.FileId is "root" || reference.FileId == _workspace.FolderId ||
            reference.Bytes is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes || reference.Sha256 is not { Length: 64 } ||
            reference.Sha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolFormatException("Catalog history reference is outside its captured workspace or byte bound.");
    }
}
