using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>One awaited, bounded raw-source backup pass inside the current
/// generation owner. Borrows the native staging owner and sink; it never deletes
/// private evidence or turns a historical locator into current availability.</summary>
internal sealed class CurrentMetadataSourceBackupService(ProcessorStateCoordinator state,StorageBudgetManager budget,
    TakeoutMetadataStageStorage storage,CurrentCatalogGenerationObjectSink sink)
{
    public async Task PreserveAsync(FrozenCatalogSnapshot frozen,WorkspacePublicationScope scope,CancellationToken token)
    {
        await state.ExecuteAsync(database=>
        {
            if(!storage.Borrows(database,budget))throw new InvalidOperationException("Metadata backup must borrow the exact staging, state and budget owners.");
            return true;
        },token).ConfigureAwait(false);
        var workspace=new CatalogWorkspace(scope.AccountId,scope.FolderId);
        var sources=new Dictionary<string,MetadataSourceBytes>(StringComparer.Ordinal);
        var results=new List<TakeoutVerifiedMetadata>();long rawBytes=0;
        CatalogObjectReference? readReference=null;MetadataSourceBundle? readBundle=null;long readVersion=0;
        foreach(var facts in FrozenObservations(frozen,token))
        {
            token.ThrowIfCancellationRequested();
            var ready=await state.ExecuteAsync(database=>new TakeoutMetadataJobRepository(database).ReadCompletedImported(facts.ImportId,facts.SidecarKey)
                ??throw new InvalidDataException("Frozen imported observations lost their native ready result."),token).ConfigureAwait(false);
            var receipt=await state.ExecuteAsync(database=>new MetadataSourceBackupRepository(database,budget).Read(ready,workspace),token).ConfigureAwait(false);
            if(receipt is not null)
            {
                // One bounded readback is shared by its exact bundle members
                // during this awaited pass. It survives neither this pass nor
                // a different locator and supplies no retirement permission.
                if(readReference!=receipt.Bundle||readVersion<receipt.VerifiedVersion)
                {readBundle=await sink.ReadMetadataSourceBundleAsync(receipt.Bundle,receipt.VerifiedVersion,token).ConfigureAwait(false);readReference=receipt.Bundle;readVersion=receipt.VerifiedVersion;}
                await RequireFactsAsync(ready,facts,TakeoutMetadataDocument.ParseSource(readBundle!.ReadSource(facts.Source.Sha256)),token).ConfigureAwait(false);
                continue;
            }
            var document=await storage.ReadVerifiedAsync(ready,token).ConfigureAwait(false);await RequireFactsAsync(ready,facts,document,token).ConfigureAwait(false);
            var source=MetadataSourceBytes.Capture(document.CopyRawSource());
            // Reserve the codec's full header bound when deciding a split.
            // Never use a larger cloud object or truncate an input to fit.
            if(results.Count!=0&&(results.Count==MetadataSourceBundle.MaximumSources||
                !sources.ContainsKey(source.Fingerprint.Sha256)&&rawBytes+source.Fingerprint.Bytes+32768+64>MetadataSourceBundle.MaximumBytes))
                await FlushAsync().ConfigureAwait(false);
            if(sources.TryAdd(source.Fingerprint.Sha256,source))rawBytes+=source.Fingerprint.Bytes;
            results.Add(ready);
        }
        if(results.Count!=0)await FlushAsync().ConfigureAwait(false);

        async Task FlushAsync()
        {
            var bundle=MetadataSourceBundle.Capture(sources.Values);
            var reference=await sink.StoreAsync(MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,bundle.CopyPayload(),token).ConfigureAwait(false);
            var id=CatalogDependencyPayload.ObjectId(scope,MetadataSourceBundle.Role,MetadataSourceBundle.MimeType,reference.Sha256);
            await state.ExecuteAsync(database=>new MetadataSourceBackupRepository(database,budget).RecordBundle(id,results),token).ConfigureAwait(false);
            sources.Clear();results.Clear();rawBytes=0;
        }
    }
    private static IEnumerable<ImportedPhotoMetadata> FrozenObservations(FrozenCatalogSnapshot frozen,CancellationToken token)
    {
        if(frozen.ImportedObservationCount is{} expected)
        {
            string? after=null;long count=0;
            while(true)
            {
                var page=frozen.ReadImportedObservations(after,token);
                foreach(var observation in page.Observations){count=checked(count+1);if(count>expected)throw new InvalidDataException("Frozen raw metadata observation count changed.");yield return observation.Metadata;}
                if(!page.HasMore)break;
                if(page.Observations.Count==0)throw new InvalidDataException("Frozen raw metadata backup did not advance.");after=page.Observations[^1].Key;
            }
            if(count!=expected)throw new InvalidDataException("Frozen raw metadata observation set is incomplete.");
            yield break;
        }
        // Older pending files retain their original selected-facts-only contract.
        // Never add live observations to their already allocated generation/batches.
        Guid? assetAfter=null;
        while(true)
        {
            var page=frozen.ReadPage("",assetAfter,250,token);
            foreach(var asset in page.Assets)if(asset.ImportedMetadata is{} facts)yield return facts;
            if(!page.HasMore)break;
            if(page.Assets.Count==0)throw new InvalidDataException("Frozen legacy metadata backup did not advance.");assetAfter=page.Assets[^1].Id;
        }
    }
    private Task<bool> RequireFactsAsync(TakeoutVerifiedMetadata ready,ImportedPhotoMetadata facts,TakeoutMetadataDocument document,CancellationToken token)=>state.ExecuteAsync(_=>
    {
        if(!ready.DescribeImportedFacts(document).Encode().AsSpan().SequenceEqual(facts.Encode()))
            throw new InvalidDataException("Raw metadata backup changed the exact frozen imported observations or original binding.");
        return true;
    },token);
}
