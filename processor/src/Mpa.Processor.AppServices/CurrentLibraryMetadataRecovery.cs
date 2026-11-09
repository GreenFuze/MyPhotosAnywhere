using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed record RecoveredMetadataSource(Guid StageId,Guid AssetId,string Sha256,byte[] Bytes);

public sealed partial class CurrentLibraryCatalogRuntime
{
    /// <summary>Recover exact inert source bytes using the accepted signed photo
    /// and current approved workspace reads. No private import jobs, staged files,
    /// local backup receipt or browser-supplied source/hash is required. The
    /// operation is read-only and joins this engine owner's actual provider IO.</summary>
    public Task<RecoveredMetadataSource?> ReadImportedMetadataSourceAsync(Guid stageId,Guid assetId,CurrentCatalogAccountFilter? account,CancellationToken caller)
    {
        if(stageId==Guid.Empty||assetId==Guid.Empty)throw new ArgumentException("An actual canonical photo and stage are required.");
        return OperationAsync<RecoveredMetadataSource?>(async token=>
        {
            var selected=await FindAsync(token).ConfigureAwait(false);
            if(selected.Asset?.ImportedMetadata is not{} facts)return null;
            var pin=LibraryConnectionDescriptor.Decode(selected.Selection.ConnectionCode);
            var capture=await _reader.CaptureAsync(token).ConfigureAwait(false);
            if(capture.Pin.LibraryId!=pin.LibraryId||capture.Pin.AccountId!=pin.Workspace.AccountId||capture.Pin.FolderId!=pin.Workspace.FolderId)
                throw new CurrentControlObservationConflictException();
            var inventory=await _reader.ObserveWorkspaceInventorySessionAsync(capture,token).ConfigureAwait(false);
            await _reader.AssertWorkspaceInventoryUnchangedAsync(inventory,token).ConfigureAwait(false);
            var candidates=await _state.ExecuteAsync(database=>new MetadataSourceHintRepository(database,_budget).Candidates(inventory.Checkpoint.Context,facts.Source,token),token).ConfigureAwait(false);
            if(candidates.Count==0)return null;
            // Hints choose a bounded candidate only. The actual account read and
            // the signed source/result/observation fingerprints decide success.
            var candidate=candidates[0];var source=new CatalogWorkspaceObjectSource(_state,_accounts,pin.Workspace,capture.Attempt.AccountRevision);
            var bytes=await source.ReadAtLeastVersionAsync(candidate.Bundle,candidate.MinimumVersion,token).ConfigureAwait(false);
            var raw=MetadataSourceBundle.Read(bytes.Span).ReadSource(facts.Source.Sha256);
            var document=TakeoutMetadataDocument.ParseSource(raw);
            var reconstructed=ImportedPhotoMetadata.FromTakeout(facts.AssetId,facts.ImportId,facts.JobId,facts.OriginalSha256,facts.OriginalVersion,facts.SidecarKey,
                new(facts.Source.Bytes,facts.Source.Sha256,"input.bin"),new(facts.Artifact.Bytes,facts.Artifact.Sha256,"metadata.json"),document);
            if(!reconstructed.Encode().AsSpan().SequenceEqual(facts.Encode()))throw new InvalidDataException("Recovered export metadata differs from its accepted signed photo observations.");
            await _reader.AssertWorkspaceInventoryUnchangedAsync(inventory,token).ConfigureAwait(false);
            var current=await FindAsync(token).ConfigureAwait(false);
            if(current.Selection!=selected.Selection||current.Asset?.ImportedMetadata is not{} still||!still.Encode().AsSpan().SequenceEqual(facts.Encode()))
                throw new CurrentCanonicalPositionChangedException();
            return new(stageId,assetId,facts.Source.Sha256,raw);
        },caller);

        async Task<CurrentCanonicalReadAsset> FindAsync(CancellationToken token)
        {
            var result=account is null?await _canonical.FindAssetAsync(stageId,assetId,token).ConfigureAwait(false):
                await _canonical.FindAccountAssetAsync(stageId,account,assetId,token).ConfigureAwait(false);
            if(result is null||result.Selection.StageId!=stageId)throw new CurrentCanonicalPositionChangedException();
            if(result.Asset is{} asset&&(asset.Id!=assetId||account is not null&&asset.AccountId!=account.AccountId))throw new InvalidDataException("Raw metadata recovery returned another canonical photo or account.");
            return result;
        }
    }
}
