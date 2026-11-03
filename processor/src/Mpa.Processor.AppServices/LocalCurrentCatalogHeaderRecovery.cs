using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    public Task<CurrentCatalogHeaderInspection> InspectCatalogHeaderAsync(LocalCurrentControlFence fence,Guid generationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(fence);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertHeaderFenceOwner(database,fence);
            var signing=new CurrentControlTransitionJournal(database,_budget).CaptureSigningInventory(fence.Attempt,token);
            return new CatalogGenerationCaptureRepository(database,_budget).InspectCurrentCatalogHeader(generationId,signing,fence.Attempt,token);
        },token),caller);
    }

    /// <summary>Recover only an already reserved original header in LOCAL journals.
    /// Keep the original body/counter and first signature. A new complete inventory
    /// and distinct per-object admission are still required for any cloud action.</summary>
    public Task<WorkspacePublicationRecord> RecoverCatalogHeaderPendingAsync(LocalCurrentControlFence fence,Guid generationId,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(fence);
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertHeaderFenceOwner(database,fence);
            var roots=new CurrentControlTransitionJournal(database,_budget);
            var signing=roots.CaptureSigningInventory(fence.Attempt,token);
            var captures=new CatalogGenerationCaptureRepository(database,_budget);
            var inspection=captures.InspectCurrentCatalogHeader(generationId,signing,fence.Attempt,token);
            var identity=_keys.PublicIdentity;
            if(identity.LibraryId!=inspection.OriginalScope.LibraryId || identity.PublisherId!=inspection.OriginalScope.ActorId ||
                identity.PublisherEpoch!=inspection.OriginalScope.ActorEpoch)
                throw new InvalidDataException("Ready native signer differs from the exact original header publisher.");
            var journal=new ProtocolSigningJournal(database,_budget);
            var intent=journal.Read(inspection.CommitIntentId) ?? throw new SigningHistoryRecoveryRequiredException();
            if(intent.Envelope is null)
            {
                // No new reservation and no arbitrary body factory. Native Seal
                // independently verifies this signature against the retained actor.
                var signature=_keys.SignCatalogDocument(intent.Body.Span);Check(caller,token);
                var actual=captures.InspectCurrentCatalogHeader(generationId,signing,fence.Attempt,token);
                if(actual.ClaimSha256!=inspection.ClaimSha256) throw new CurrentControlObservationConflictException();
                _=journal.Seal(intent.Id,intent.BodySha256,signature);
            }
            Check(caller,token);
            // Sealing changes the native inventory. Capture its actual new state;
            // do not relabel a previous complete workspace checkpoint as fresh.
            signing=roots.CaptureSigningInventory(fence.Attempt,token);
            var pending=captures.PrepareCurrentOriginalHeader(generationId,signing,fence.Attempt,token);
            Check(caller,token);return pending;
        },token),caller);
    }

    private void AssertHeaderFenceOwner(ProcessorStateDatabase database,LocalCurrentControlFence fence)
    {
        if(!ReferenceEquals(fence.Owner,_identity) || !ReferenceEquals(fence.Capture.Owner,_identity)) throw new CurrentControlObservationConflictException();
        AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
    }
}
