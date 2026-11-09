using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    // Return actual durable native progress only. The caller must obtain a new
    // genuine complete scan after this reservation spends its native inventory.
    internal Task<ProtocolSigningIntent> ReserveFreshCatalogHeaderAsync(LocalCurrentWorkspaceInventory inventory,
        Guid generation,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertSnapshotInventory(database,inventory);
            var result=new CatalogGenerationCaptureRepository(database,_budget).ReserveCurrentFreshCatalogHeader(generation,
                inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
            Check(caller,token);return result;
        },token),caller);
    }

    // Native key use is joined by the reader and restricted to the exact retained
    // intent body. This never acquires an account or performs IO in the state lane.
    internal Task<WorkspacePublicationRecord> SealFreshCatalogHeaderAsync(LocalCurrentWorkspaceInventory inventory,
        Guid generation,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertSnapshotInventory(database,inventory);
            var captures=new CatalogGenerationCaptureRepository(database,_budget);
            var inspection=captures.InspectCurrentFreshCatalogHeader(generation,inventory.Checkpoint,inventory.Signing,
                inventory.Fence.Attempt,inventory.Publications,token);
            var identity=_keys.PublicIdentity;var scope=inspection.OriginalScope;
            if(identity.LibraryId!=scope.LibraryId || identity.PublisherId!=scope.ActorId || identity.PublisherEpoch!=scope.ActorEpoch)
                throw new InvalidDataException("Current native signer differs from the captured original publisher.");
            var intent=new ProtocolSigningJournal(database,_budget).Read(inspection.CommitIntentId)
                ??throw new SigningHistoryRecoveryRequiredException();
            var signature=intent.Envelope is null?_keys.SignCatalogDocument(intent.Body.Span):null;
            Check(caller,token);
            var actual=captures.InspectCurrentFreshCatalogHeader(generation,inventory.Checkpoint,inventory.Signing,
                inventory.Fence.Attempt,inventory.Publications,token);
            if(actual.ClaimSha256!=inspection.ClaimSha256) throw new CurrentControlObservationConflictException();
            var result=captures.SealCurrentFreshCatalogHeader(actual,signature,inventory.Checkpoint,inventory.Signing,
                inventory.Fence.Attempt,inventory.Publications,token);
            Check(caller,token);return result;
        },token),caller);
    }
}
