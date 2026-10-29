using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

// Historical progress only. It is not a source/Root grant, and a transport owner
// must perform its own genuine new capture, target traversal and final fence.
internal sealed record CurrentGenerationRootPreparation(CurrentControlTransitionPlan? Transition,bool Selected);

public sealed partial class LocalCurrentControlReader
{
    internal Task<CurrentGenerationRootPreparation> PrepareGenerationRootAsync(LocalCurrentWorkspaceInventory inventory,
        Guid generation,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if(generation==Guid.Empty) throw new ArgumentException("An original generation is required.",nameof(generation));
        return OperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertSnapshotInventory(database,inventory);
            var capture=new CatalogGenerationCaptureRepository(database,_budget).Read(generation)
                ??throw new CatalogGenerationCaptureConflictException();
            if(new CurrentCatalogGenerationSelectionRepository(database,_budget).Read(generation,token) is not null)
                return new CurrentGenerationRootPreparation(null,true);
            var header=new WorkspacePublicationRepository(database,_budget).Read(capture.CommitIntentId)
                ??throw new WorkspacePublicationConflictException();
            if(capture.Phase!="commit-sealed" || header.Scope!=capture.Scope || header.Role!="catalog-commit" ||
                header.FileId is null || header.VerifiedVersion is null) throw new CatalogGenerationCaptureConflictException();
            var journal=new CurrentControlTransitionJournal(database,_budget);
            var retained=journal.ReadAll(token).Where(value=>value.GenerationId==generation).ToArray();
            if(retained.Length>1) throw new InvalidDataException("An original generation has multiple Root transitions; preserve all retained work.");
            var plan=retained.SingleOrDefault();
            if(plan is not null)
            {
                if(plan.Target.CatalogPublicationId!=header.Id) throw new CatalogGenerationCaptureConflictException();
                return new CurrentGenerationRootPreparation(plan,false);
            }
            // Derivation and reservation are within ONE actual serialized state
            // callback, so another same-process owner cannot reserve a competing
            // generation Root between inspecting the journal and preparing it.
            var fence=inventory.Fence;var head=fence.Capture.Head;
            plan=journal.PrepareCurrent(Guid.NewGuid(),fence.Attempt,new(head.Publishers,head.Sources,header.Id),
                inventory.Checkpoint,inventory.Signing,inventory.Publications,token);
            Check(caller,token);return new CurrentGenerationRootPreparation(plan,false);
        },token),caller);
    }
}
