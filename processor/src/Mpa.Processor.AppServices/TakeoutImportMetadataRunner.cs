using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

public sealed record TakeoutImportMetadataResult(long Analyzed,long Retained,long Unsupported,long Unmatched,long WaitingForOriginal);

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

    public async Task<TakeoutImportMetadataResult> RunAsync(TakeoutCompletedInventory completed,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(completed);token.ThrowIfCancellationRequested();
        if(Interlocked.CompareExchange(ref _running,1,0)!=0)throw new ProcessorWorkStateConflictException();
        try
        {
            using var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,_budget,token).ConfigureAwait(false);
            long analyzed=0,retained=0,unsupported=0,unmatched=0,waiting=0;string? after=null;
            while(true)
            {
                var page=catalog.ReadPage(TakeoutEntryKind.JsonSidecar,after,token:token);
                foreach(var sidecar in page.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if(sidecar.Bytes is <1 or >TakeoutMetadataDocument.MaximumSourceBytes){unsupported++;continue;}
                    using var reservation=_budget.Reserve(checked(sidecar.Bytes*4+65536));reservation.RecheckBeforeWrite(reservation.Bytes);
                    var raw=await ReadSidecarAsync(completed,catalog,sidecar,token).ConfigureAwait(false);
                    TakeoutMetadataDocument document;
                    try{document=TakeoutMetadataDocument.ParseSource(raw);}
                    catch(TakeoutMetadataIssueException){unsupported++;continue;}
                    var association=catalog.AssociateMetadata(sidecar.Key,document);
                    if(association.Status!="matched"){unmatched++;continue;}
                    var original=await _state.ExecuteAsync(db=>new TakeoutOriginalJobRepository(db).ReadVerified(completed.ImportId,association.Media!.Key),token).ConfigureAwait(false);
                    if(original is null){waiting++;continue;}
                    var descriptor=new ArtifactDescriptor(raw.Length,document.SourceSha256,"input.bin");
                    var work=await _state.ExecuteAsync(db=>
                    {
                        var jobs=new TakeoutMetadataJobRepository(db);var id=jobs.EnsureImported(original,association,descriptor);return jobs.Claim(id);
                    },token).ConfigureAwait(false);
                    if(work is null){retained++;continue;}
                    await AnalyzeAsync(work,raw,token).ConfigureAwait(false);analyzed++;
                }
                if(!page.HasMore)break;after=page.Entries[^1].Key;
            }
            return new(analyzed,retained,unsupported,unmatched,waiting);
        }
        finally{Volatile.Write(ref _running,0);}
    }

    private static async Task<byte[]> ReadSidecarAsync(TakeoutCompletedInventory completed,TakeoutAssociationCatalog catalog,TakeoutCapturedEntry entry,CancellationToken token)
    {
        var part=completed.Parts.Single(value=>value.PartId==entry.PartId);
        using var selected=part.Kind=="folder"?TakeoutSelectedInput.OpenFolder(part.PartId,part.Location):
            await TakeoutSelectedInput.OpenZipAsync(part.PartId,part.Location,part.Bytes!.Value,part.Sha256!,token).ConfigureAwait(false);
        await using var input=catalog.OpenCaptured(selected,entry,TakeoutMetadataDocument.MaximumSourceBytes);
        var bytes=new byte[checked((int)entry.Bytes)];await input.ReadExactlyAsync(bytes,token).ConfigureAwait(false);
        var tail=new byte[1];if(await input.ReadAsync(tail,token).ConfigureAwait(false)!=0)throw new InvalidDataException("Sidecar changed its captured byte length.");
        return bytes;
    }

    private async Task AnalyzeAsync(TakeoutMetadataWork work,byte[] raw,CancellationToken token)
    {
        TakeoutMetadataWorkerOperation? operation=null;var transferred=false;
        try
        {
            using(var source=new MemoryStream(raw,false))await _storage.CaptureAsync(work,source,token).ConfigureAwait(false);
            var installed=await _installation(token).ConfigureAwait(false);
            operation=await TakeoutMetadataWorkerOperation.CreateAsync(_state,work,_storage,installed,token).ConfigureAwait(false);
            var completion=_native.Start(operation,token);transferred=true;await completion.ConfigureAwait(false);
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
