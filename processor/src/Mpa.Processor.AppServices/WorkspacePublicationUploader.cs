using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

/// <summary>Must verify independently enrolled publisher/workspace authority for this
/// exact captured scope. Neither journal contents nor Drive properties grant authority.
/// Validation also runs inside the joined account lease: do not reacquire that account
/// or perform network reconciliation here. The owner first completes reconciliation
/// outside the upload lease; validation fences its run-owned admission and current state.</summary>
public interface IWorkspacePublicationAuthority
{
    Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken cancellationToken);
}

/// <summary>One immutable-object upload borrowing the existing account owner. Its caller
/// owns scheduling; no route/background task is enabled here. Actual network operations
/// remain joined during cancellation, credential retirement and shutdown.</summary>
public sealed class WorkspacePublicationUploader(ProcessorStateCoordinator state, ProcessorAccountService accounts,
    StorageBudgetManager budget, IWorkspacePublicationAuthority authority)
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly IWorkspacePublicationAuthority _authority = authority ?? throw new ArgumentNullException(nameof(authority));

    public async Task<WorkspacePublicationRecord> UploadAsync(Guid id, CancellationToken cancellationToken)
    {
        var captured = await _state.ExecuteAsync(database => new WorkspacePublicationRepository(database, _budget).Read(id)
            ?? throw new InvalidOperationException("The captured publication is missing."), cancellationToken).ConfigureAwait(false);
        await ValidateAsync(captured, cancellationToken).ConfigureAwait(false);
        // A retained receipt reports this earlier verified upload only. It is not proof
        // of current remote availability or authority to activate a catalog generation.
        if (captured.VerifiedVersion is not null) return captured;
        return await _accounts.ReadDriveAsync(captured.Scope.AccountId, async (connection, revision, token) =>
        {
            if (revision != captured.Scope.AccountRevision) throw new StaleLibraryConfigurationException();
            if (connection is not IGoogleWorkspacePublication writer) throw new InvalidOperationException("The owned Drive connection does not support metadata publication.");
            // Another caller may have completed this same work while this caller waited
            // for the shared account lease. Re-read before allocating another identity.
            var current = await _state.ExecuteAsync(database => new WorkspacePublicationRepository(database, _budget).Read(id)
                ?? throw new InvalidOperationException("The captured publication disappeared."), token).ConfigureAwait(false);
            if (current.Scope != captured.Scope || current.Role != captured.Role || current.MimeType != captured.MimeType || current.Sha256 != captured.Sha256)
                throw new InvalidDataException("Immutable publication context changed while waiting for its account lease.");
            captured = current;
            await ValidateAsync(captured, token).ConfigureAwait(false);
            if (captured.VerifiedVersion is not null) return captured;
            if (captured.FileId is null)
            {
                var allocated = await writer.AllocateWorkspaceObjectIdAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
                captured = await _state.ExecuteAsync(database => new WorkspacePublicationRepository(database, _budget)
                    .AssignFileId(id, captured.Sha256, allocated), token).ConfigureAwait(false);
            }
            await ValidateAsync(captured, token).ConfigureAwait(false);
            var result = await writer.CreateWorkspaceObjectAsync(captured.Scope.FolderId, captured.FileId!, captured.MimeType,
                captured.Payload, captured.Sha256, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            if (result.AccountId != captured.Scope.AccountId || result.FolderId != captured.Scope.FolderId || result.FileId != captured.FileId ||
                result.DriveVersion < 1 || result.Bytes.Length != captured.Payload.Length ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(result.Bytes.Span), Convert.FromHexString(captured.Sha256)))
                throw new InvalidDataException("The owned publication transport returned a different object, scope or payload.");
            await ValidateAsync(captured, token).ConfigureAwait(false);
            return await _state.ExecuteAsync(database =>
            {
                token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
                var journal = new WorkspacePublicationRepository(database, _budget); journal.AssertCurrent(id);
                return journal.ConfirmVerified(id, result.AccountId, result.FolderId, result.FileId, captured.Sha256, result.Bytes.Length, result.DriveVersion);
            }, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateAsync(WorkspacePublicationRecord captured, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _authority.ValidateAsync(captured.Scope, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await _state.ExecuteAsync(database => { new WorkspacePublicationRepository(database, _budget).AssertCurrent(captured.Id); return true; }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}
