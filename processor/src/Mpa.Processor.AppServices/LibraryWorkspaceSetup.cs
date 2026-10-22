using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed record LibraryWorkspaceInspection(DriveWorkspaceAccess Workspace, long AccountRevision,
    LibrarySourceSnapshot Sources, LibrarySigningPublicIdentity Identity, LibraryControllerPin? Controller);
public sealed record ApproveLibraryWorkspace(string AccountId, string FolderId, long AccountRevision,
    long FolderVersion, long ConfigurationRevision, string ControllerKeyId, bool AcceptSharedWorkspace, bool AssociateSelectedSources);
public sealed class LibraryWorkspaceApprovalException(string message) : InvalidOperationException(message);

/// <summary>Explicit host-owner enrollment, borrowing the engine's accounts/state/native
/// keys. Inspection never pins a key. Approval rechecks actual folder permission/revision,
/// account and selected-source revisions before atomic local pin/counter bootstrap.
/// Local approval is not proof of cloud publication, existing-library discovery or catalog
/// activation. The composition root must join its callers before retiring borrowed owners.</summary>
public sealed class LibraryWorkspaceSetup(ProcessorStateCoordinator state, ProcessorAccountService accounts,
    LibrarySigningIdentityOwner keys, StorageBudgetManager budget)
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly LibrarySigningIdentityOwner _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));

    public Task<LibraryControllerPin?> CurrentAsync(CancellationToken token) =>
        _state.ExecuteAsync(database => new LibraryAuthorityRepository(database, _budget).Controller(), token);

    public async Task<LibraryWorkspaceInspection> InspectAsync(string accountId, string folderId, CancellationToken token)
    {
        var identity = _keys.PublicIdentity;
        var observed = await _accounts.ReadDriveAsync(accountId, async (connection, revision, readToken) =>
        {
            if (connection is not IGoogleWorkspaceSetup setup) throw new InvalidOperationException("The owned Drive connection does not support workspace inspection.");
            var workspace = await setup.InspectWorkspaceAsync(folderId, readToken).ConfigureAwait(false);
            if (workspace.AccountId != accountId || workspace.FolderId != folderId) throw new InvalidDataException("Workspace inspection returned a different captured account/folder.");
            return (Workspace: workspace, AccountRevision: revision);
        }, token).ConfigureAwait(false);
        return await _state.ExecuteAsync(database =>
        {
            token.ThrowIfCancellationRequested(); var source = new LibrarySourceRepository(database).Snapshot();
            AssertAccount(database, accountId, observed.AccountRevision);
            var controller = new LibraryAuthorityRepository(database, _budget).Controller();
            if (source.Sources.Any(item => item.AccountId == accountId && item.FolderId == folderId))
                throw new LibraryWorkspaceApprovalException("Choose a metadata workspace separate from your selected photo folders.");
            return new LibraryWorkspaceInspection(observed.Workspace, observed.AccountRevision, source, identity, controller);
        }, token).ConfigureAwait(false);
    }

    public async Task<LibraryControllerPin> ApproveAsync(ApproveLibraryWorkspace approval, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (!approval.AssociateSelectedSources || approval.AccountRevision < 1 || approval.FolderVersion < 1 || approval.ConfigurationRevision < 1)
            throw new LibraryWorkspaceApprovalException("Review and approve the workspace and selected source association first.");
        var observed = await InspectAsync(approval.AccountId, approval.FolderId, token).ConfigureAwait(false);
        if (observed.Controller is null && !observed.Workspace.VerifiedEmpty)
            throw new LibraryWorkspaceApprovalException("Choose an empty metadata folder for a new library. Existing folder contents were preserved.");
        if (observed.Workspace.Shared && !approval.AcceptSharedWorkspace)
            throw new LibraryWorkspaceApprovalException("This folder is shared. Approve storing private metadata and previews there, or choose another folder.");
        if (observed.AccountRevision != approval.AccountRevision || observed.Workspace.DriveVersion != approval.FolderVersion ||
            observed.Sources.Revision != approval.ConfigurationRevision || observed.Identity.ControllerKeyId != approval.ControllerKeyId)
            throw new LibraryWorkspaceApprovalException("Workspace, account or library settings changed. Review their current state before approving.");
        return await _state.ExecuteAsync(database =>
        {
            token.ThrowIfCancellationRequested(); var sources = new LibrarySourceRepository(database).Snapshot();
            AssertAccount(database, approval.AccountId, approval.AccountRevision);
            if (sources.Revision != approval.ConfigurationRevision) throw new StaleLibraryConfigurationException();
            var identity = _keys.PublicIdentity;
            if (identity != observed.Identity) throw new InvalidDataException("Native signing identity changed during library approval.");
            return new LibraryAuthorityRepository(database, _budget).Bootstrap(identity.ControllerId, identity.ControllerEpoch,
                new(approval.AccountId, approval.FolderId), approval.AccountRevision, Convert.FromBase64String(identity.ControllerPublicKeySpki),
                new(identity.PublisherId, identity.PublisherEpoch, identity.PublisherPublicKeySpki, identity.PublisherKeyId));
        }, token).ConfigureAwait(false);
    }

    private static void AssertAccount(ProcessorStateDatabase database, string accountId, long revision)
    {
        var account = new DriveAccountRepository(database).Accounts().SingleOrDefault(item => item.PermissionId == accountId);
        if (account is null || !account.Connected || account.Revision != revision) throw new StaleDriveConnectionException();
    }
}
