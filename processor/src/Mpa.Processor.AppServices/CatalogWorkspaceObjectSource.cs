using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Read-only catalog transport borrowing the engine's existing account owner.
/// A captured workspace is authority supplied by setup, never by an incoming reference.
/// The owner joins network/refresh before retirement; generation activation is separate.</summary>
public sealed class CatalogWorkspaceObjectSource : ICatalogObjectSource
{
    private readonly ProcessorStateCoordinator _state;
    private readonly ProcessorAccountService _accounts;
    private readonly CatalogWorkspace _workspace;
    private readonly long _accountRevision;

    public CatalogWorkspaceObjectSource(ProcessorStateCoordinator state, ProcessorAccountService accounts,
        CatalogWorkspace workspace, long accountRevision)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ArgumentOutOfRangeException.ThrowIfLessThan(accountRevision, 1);
        if (workspace.FolderId == "root") throw new ArgumentException("A selected metadata workspace folder is required.", nameof(workspace));
        _accountRevision = accountRevision;
    }

    public async Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.AccountId != _workspace.AccountId || reference.WorkspaceId != _workspace.FolderId ||
            reference.FileId is null || reference.FileId.Length is < 1 or > 256 ||
            reference.FileId is "root" || reference.FileId == _workspace.FolderId ||
            reference.FileId.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')) ||
            reference.Bytes is < 1 or > GoogleAccountConnection.MaximumWorkspaceObjectBytes ||
            reference.Sha256 is null || reference.Sha256.Length != 64 ||
            reference.Sha256.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolFormatException("Catalog object reference is outside its captured metadata workspace or bounds.");

        return await _accounts.ReadDriveAsync(_workspace.AccountId, async (reader, revision, token) =>
        {
            if (revision != _accountRevision) throw new StaleLibraryConfigurationException();
            var result = await reader.ReadWorkspaceObjectAsync(_workspace.FolderId, reference.FileId,
                reference.Bytes, reference.Sha256, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            if (result.AccountId != _workspace.AccountId || result.FolderId != _workspace.FolderId || result.FileId != reference.FileId)
                throw new InvalidDataException("The owned Drive reader returned a different metadata object identity.");
            await _state.ExecuteAsync(database =>
            {
                if (!new DriveAccountRepository(database).Accounts().Any(account => account.PermissionId == _workspace.AccountId &&
                    account.Connected && account.Revision == _accountRevision)) throw new StaleLibraryConfigurationException();
                return true;
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            return result.Bytes;
        }, cancellationToken).ConfigureAwait(false);
    }
}
