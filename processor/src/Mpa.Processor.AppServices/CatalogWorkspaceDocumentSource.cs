using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public interface IWorkspaceDocumentSource
{
    Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, CancellationToken token);
    Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, CancellationToken token);
    Task<DriveWorkspaceObject> ReadAsync(DriveWorkspaceDocumentCandidate candidate, CancellationToken token);
}

/// <summary>Read-only document reconciliation through the engine's joined account
/// lease. Setup supplies the captured workspace; incoming documents cannot select it.
/// Returned bytes/inventories remain untrusted and confer no publication permission.</summary>
public sealed class CatalogWorkspaceDocumentSource : IWorkspaceDocumentSource
{
    private readonly ProcessorStateCoordinator _state;
    private readonly ProcessorAccountService _accounts;
    private readonly CatalogWorkspace _workspace;
    private readonly string? _sharedDriveId;
    private readonly long _accountRevision;

    public CatalogWorkspaceDocumentSource(ProcessorStateCoordinator state, ProcessorAccountService accounts,
        CatalogWorkspace workspace, string? sharedDriveId, long accountRevision)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ArgumentOutOfRangeException.ThrowIfLessThan(accountRevision, 1);
        if (!Identity(workspace.AccountId) || !Identity(workspace.FolderId) || workspace.FolderId == "root" || sharedDriveId is not null && !Identity(sharedDriveId))
            throw new ArgumentException("A captured canonical metadata workspace is required.", nameof(workspace));
        _sharedDriveId = sharedDriveId; _accountRevision = accountRevision;
    }

    public Task<WorkspaceDocumentInventory> CaptureAsync(IReadOnlyCollection<string> previouslyKnownIds, CancellationToken token) =>
        UseAsync((documents, changes, ownedToken) => new WorkspaceDocumentInventoryReader(documents, changes,
            _workspace.AccountId, _workspace.FolderId, _sharedDriveId).CaptureAsync(previouslyKnownIds, ownedToken), token);

    public Task<DriveChangeCursor> CheckUnchangedAsync(WorkspaceDocumentInventory inventory, CancellationToken token) =>
        UseAsync((documents, changes, ownedToken) => new WorkspaceDocumentInventoryReader(documents, changes,
            _workspace.AccountId, _workspace.FolderId, _sharedDriveId).CheckUnchangedAsync(inventory, ownedToken), token);

    public Task<DriveWorkspaceObject> ReadAsync(DriveWorkspaceDocumentCandidate candidate, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Identity(candidate.FileId) || candidate.FileId is "root" || candidate.FileId == _workspace.FolderId || candidate.Version < 1 ||
            candidate.Bytes is < 1 or > GoogleAccountConnection.MaximumWorkspaceDocumentBytes) throw new ArgumentException("A bounded metadata document candidate is required.", nameof(candidate));
        return UseAsync(async (documents, _, ownedToken) =>
        {
            var result = await documents.ReadWorkspaceDocumentAsync(_workspace.FolderId, candidate, ownedToken).ConfigureAwait(false);
            if (result.AccountId != _workspace.AccountId || result.FolderId != _workspace.FolderId || result.FileId != candidate.FileId ||
                result.DriveVersion != candidate.Version || result.Bytes.Length != candidate.Bytes) throw new InvalidDataException("Document transport substituted a captured identity, revision or size.");
            return result;
        }, token);
    }

    private Task<T> UseAsync<T>(Func<IGoogleWorkspaceDiscovery, IGoogleWorkspaceChanges, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        _accounts.ReadDriveAsync(_workspace.AccountId, async (reader, revision, token) =>
        {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            if (revision != _accountRevision) throw new StaleLibraryConfigurationException();
            if (reader is not IGoogleWorkspaceDiscovery documents || reader is not IGoogleWorkspaceChanges changes)
                throw new InvalidOperationException("The owned account reader does not support workspace document reconciliation.");
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
}
