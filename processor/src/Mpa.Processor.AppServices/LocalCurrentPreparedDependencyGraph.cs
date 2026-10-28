using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    // Called only by the joined owner that opened the actual claimed frozen file.
    // It cannot be invoked from JSON with caller bytes or a saved checkpoint.
    internal Task<CapturedCatalogPublication> SealPreparedDependencyGraphAsync(Guid generationId,CapturedCatalogCommitPlan plan,
        FrozenCatalogSnapshot frozen,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(plan);ArgumentNullException.ThrowIfNull(frozen);
        return OperationAsync(async token=>
        {
            var capture=await CaptureCoreAsync(caller,token).ConfigureAwait(false);
            var inventory=await ObserveWorkspaceInventoryCoreAsync(capture,caller,token,reuseCompletedReads:true).ConfigureAwait(false);
            var prepared=await InspectPreparedGraphAsync(generationId,plan,inventory,caller,token).ConfigureAwait(false);
            if(prepared.Publication.Basis!=plan.Basis || frozen.LibraryId!=prepared.Publication.Capture.Scope.LibraryId ||
                frozen.Count!=plan.Basis.Snapshot.Assets || frozen.ConfigurationRevision!=plan.Basis.Snapshot.ConfigurationRevision ||
                frozen.DiscoveryRevision!=plan.Basis.Snapshot.DiscoveryRevision) throw new CatalogGenerationCaptureConflictException();
            var source=new PreparedCurrentGraphSource(this,generationId,plan,prepared,inventory,caller);
            var candidate=new CatalogCommit(generationId,plan.Basis.Parent,plan.RootIndex,plan.SourceCoverage,
                plan.Basis.SettingsRevision,plan.Basis.MinimumHistoryGeneration);
            var comparison=new FrozenCatalogGraphComparison(frozen);
            await foreach(var leaf in new CatalogFactsTraversal(frozen.LibraryId,
                new(prepared.Publication.Capture.Scope.AccountId,prepared.Publication.Capture.Scope.FolderId),source,WorkspaceObservationRepository.MaximumDocuments)
                .ReadPreparedLeavesAsync(candidate,checked(plan.Basis.ParentSequence+1),(coverage,owned)=>
                {
                    Check(caller,owned);prepared.Sources.Configuration.ValidateCoverage(coverage);
                    if(coverage.Root.ConfigurationRevision!=frozen.ConfigurationRevision || coverage.Root.DiscoveryRevision!=frozen.DiscoveryRevision ||
                        !coverage.Sources.SequenceEqual(frozen.Coverage.OrderBy(value=>value.Id.ToString("D"),StringComparer.Ordinal)))
                        throw new CatalogGenerationCaptureConflictException();
                    return Task.CompletedTask;
                },token).ConfigureAwait(false))
            {
                Check(caller,token);
                await Task.Run(()=>comparison.Add(leaf,token),CancellationToken.None).ConfigureAwait(false);
                Check(caller,token);
            }
            comparison.Complete(token);
            // Complete real observations after every graph read. Prior decoded bytes
            // are not promoted into a nonce, access refresh or a last-Root fence.
            var finalCapture=await CaptureCoreAsync(caller,token).ConfigureAwait(false);
            if(finalCapture.Pin!=capture.Pin || finalCapture.Slot!=capture.Slot ||
                finalCapture.Head.EnvelopeSha256!=capture.Head.EnvelopeSha256 || finalCapture.Attempt.Version!=capture.Attempt.Version ||
                finalCapture.Attempt.AccountRevision!=capture.Attempt.AccountRevision) throw new CurrentControlObservationConflictException();
            var final=await ObserveWorkspaceInventoryCoreAsync(finalCapture,caller,token,reuseCompletedReads:true).ConfigureAwait(false);
            return await _state.ExecuteAsync(database=>
            {
                Check(caller,token);AssertSnapshotInventory(database,final);
                return new CatalogGenerationCaptureRepository(database,_budget).SealCurrentDependencyGraph(generationId,plan,
                    final.Checkpoint,final.Signing,final.Fence.Attempt,final.Publications,token);
            },token).ConfigureAwait(false);
        },caller);
    }

    private Task<CurrentPreparedCatalogBasis> InspectPreparedGraphAsync(Guid generationId,CapturedCatalogCommitPlan plan,
        LocalCurrentWorkspaceInventory inventory,CancellationToken caller,CancellationToken token)=>_state.ExecuteAsync(database=>
    {
        Check(caller,token);AssertSnapshotInventory(database,inventory);
        return new CatalogGenerationCaptureRepository(database,_budget).InspectCurrentDependencyGraph(generationId,plan,
            inventory.Checkpoint,inventory.Signing,inventory.Fence.Attempt,inventory.Publications,token);
    },token);

    private static bool SamePrepared(CurrentPreparedCatalogBasis actual,CurrentPreparedCatalogBasis expected)=>
        actual.Publication==expected.Publication && actual.Sources.Sequence==expected.Sources.Sequence && actual.Sources.EnvelopeSha256==expected.Sources.EnvelopeSha256;

    private sealed class PreparedCurrentGraphSource(LocalCurrentControlReader owner,Guid generationId,CapturedCatalogCommitPlan plan,
        CurrentPreparedCatalogBasis prepared,LocalCurrentWorkspaceInventory inventory,CancellationToken caller) : ICatalogObjectSource
    {
        public async Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference,CancellationToken cancellationToken)
        {
            owner.Check(caller,cancellationToken);
            var work=inventory.Publications.Objects.SingleOrDefault(value=>value.FileId==reference.FileId);
            if(work is null || work.Scope!=prepared.Publication.Capture.Scope || work.FileId is null || work.VerifiedVersion is null ||
                work.Role is not("facts-chunk" or "facts-index" or "coverage-page" or "coverage-root") ||
                reference.AccountId!=work.Scope.AccountId || reference.WorkspaceId!=work.Scope.FolderId || reference.Bytes!=work.Bytes || reference.Sha256!=work.Sha256)
                throw new WorkspacePublicationConflictException();
            if(!SamePrepared(await owner.InspectPreparedGraphAsync(generationId,plan,inventory,caller,cancellationToken).ConfigureAwait(false),prepared))
                throw new CatalogGenerationCaptureConflictException();
            var bytes=await new CatalogWorkspaceObjectSource(owner._state,owner._accounts,new(reference.AccountId,reference.WorkspaceId),
                inventory.Checkpoint.Context.AccountRevision).ReadAsync(reference,cancellationToken).ConfigureAwait(false);
            owner.Check(caller,cancellationToken);
            if(bytes.Length!=work.Bytes) throw new WorkspaceObservationConflictException();
            var owned=bytes.ToArray();
            if(Convert.ToHexStringLower(SHA256.HashData(owned))!=work.Sha256) throw new WorkspaceObservationConflictException();
            if(!SamePrepared(await owner.InspectPreparedGraphAsync(generationId,plan,inventory,caller,cancellationToken).ConfigureAwait(false),prepared))
                throw new CatalogGenerationCaptureConflictException();
            return owned;
        }
    }
}
