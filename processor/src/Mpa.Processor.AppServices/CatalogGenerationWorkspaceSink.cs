using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Durable create-only dependency adapter borrowing the existing state,
/// account and publication-authority owners. A generation owner must establish its
/// frozen scope and exclusive head admission before use. This adapter cannot write
/// signed commits, controller policies, original media or an active-head pointer.</summary>
public sealed class CatalogGenerationWorkspaceSink : ICatalogGenerationObjectSink
{
    private readonly ProcessorStateCoordinator _state;
    private readonly StorageBudgetManager _budget;
    private readonly IWorkspacePublicationAuthority _authority;
    private readonly WorkspacePublicationScope _scope;
    private readonly WorkspacePublicationUploader _uploader;
    private readonly CatalogWorkspaceObjectSource _readback;
    private readonly CatalogGenerationLocalAccess? _localAccess;

    public CatalogGenerationWorkspaceSink(ProcessorStateCoordinator state, ProcessorAccountService accounts,
        StorageBudgetManager budget, IWorkspacePublicationAuthority authority, WorkspacePublicationScope scope, CatalogGenerationLocalAccess? localAccess = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _localAccess = localAccess;
        ArgumentNullException.ThrowIfNull(accounts);
        if (scope.LibraryId == Guid.Empty || scope.ActorId == Guid.Empty || scope.GenerationId == Guid.Empty || scope.ActorEpoch < 1 ||
            scope.AccountRevision < 1 || scope.ConfigurationRevision < 1 || !Identity(scope.AccountId) || !Identity(scope.FolderId) || scope.FolderId == "root" ||
            scope.AuthoritySha256 is not { Length: 64 } || scope.AuthoritySha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A complete captured publication scope is required.", nameof(scope));
        _uploader = new(state, accounts, budget, authority, localAccess);
        _readback = new(state, accounts, new(scope.AccountId, scope.FolderId), localAccess?.AccountRevision ?? scope.AccountRevision);
    }

    public async Task<CatalogObjectReference> StoreAsync(string role, string mimeType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (role == "facts-chunk" ? mimeType != "application/gzip" : role is not ("facts-index" or "coverage-page" or "coverage-root") || mimeType != "application/json")
            throw new ArgumentException("Only bounded catalog dependency roles and their exact MIME types may use this sink.");
        if (payload.Length is < 1 or > WorkspacePublicationRepository.MaximumPayloadBytes || mimeType == "application/json" && payload.Length > ProtocolDocumentReader.MaximumDocumentBytes)
            throw new ArgumentException("Catalog dependency payload exceeds its role's bound.", nameof(payload));
        // Own the exact bytes before awaiting authority or another serial owner. A
        // caller's mutable buffer cannot change a prepared upload while it is queued.
        var owned = payload.ToArray();
        if (mimeType == "application/json" && !new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(owned).AsSpan().SequenceEqual(owned))
            throw new ProtocolFormatException("Catalog dependency JSON must be canonical before publication.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(owned)); var id = ObjectId(_scope, role, mimeType, hash);
        await _authority.ValidateAsync(_scope, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        var prepared = await _state.ExecuteAsync(database => new WorkspacePublicationRepository(database, _budget)
            .Prepare(id, _scope, role, mimeType, owned, _localAccess), cancellationToken).ConfigureAwait(false);
        var uploaded = await _uploader.UploadAsync(prepared.Id, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        if (uploaded.Scope != _scope || uploaded.Id != id || uploaded.Role != role || uploaded.MimeType != mimeType || uploaded.Sha256 != hash ||
            uploaded.FileId is null || uploaded.VerifiedVersion is null || uploaded.Payload.Length != owned.Length)
            throw new InvalidDataException("The durable dependency upload changed its captured context or lost verification.");
        var reference = new CatalogObjectReference(_scope.AccountId, uploaded.FileId, _scope.FolderId, owned.Length, hash);
        // A receipt only proves an earlier upload. Even a byte-for-byte retry must
        // verify current remote membership/type/size/hash through the joined reader.
        var current = await _readback.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (current.Length != owned.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current.Span), Convert.FromHexString(hash)))
            throw new InvalidDataException("The dependency's current read-back differs from its captured bytes.");
        await _authority.ValidateAsync(_scope, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        await _state.ExecuteAsync(database => { new WorkspacePublicationRepository(database, _budget).AssertCurrent(id, _localAccess); return true; }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return reference;
    }

    internal static Guid ObjectId(WorkspacePublicationScope scope, string role, string mimeType, string hash)=>
        CatalogDependencyPayload.ObjectId(scope,role,mimeType,hash);
    private static bool Identity(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
