using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public interface IWorkspaceDocumentSource
{
    Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, CancellationToken token);
    Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, IWorkspaceInventoryObserver observer, CancellationToken token);
    Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, CancellationToken token);
    Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, IWorkspaceInventoryObserver observer, CancellationToken token);
    Task<DriveWorkspaceObject> ReadAsync(DriveWorkspaceDocumentCandidate candidate, CancellationToken token);
}

/// <summary>Read-only document reconciliation through the engine's joined account
/// lease. Setup supplies the captured workspace; incoming documents cannot select it.
/// Returned bytes/inventories remain untrusted and confer no publication permission.</summary>
public sealed class CatalogWorkspaceDocumentSource : IWorkspaceDocumentSource, IWorkspaceHistoryDocumentSource
{
    private readonly ProcessorStateCoordinator _state;
    private readonly ProcessorAccountService _accounts;
    private readonly CatalogWorkspace _workspace;
    private readonly string? _sharedDriveId;
    private readonly long _accountRevision;
    private readonly bool _initialPoliciesOnly;
    private readonly bool _allMetadata;
    public bool RequiresHistoryReplay => _allMetadata;

    public CatalogWorkspaceDocumentSource(ProcessorStateCoordinator state, ProcessorAccountService accounts,
        CatalogWorkspace workspace, string? sharedDriveId, long accountRevision, bool initialPoliciesOnly = false, bool allMetadata = false)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ArgumentOutOfRangeException.ThrowIfLessThan(accountRevision, 1);
        if (!Identity(workspace.AccountId) || !Identity(workspace.FolderId) || workspace.FolderId == "root" || sharedDriveId is not null && !Identity(sharedDriveId))
            throw new ArgumentException("A captured canonical metadata workspace is required.", nameof(workspace));
        if (initialPoliciesOnly && allMetadata) throw new ArgumentException("Initial bootstrap cannot adopt general catalog dependencies.", nameof(allMetadata));
        _sharedDriveId = sharedDriveId; _accountRevision = accountRevision; _initialPoliciesOnly = initialPoliciesOnly; _allMetadata = allMetadata;
    }

    public Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, CancellationToken token) =>
        UseAsync((documents, changes, ownedToken) => new WorkspaceDocumentInventoryReader(documents, changes,
            _workspace.AccountId, _workspace.FolderId, _sharedDriveId, _initialPoliciesOnly, _allMetadata).CaptureAsync(previouslyKnownIds, ownedToken), token);

    public Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, IWorkspaceInventoryObserver observer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return UseAsync((documents, changes, ownedToken) => new WorkspaceDocumentInventoryReader(documents, changes,
            _workspace.AccountId, _workspace.FolderId, _sharedDriveId, _initialPoliciesOnly, _allMetadata).CaptureAsync(previouslyKnownIds, ownedToken, observer), token);
    }

    public Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, CancellationToken token) =>
        UseAsync((documents, changes, ownedToken) => new WorkspaceDocumentInventoryReader(documents, changes,
            _workspace.AccountId, _workspace.FolderId, _sharedDriveId, _initialPoliciesOnly, _allMetadata).CheckUnchangedAsync(inventory, ownedToken), token);

    public Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, IWorkspaceInventoryObserver observer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return UseAsync((documents, changes, ownedToken) => new WorkspaceDocumentInventoryReader(documents, changes,
            _workspace.AccountId, _workspace.FolderId, _sharedDriveId, _initialPoliciesOnly, _allMetadata).CheckUnchangedAsync(inventory, ownedToken, observer), token);
    }

    public Task<DriveWorkspaceObject> ReadAsync(DriveWorkspaceDocumentCandidate candidate, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Identity(candidate.FileId) || candidate.FileId is "root" || candidate.FileId == _workspace.FolderId || candidate.Version < 1 ||
            candidate.Bytes < 1 || candidate.Bytes > WorkspaceDocumentInventoryReader.CandidateByteLimit(candidate.MimeType, _allMetadata)) throw new ArgumentException("A bounded metadata document candidate is required.", nameof(candidate));
        return UseAsync(async (documents, _, ownedToken) =>
        {
            var result = await documents.ReadWorkspaceDocumentAsync(_workspace.FolderId, candidate, ownedToken).ConfigureAwait(false);
            if (result.AccountId != _workspace.AccountId || result.FolderId != _workspace.FolderId || result.FileId != candidate.FileId ||
                result.DriveVersion != candidate.Version || result.Bytes.Length != candidate.Bytes) throw new InvalidDataException("Document transport substituted a captured identity, revision or size.");
            return result;
        }, token);
    }

    public Task<WorkspaceDocumentInventory> ReplayHistoryAsync(WorkspaceDocumentInventory inventory, DriveChangeCursor from,
        IWorkspaceHistoryObserver observer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(inventory); ArgumentNullException.ThrowIfNull(from); ArgumentNullException.ThrowIfNull(observer);
        if (!_allMetadata || inventory.AccountId != _workspace.AccountId || inventory.FolderId != _workspace.FolderId || inventory.SharedDriveId != _sharedDriveId)
            throw new ArgumentException("History replay requires the captured complete metadata source.", nameof(inventory));
        return UseAsync((_, changes, ownedToken) => new WorkspaceHistoryReplayReader(changes).ReplayAsync(inventory, from, observer, ownedToken), token);
    }

    private Task<T> UseAsync<T>(Func<IGoogleWorkspaceDiscovery, IGoogleWorkspaceChanges, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        _accounts.ReadDriveAsync(_workspace.AccountId, async (reader, revision, token) =>
        {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            if (revision != _accountRevision) throw new StaleLibraryConfigurationException();
            if (reader is not IGoogleWorkspaceDiscovery documents || reader is not IGoogleWorkspaceChanges changes)
                throw new InvalidOperationException("The owned account reader does not support workspace document reconciliation.");
            if (_allMetadata)
                documents = reader is IGoogleWorkspaceMetadataDiscovery metadata ? new AllMetadataDocuments(metadata) :
                    throw new InvalidOperationException("General reconciliation requires a complete typed metadata inventory.");
            else if (_initialPoliciesOnly)
                documents = reader is IGoogleInitialWorkspaceDiscovery initial ? new InitialDocuments(documents, initial) :
                    throw new InvalidOperationException("Initial publication requires an unfiltered workspace inventory.");
            var result = await operation(documents, changes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            await _state.ExecuteAsync(database =>
            {
                if (!new DriveAccountRepository(database).Accounts().Any(account => account.PermissionId == _workspace.AccountId &&
                    account.Connected && account.Revision == _accountRevision)) throw new StaleLibraryConfigurationException();
                return true;
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);

    private static bool Identity(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private sealed class InitialDocuments(IGoogleWorkspaceDiscovery documents, IGoogleInitialWorkspaceDiscovery initial) : IGoogleWorkspaceDiscovery
    {
        public Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken) =>
            initial.ReadInitialWorkspaceDocumentsAsync(folderId, sharedDriveId, pageToken, cancellationToken);
        public Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken) =>
            documents.ReadWorkspaceDocumentAsync(folderId, candidate, cancellationToken);
    }
    private sealed class AllMetadataDocuments(IGoogleWorkspaceMetadataDiscovery metadata) : IGoogleWorkspaceDiscovery
    {
        public async Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folderId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken)
        {
            var page = await metadata.ReadWorkspaceMetadataPageAsync(folderId, sharedDriveId, pageToken, cancellationToken).ConfigureAwait(false);
            return new(page.Objects.Select(item => new DriveWorkspaceDocumentCandidate(item.FileId, item.Version, item.Bytes, item.MimeType)).ToList().AsReadOnly(), page.NextPageToken, page.IncompleteSearch);
        }
        public Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folderId, DriveWorkspaceDocumentCandidate candidate, CancellationToken cancellationToken) =>
            metadata.ReadWorkspaceMetadataCandidateAsync(folderId, new(candidate.FileId, candidate.Version, candidate.Bytes, candidate.MimeType), cancellationToken);
    }
}
