using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

/// <summary>One exact previously assigned original header under a fresh P1 scan.
/// Borrows its real account/reader owners; never allocates, selects a head, opens
/// an original or grants permission to the rest of an obsolete generation.</summary>
public sealed class CurrentCatalogHeaderUploadService(ProcessorAccountService accounts,LocalCurrentControlReader reader) : IAsyncDisposable
{
    private readonly ProcessorAccountService _accounts=accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly LocalCurrentControlReader _reader=reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get{lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get{lock(_gate) return _failure;}}

    public async Task<WorkspacePublicationRecord> UploadAsync(LocalCurrentCatalogHeaderCloudAdmission admission,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(admission);
        CancellationTokenSource linked;
        lock(_gate){ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                Check(caller,linked.Token);_reader.AssertCatalogHeaderCloudAdmissionOwner(admission);
                var header=admission.Original.Header;
                if(header.FileId is null) throw new InvalidOperationException("Retain the original header allocation, then obtain a new complete P1 inventory before create.");
                if(header.VerifiedVersion is not null)
                    return Admit(await _reader.AssertCatalogHeaderCloudAdmissionAsync(admission,linked.Token).ConfigureAwait(false),caller,linked.Token);
                var saved=await _accounts.ReadDriveAsync(header.Scope.AccountId,async(connection,revision,token)=>
                {
                    Check(caller,token);
                    if(revision!=admission.Inventory.Checkpoint.Context.AccountRevision) throw new StaleDriveConnectionException();
                    if(connection is not IGoogleWorkspaceAdmittedPublication writer)
                        throw new InvalidOperationException("The owned Drive connection cannot admit each original-header request.");
                    var actual=await writer.CreateAdmittedWorkspaceObjectAsync(header.Scope.FolderId,header.FileId,header.MimeType,
                        header.Payload,header.Sha256,async owned=>
                        {
                            Check(caller,owned);
                            var current=await _reader.AssertCatalogHeaderCloudAdmissionAsync(admission,owned).ConfigureAwait(false);
                            if(current.Id!=header.Id || current.FileId!=header.FileId || current.VerifiedVersion is not null)
                                throw new WorkspacePublicationConflictException();
                        },token).ConfigureAwait(false);
                    Check(caller,token);
                    // This repeats actual P1/native/original-header admission inside
                    // the SAME receipt transaction. No separate check-then-write gap.
                    return await _reader.ConfirmCatalogHeaderReceiptAsync(admission,actual,token).ConfigureAwait(false);
                },linked.Token).ConfigureAwait(false);
                // Receipt spends P1. Return progress only; any next cloud operation
                // or materialized disposition requires its own actual new P2 scan.
                return Admit(saved,caller,linked.Token);
            }
            catch(Exception error){lock(_gate) _failure=error;throw;}
            finally{_serial.Release();}
        }
    }
    private WorkspacePublicationRecord Admit(WorkspacePublicationRecord row,CancellationToken caller,CancellationToken token)
    {lock(_gate){Check(caller,token);_failure=null;return row;}}
    private void Check(CancellationToken caller,CancellationToken token)
    {lock(_gate) if(_stopping || caller.IsCancellationRequested || token.IsCancellationRequested) throw new OperationCanceledException("Original-header upload retired before result admission.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate){_stopping=true;if(_disposal is{IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;
        try{await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){cancellationFailure=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if(cancellationFailure is not null) throw new AggregateException("Original-header upload cancellation needs attention; retain borrowed owners.",cancellationFailure);
            _stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        catch(Exception error){lock(_gate) _failure=error;throw;}
        finally{_serial.Release();}
    }
}
