using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed record ControllerPolicyObservation(string Kind, long ObservedSequence, long SelectedSequence, string EnvelopeSha256);

/// <summary>Observes this installation's pinned controller policies only. Real native
/// possession, exact sealed local signing history and controller signatures precede
/// monotonic policy selection. Missing/restored signing history pauses recovery instead
/// of adopting a cloud counter. This is not a general enrollment, catalog verifier or
/// publication authority; the full-scan owner persists the file observation first and
/// joins the observer before retiring its borrowed native key/state owners.</summary>
public sealed class LocalControllerPolicyObserver(ProcessorStateCoordinator state, StorageBudgetManager budget,
    LibrarySigningIdentityOwner keys, WorkspaceObservationContext context)
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly LibrarySigningIdentityOwner _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly WorkspaceObservationContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public Task<ControllerPolicyObservation> ObserveAsync(DriveWorkspaceObject document, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.AccountId != _context.AccountId || document.FolderId != _context.FolderId || document.FileId == _context.FolderId || document.FileId == "root" ||
            document.FileId is not { Length: > 0 and <= 128 } || document.FileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
            document.DriveVersion < 1 || document.Bytes.Length is < 1 or > GoogleAccountConnection.MaximumWorkspaceDocumentBytes)
            throw new ArgumentException("A current exact workspace controller-policy read is required.", nameof(document));
        token.ThrowIfCancellationRequested(); var bytes = document.Bytes.ToArray(); var native = _keys.PublicIdentity;
        return _state.ExecuteAsync(database =>
        {
            token.ThrowIfCancellationRequested();
            if (_keys.PublicIdentity != native) throw new InvalidDataException("Native controller ownership changed while the observation was queued.");
            var authority = new LibraryAuthorityRepository(database, _budget);
            var pin = authority.Controller() ?? throw new InvalidOperationException("Independently approve the pinned library controller first.");
            if (pin.LibraryId != _context.LibraryId || pin.AccountId != _context.AccountId || pin.FolderId != _context.FolderId ||
                native.LibraryId != pin.LibraryId || native.ControllerId != pin.ActorId || native.ControllerEpoch != pin.Epoch ||
                native.ControllerKeyId != pin.KeyId || native.ControllerPublicKeySpki != pin.PublicKeySpki)
                throw new InvalidDataException("The observed workspace and ready native identity do not match the pinned local controller.");
            var account = new DriveAccountRepository(database).Accounts().SingleOrDefault(account => account.PermissionId == _context.AccountId);
            if (account is null || !account.Connected || account.Revision != _context.AccountRevision) throw new StaleDriveConnectionException();
            var retained = new ProtocolSigningJournal(database, _budget).AssertRetainedCloudEnvelope(pin.ActorId, pin.Epoch, bytes);
            var selected = retained.Kind switch
            {
                "library-publisher-authorization" => authority.SelectPublisherPolicy(bytes).Sequence,
                "library-source-configuration" => authority.SelectSourceConfiguration(bytes).Sequence,
                _ => throw new InvalidDataException("A controller-policy observer cannot accept catalog or other document kinds.")
            };
            return new ControllerPolicyObservation(retained.Kind, retained.Sequence, selected,
                retained.EnvelopeSha256 ?? throw new InvalidDataException("An accepted controller observation lost its sealed envelope digest."));
        }, token);
    }
}
