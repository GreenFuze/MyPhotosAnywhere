using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed record TakeoutImportMetadataResult(long Analyzed,long Retained,long Unsupported,long Unmatched,long WaitingForOriginal)
{public long Unavailable{get;init;}public bool ObservationsChanged{get;init;}}
public sealed record TakeoutMetadataCleanupResult(
    [property:System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)] long Retired,
    [property:System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)] long Kept);
internal sealed class TakeoutMetadataAdmissionClosedException(Exception inner):Exception("Metadata worker admission closed before ownership transfer; captured work is retained.",inner);
internal sealed class TakeoutMetadataInputUnavailableException(Exception inner):Exception("The captured metadata input is unavailable; its original binding is retained.",inner);

/// <summary>One bounded optional sidecar at a time, after the independent original
/// copy. Borrows the engine's single native supervisor and staging owner. The
/// enclosing import operation supplies cancellation and must join this call before
/// releasing either owner. No provider write or human metadata mutation occurs.</summary>
public sealed class TakeoutImportMetadataRunner(ProcessorStateCoordinator state,StorageBudgetManager budget,
    TakeoutMetadataStageStorage storage,ProcessorWorkSupervisor native,Func<CancellationToken,Task<TakeoutWorkerInstallation>> installation)
{
    private readonly ProcessorStateCoordinator _state=state??throw new ArgumentNullException(nameof(state));
    private readonly StorageBudgetManager _budget=budget??throw new ArgumentNullException(nameof(budget));
    private readonly TakeoutMetadataStageStorage _storage=storage??throw new ArgumentNullException(nameof(storage));
    private readonly ProcessorWorkSupervisor _native=native??throw new ArgumentNullException(nameof(native));
    private readonly Func<CancellationToken,Task<TakeoutWorkerInstallation>> _installation=installation??throw new ArgumentNullException(nameof(installation));
    private int _running;

    public Task<TakeoutImportMetadataResult> RunAsync(TakeoutCompletedInventory completed,CancellationToken token)=>RunAndNotifyAsync(completed,null,token);

    // The enclosing engine owns the wake-up. Deliver committed observations even
    // when a later sidecar fails or pauses; no callback runs inside SQLite or
    // before this pass's actual file/worker owners have joined.
    internal async Task<TakeoutImportMetadataResult> RunAndNotifyAsync(TakeoutCompletedInventory completed,Action? committed,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(completed);token.ThrowIfCancellationRequested();
        if(Interlocked.CompareExchange(ref _running,1,0)!=0)throw new ProcessorWorkStateConflictException();
        var changes=new ObservationChanges();TakeoutImportMetadataResult? result=null;Exception? failure=null;
        try{result=await RunOwnedAsync(completed,changes,token).ConfigureAwait(false);}
        catch(Exception error){failure=error;}
        try
        {if(changes.Changed)committed?.Invoke();}
        catch(Exception error){failure=failure is null?error:new AggregateException("Metadata processing and its committed-observation wake-up both failed.",failure,error);}
        finally{Volatile.Write(ref _running,0);}
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result??throw new InvalidOperationException("Metadata processing returned no result.");
    }
    private sealed class ObservationChanges
    {public bool Changed{get;private set;}public void Commit()=>Changed=true;}

    public async Task<TakeoutMetadataCleanupResult> CleanupBackedUpAsync(TakeoutCompletedInventory completed,CancellationToken token)
    {
        if(Interlocked.CompareExchange(ref _running,1,0)!=0)throw new ProcessorWorkStateConflictException();
        try
        {
            var workspace=await _state.ExecuteAsync(db=>
            {
                var pin=new LibraryAuthorityRepository(db,_budget).Controller()??throw new InvalidOperationException("Library setup is required before metadata cleanup.");
                if(pin.LibraryId!=completed.Target.LibraryId)throw new InvalidDataException("Metadata cleanup belongs to another library.");return new CatalogWorkspace(pin.AccountId,pin.FolderId);
            },token).ConfigureAwait(false);
            using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,_budget,token).ConfigureAwait(false);
            long retired=0,kept=0;string? after=null;
            while(true)
            {
                var page=catalog.ReadPage(TakeoutEntryKind.JsonSidecar,after,token:token);
                foreach(var entry in page.Entries)
                {
                    var captured=await _state.ExecuteAsync(db=>
                    {
                        var ready=new TakeoutMetadataJobRepository(db).ReadCompletedImported(completed.ImportId,entry.Key);
                        return ready is null?null:new{Ready=ready,Backup=new MetadataSourceBackupRepository(db,_budget).Read(ready,workspace)};
                    },token).ConfigureAwait(false);
                    if(captured?.Backup is null){kept++;continue;}
                    if(await _storage.RetireBackedUpItemAsync(captured.Ready,captured.Backup,_state,token).ConfigureAwait(false))retired++;
                }
                if(!page.HasMore)break;after=page.Entries[^1].Key;
            }
            return new(retired,kept);
        }
        finally{Volatile.Write(ref _running,0);}
    }

    private async Task<TakeoutImportMetadataResult> RunOwnedAsync(TakeoutCompletedInventory completed,ObservationChanges changes,CancellationToken token)
    {
        using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,_budget,token).ConfigureAwait(false);
        long analyzed=0,retained=0,unsupported=0,unmatched=0,waiting=0,unavailable=0;string? after=null;
        var total=catalog.Count(TakeoutEntryKind.JsonSidecar);
        var selections=await _state.ExecuteAsync(db=>new TakeoutMetadataIssueRepository(db).AssociationCount(completed.ImportId),token).ConfigureAwait(false);
        await SaveAsync(false).ConfigureAwait(false);
        while(true)
        {
            var page=catalog.ReadPage(TakeoutEntryKind.JsonSidecar,after,token:token);
            foreach(var sidecar in page.Entries)
            {
                SidecarResult result;
                try{result=await RunSidecarAsync(completed,catalog,sidecar,changes,token).ConfigureAwait(false);}
                catch(TakeoutMetadataInputUnavailableException)
                {await RecordIssueAsync(completed,catalog,sidecar,"input-unavailable",null,token).ConfigureAwait(false);result=SidecarResult.Unavailable;}
                switch(result)
                {
                    case SidecarResult.Analyzed or SidecarResult.AnalyzedAndObserved:analyzed++;break;
                    case SidecarResult.Retained or SidecarResult.RetainedAndObserved:retained++;break;
                    case SidecarResult.Unsupported:unsupported++;break;
                    case SidecarResult.Unmatched:unmatched++;break;
                    case SidecarResult.Waiting:waiting++;break;
                    case SidecarResult.Unavailable:unavailable++;break;
                    default:throw new InvalidOperationException("Unknown native metadata result.");
                }
                await SaveAsync(false).ConfigureAwait(false);
            }
            if(!page.HasMore)break;after=page.Entries[^1].Key;
        }
        await SaveAsync(waiting==0&&unavailable==0).ConfigureAwait(false);return new(analyzed,retained,unsupported,unmatched,waiting){Unavailable=unavailable,ObservationsChanged=changes.Changed};
        Task SaveAsync(bool finished)=>_state.ExecuteAsync(db=>
        {finished=finished&&new TakeoutMetadataIssueRepository(db).AssociationCount(completed.ImportId)==selections;new TakeoutImportInventoryRepository(db).RecordMetadataProgress(completed,new(total,analyzed+retained,unsupported+unmatched+unavailable,waiting,finished));if(finished)new TakeoutImportedPhotoFactsRepository(db).CompleteObservationPass(completed.ImportId);return true;},token);
    }

    private enum SidecarResult{Analyzed,AnalyzedAndObserved,Retained,RetainedAndObserved,Unsupported,Unmatched,Waiting,Unavailable}
    private enum Observation{Unsupported,Retained,New}
    private async Task<SidecarResult> RunSidecarAsync(TakeoutCompletedInventory completed,TakeoutAssociationCatalog catalog,TakeoutCapturedEntry sidecar,ObservationChanges changes,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selection=await _state.ExecuteAsync(db=>new TakeoutMetadataIssueRepository(db).ReadAssociation(completed,sidecar.Key),token).ConfigureAwait(false);
        var previous=await _state.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(completed.ImportId,sidecar.Key),token).ConfigureAwait(false);
        if(previous is not null)
        {
            if(selection is not null&&(previous.Imported.Original.Capture.Entry!=selection.Original.Capture.Entry||previous.Source.Sha256!=selection.SourceSha256))throw new InvalidDataException("Ready metadata differs from its first explicit original selection.");
            var observation=await VerifyAndResolveAsync(previous,changes,token).ConfigureAwait(false);
            if(observation!=Observation.Unsupported)return observation==Observation.New?SidecarResult.RetainedAndObserved:SidecarResult.Retained;
            await RecordIssueAsync(completed,catalog,sidecar,"metadata-too-large",previous.Source.Sha256,token).ConfigureAwait(false);return SidecarResult.Unsupported;
        }
        var issue=await _state.ExecuteAsync(db=>new TakeoutMetadataIssueRepository(db).Find(completed,sidecar.Key),token).ConfigureAwait(false);
        if(selection is null&&issue is{Retryable:false})return issue.Code is "name-mismatch" or "missing-media" or "ambiguous-media"?SidecarResult.Unmatched:SidecarResult.Unsupported;
        var pending=await _state.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ClaimImported(completed.ImportId,sidecar.Key),token).ConfigureAwait(false);
        if(pending is not null)
        {
            var prepared=false;
            try
            {
                if(pending.Imported!.Sidecar!=sidecar)throw new InvalidDataException("Pending metadata changed its captured sidecar entry.");
                var captured=await _storage.ReadCapturedSourceAsync(pending,token).ConfigureAwait(false)??await ReadSidecarAsync(completed,catalog,sidecar,token).ConfigureAwait(false);
                prepared=true;await AnalyzeAsync(pending,captured,token).ConfigureAwait(false);return await ResolveAfterAnalysisAsync(completed,catalog,sidecar,changes,token).ConfigureAwait(false);
            }
            finally
            {
                if(!prepared)await _state.ExecuteAsync(db=>{new DurableJobRepository(db).ReturnJoinedRead(pending.Lease);return true;},CancellationToken.None).ConfigureAwait(false);
            }
        }
        if(sidecar.Bytes is <1 or >TakeoutMetadataDocument.MaximumSourceBytes)
        {await RecordIssueAsync(completed,catalog,sidecar,"metadata-too-large",null,token).ConfigureAwait(false);return SidecarResult.Unsupported;}
        using var reservation=_budget.Reserve(checked(sidecar.Bytes*4+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
        var retained = selection is null ? null : await _state.ExecuteAsync(db =>
            new TakeoutMetadataIssueRepository(db).ReadRetainedRaw(completed.ImportId, sidecar.Key), token).ConfigureAwait(false);
        var raw = retained ?? await ReadSidecarAsync(completed,catalog,sidecar,token).ConfigureAwait(false);
        TakeoutMetadataDocument document;
        try{document=TakeoutMetadataDocument.ParseSource(raw);}catch(TakeoutMetadataIssueException error)
        {
            reservation.RecheckBeforeWrite(reservation.Bytes);
            await RetainIssueSourceAsync(completed, catalog, sidecar, error.Code, raw, token).ConfigureAwait(false);
            return SidecarResult.Unsupported;
        }
        var association=selection is null?catalog.AssociateMetadata(sidecar.Key,document):catalog.AssociateMetadata(completed,selection,document);if(association.Status!="matched")
        {
            reservation.RecheckBeforeWrite(reservation.Bytes);
            await RetainIssueSourceAsync(completed, catalog, sidecar, association.Status, raw, token).ConfigureAwait(false);
            return SidecarResult.Unmatched;
        }
        var original=await _state.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).ReadVerified(completed.ImportId,association.Media!.Key),token).ConfigureAwait(false);
        if(original is null)return SidecarResult.Waiting;
        var descriptor=new ArtifactDescriptor(raw.Length,document.SourceSha256,"input.bin");
        var work=await _state.ExecuteAsync(db=>
        {var jobs=new TakeoutMetadataJobRepository(db);return jobs.Claim(jobs.EnsureImported(original,association,descriptor));},token).ConfigureAwait(false);
        if(work is null)throw new ProcessorWorkStateConflictException();
        await AnalyzeAsync(work,raw,token).ConfigureAwait(false);return await ResolveAfterAnalysisAsync(completed,catalog,sidecar,changes,token).ConfigureAwait(false);
    }

    private Task<bool> RetainIssueSourceAsync(TakeoutCompletedInventory completed, TakeoutAssociationCatalog catalog,
        TakeoutCapturedEntry sidecar, string code, byte[] raw, CancellationToken token)
    {
        var input = catalog.CaptureMetadataIssueInput(completed, sidecar);
        return _state.ExecuteAsync(db =>
        {
            new TakeoutMetadataIssueRepository(db).RecordRetainedRaw(input, code, raw);
            return true;
        }, token);
    }

    private Task<bool> RecordIssueAsync(TakeoutCompletedInventory completed,TakeoutAssociationCatalog catalog,TakeoutCapturedEntry sidecar,string code,string? hash,CancellationToken token)
    {
        var input=catalog.CaptureMetadataIssueInput(completed,sidecar);
        return _state.ExecuteAsync(db=>{new TakeoutMetadataIssueRepository(db).Record(input,code,hash);return true;},token);
    }
    private async Task<SidecarResult> ResolveAfterAnalysisAsync(TakeoutCompletedInventory completed,TakeoutAssociationCatalog catalog,TakeoutCapturedEntry sidecar,ObservationChanges changes,CancellationToken token)
    {
        var result=await _state.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(completed.ImportId,sidecar.Key)??throw new InvalidDataException("Metadata analysis returned without its native ready receipt."),token).ConfigureAwait(false);
        var observation=await VerifyAndResolveAsync(result,changes,token).ConfigureAwait(false);
        if(observation!=Observation.Unsupported)return observation==Observation.New?SidecarResult.AnalyzedAndObserved:SidecarResult.Analyzed;
        await RecordIssueAsync(completed,catalog,sidecar,"metadata-too-large",result.Source.Sha256,token).ConfigureAwait(false);return SidecarResult.Unsupported;
    }
    private async Task<Observation> VerifyAndResolveAsync(TakeoutVerifiedMetadata result,ObservationChanges changes,CancellationToken token)
    {
        var verified=await _storage.ReadVerifiedAndRetireReplayAsync(result,_state,token).ConfigureAwait(false);
        try{return await _state.ExecuteAsync(db=>{var changed=new TakeoutImportedPhotoFactsRepository(db).Retain(result,verified);if(changed)changes.Commit();new TakeoutMetadataIssueRepository(db).Resolve(result);return changed?Observation.New:Observation.Retained;},token).ConfigureAwait(false);}
        catch(CatalogChunkCapacityException)
        {await _state.ExecuteAsync(db=>{new TakeoutMetadataIssueRepository(db).ObserveProjectionCapacity(result,verified);return true;},token).ConfigureAwait(false);return Observation.Unsupported;}
    }

    private static async Task<byte[]> ReadSidecarAsync(TakeoutCompletedInventory completed,TakeoutAssociationCatalog catalog,TakeoutCapturedEntry entry,CancellationToken token)
    {
        try
        {
        var part=completed.Parts.Single(value=>value.PartId==entry.PartId);
        using var selected=part.Kind=="folder"?TakeoutSelectedInput.OpenFolder(part.PartId,part.Location):
            await TakeoutSelectedInput.OpenZipAsync(part.PartId,part.Location,part.Bytes!.Value,part.Sha256!,token).ConfigureAwait(false);
        await using var input=catalog.OpenCaptured(selected,entry,TakeoutMetadataDocument.MaximumSourceBytes);
        var bytes=new byte[checked((int)entry.Bytes)];await input.ReadExactlyAsync(bytes,token).ConfigureAwait(false);
        var tail=new byte[1];if(await input.ReadAsync(tail,token).ConfigureAwait(false)!=0)throw new InvalidDataException("Sidecar changed its captured byte length.");
        return bytes;
        }
        catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {throw new TakeoutMetadataInputUnavailableException(error);}
    }

    private async Task AnalyzeAsync(TakeoutMetadataWork work,byte[] raw,CancellationToken token)
    {
        TakeoutMetadataWorkerOperation? operation=null;var transferred=false;
        try
        {
            using(var source=new MemoryStream(raw,false))await _storage.CaptureAsync(work,source,token).ConfigureAwait(false);
            var installed=await _installation(token).ConfigureAwait(false);
            operation=await TakeoutMetadataWorkerOperation.CreateAsync(_state,work,_storage,installed,token).ConfigureAwait(false);
            Task completion;
            try{completion=_native.Start(operation,token);}
            catch(InvalidOperationException error)when(_native.Failure is null&&_native.State is ProcessorWorkState.Pausing or ProcessorWorkState.Paused or ProcessorWorkState.Stopping or ProcessorWorkState.Stopped)
            {throw new TakeoutMetadataAdmissionClosedException(error);}
            transferred=true;await completion.ConfigureAwait(false);
        }
        finally
        {
            if(!transferred)
            {
                if(operation is not null)await operation.DisposeAsync().ConfigureAwait(false);
                else await _state.ExecuteAsync(db=>{new DurableJobRepository(db).ReturnJoinedRead(work.Lease);return true;},CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
