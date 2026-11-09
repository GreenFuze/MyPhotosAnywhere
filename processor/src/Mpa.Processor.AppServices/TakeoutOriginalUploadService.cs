using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

/// <summary>Native current original-action admission for this exact intent. Must
/// verify current controller, captured approval/selection and destination policy.
/// Called on the serial state owner, including inside a borrowed account lease:
/// bounded synchronous state checks only, no IO, signing or account acquisition.
/// Metadata publication authority does not implement this original-write grant.</summary>
public interface ITakeoutOriginalAuthority
{
    // Outside the account lease: fresh Root/policy/final-fence observation.
    // Returns a separate current access revision; captured intent stays unchanged.
    Task<long> RefreshAsync(TakeoutOriginalWork work,CancellationToken token);
    void AssertCurrent(ProcessorStateDatabase database,TakeoutOriginalWork work);
}

/// <summary>One sequential original-transfer owner. Holds source through complete
/// hashing/replay; joins every request before returning its durable attempt.
/// Unknown outcomes retain the first ID and protected provider session. Scheduling
/// and current original-action enrollment belong to the desktop workflow owner.</summary>
public sealed class TakeoutOriginalUploadService(ProcessorStateCoordinator state,ProcessorAccountService accounts,
    StorageBudgetManager budget,ITakeoutOriginalAuthority authority,CancellationToken retirement=default) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state=state??throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts=accounts??throw new ArgumentNullException(nameof(accounts));
    private readonly StorageBudgetManager _budget=budget??throw new ArgumentNullException(nameof(budget));
    private readonly ITakeoutOriginalAuthority _authority=authority??throw new ArgumentNullException(nameof(authority));
    private readonly CancellationToken _retirement=retirement;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Attempt? _retained;
    public bool ExitConfirmed {get{lock(_gate)return _exitConfirmed;}}
    public Exception? Failure {get;private set;}

    public Task<DriveOriginalObject> UploadAsync(Guid jobId,CancellationToken caller)=>UploadOwnedAsync(jobId,null,null,caller);
    public Task<DriveOriginalObject> UploadPreparedAsync(Guid jobId,TakeoutAssociationCatalog catalog,TakeoutOriginalReader source,CancellationToken caller)
    {ArgumentNullException.ThrowIfNull(catalog);ArgumentNullException.ThrowIfNull(source);return UploadOwnedAsync(jobId,catalog,source,caller);}
    private async Task<DriveOriginalObject> UploadOwnedAsync(Guid jobId,TakeoutAssociationCatalog? catalog,TakeoutOriginalReader? prepared,CancellationToken caller)
    {
        CancellationTokenSource linked;
        lock(_gate){ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token,_retirement);}
        using(linked)
        {
            caller.ThrowIfCancellationRequested();await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            var owned=new Attempt();Exception? failure=null;DriveOriginalObject? result=null;
            try
            {
                Check(caller,linked.Token);
                lock(_gate)if(_retained is not null)throw new InvalidOperationException("Retire the previous original IO/state owners before retrying.");
                owned.Work=await _state.ExecuteAsync(database=>
                {
                    lock(_gate)
                    {
                        Check(caller,linked.Token);return new TakeoutOriginalJobRepository(database).Claim(jobId)??throw new InvalidOperationException("Original upload is not runnable.");
                    }
                },linked.Token).ConfigureAwait(false);
                await RefreshAsync(owned,caller,linked.Token).ConfigureAwait(false);
                await AdmitAsync(owned,caller,linked.Token).ConfigureAwait(false);
                owned.Borrowed=catalog is not null;owned.Catalog=catalog??await TakeoutAssociationCatalog.OpenCompletedAsync(owned.Work.Capture.Inventory,_budget,linked.Token).ConfigureAwait(false);
                // The original hash/replay owner performs native IO outside SQLite.
                owned.Source=prepared??await TakeoutOriginalReader.CaptureAsync(owned.Work.Capture.Inventory,owned.Catalog,owned.Work.Capture.Entry,linked.Token).ConfigureAwait(false);
                if(owned.Source.Position!=0||owned.Source.Capture.Entry!=owned.Work.Capture.Entry||owned.Source.Capture.Inventory.ImportId!=owned.Work.Capture.Inventory.ImportId||
                    owned.Source.Capture.Inventory.Target!=owned.Work.Capture.Inventory.Target||owned.Source.Capture.Sha256!=owned.Work.Capture.Sha256)
                    throw new InvalidDataException("Original content changed; retain its first pending upload and ID.");
                await AdmitAsync(owned,caller,linked.Token).ConfigureAwait(false);
                if(owned.Work.FileId is null)
                {
                    await RefreshAsync(owned,caller,linked.Token).ConfigureAwait(false);
                    await _accounts.ReadDriveAsync(owned.Work.Capture.Inventory.Target.AccountId,async(connection,revision,token)=>
                    {
                        CheckAccount(owned,revision,caller,token);
                        if(connection is not IGoogleWorkspaceObservedAllocation allocator)throw new InvalidOperationException("Original upload requires observed canonical allocation.");
                        return await allocator.ObserveWorkspaceObjectIdAllocationAsync(new AllocationObserver(this,owned),
                            admission=>AdmitAsync(owned,caller,admission),token).ConfigureAwait(false);
                    },linked.Token).ConfigureAwait(false);
                }
                var capture=TransportCapture(owned.Work);
                var progress=await RequestAsync(owned,(transport,admit,token)=>transport.PrepareOriginalAsync(capture,admit,token),caller,linked.Token).ConfigureAwait(false);
                await SaveProgressAsync(owned,progress,caller,linked.Token).ConfigureAwait(false);
                var buffer=new byte[TakeoutOriginalReader.MaximumReadBytes];
                try
                {
                    var buffered=0;long bufferStart=0;
                    // Hash every skipped prefix byte once. Never seek/skip source
                    // verification merely because the server retained an offset.
                    while(owned.Source.Position<progress.AcknowledgedBytes)
                    {
                        Check(caller,linked.Token);var count=(int)Math.Min(buffer.Length,progress.AcknowledgedBytes-owned.Source.Position);
                        if(await owned.Source.ReadAsync(buffer.AsMemory(0,count),linked.Token).ConfigureAwait(false)==0)throw new InvalidDataException("Original ended before the actual server offset.");
                    }
                    bufferStart=progress.AcknowledgedBytes;
                    while(progress.CompletedObject is null)
                    {
                        Check(caller,linked.Token);
                        var discard=checked((int)(progress.AcknowledgedBytes-bufferStart));
                        if(discard<0||discard>buffered)throw new InvalidDataException("Original acknowledgement escaped its retained bounded chunk.");
                        buffer.AsSpan(discard,buffered-discard).CopyTo(buffer);buffered-=discard;bufferStart=progress.AcknowledgedBytes;
                        while(buffered<buffer.Length&&owned.Source.Position<capture.Bytes)
                        {
                            var read=await owned.Source.ReadAsync(buffer.AsMemory(buffered),linked.Token).ConfigureAwait(false);
                            if(read==0)throw new InvalidDataException("Original ended before its captured byte count.");buffered+=read;
                        }
                        if(buffered==0)throw new InvalidDataException("The provider did not independently complete the fully acknowledged original.");
                        if(owned.Source.Position==capture.Bytes)await VerifyReplayEofAsync(owned.Source,linked.Token).ConfigureAwait(false);
                        var sent=buffered;var offset=progress.AcknowledgedBytes;
                        progress=await RequestAsync(owned,(transport,admit,token)=>transport.UploadOriginalChunkAsync(capture,offset,buffer.AsMemory(0,sent),admit,token),caller,linked.Token).ConfigureAwait(false);
                        if(progress.AcknowledgedBytes<=offset||progress.AcknowledgedBytes>offset+sent)throw new InvalidDataException("Provider returned impossible original progress.");
                        await SaveProgressAsync(owned,progress,caller,linked.Token).ConfigureAwait(false);
                    }
                    // An already-completed same-ID object still requires complete
                    // local replay before admitting any durable backup receipt.
                    while(owned.Source.Position<capture.Bytes)
                        if(await owned.Source.ReadAsync(buffer,linked.Token).ConfigureAwait(false)==0)throw new InvalidDataException("Original ended during completed-object verification.");
                    await VerifyReplayEofAsync(owned.Source,linked.Token).ConfigureAwait(false);
                    result=progress.CompletedObject;
                    var verified=await VerifyRemoteAsync(owned,result,caller,linked.Token).ConfigureAwait(false);
                    await _state.ExecuteAsync(database=>
                    {
                        lock(_gate)
                        {
                            Check(caller,linked.Token);_authority.AssertCurrent(database,owned.Work);new TakeoutOriginalJobRepository(database).ConfirmVerified(owned.Work,
                                result.AccountId,result.FolderId,result.FileId,result.Bytes,result.Version,verified);owned.Completed=true;return true;
                        }
                    },linked.Token).ConfigureAwait(false);
                }
                finally {CryptographicOperations.ZeroMemory(buffer);}
            }
            catch(Exception error){failure=error;}
            try {await RetireAsync(owned).ConfigureAwait(false);}
            catch(Exception cleanup){failure=failure is null?cleanup:new AggregateException(failure,cleanup);lock(_gate)_retained=owned;}
            finally {_serial.Release();}
            if(failure is not null){lock(_gate)Failure=failure;ExceptionDispatchInfo.Capture(failure).Throw();}
            lock(_gate){Check(caller,linked.Token);Failure=null;}
            return result??throw new InvalidOperationException("Original owner retired without a verified object.");
        }
    }
    private Task<bool> AdmitAsync(Attempt owned,CancellationToken caller,CancellationToken token)=>_state.ExecuteAsync(database=>
    {lock(_gate){Check(caller,token);var work=owned.Work??throw new InvalidOperationException("Original intent has not been claimed.");new TakeoutOriginalJobRepository(database).AssertCurrent(work);_authority.AssertCurrent(database,work);Check(caller,token);return true;}},token);
    private async Task<T> RequestAsync<T>(Attempt owned,Func<IGoogleOriginalTransfer,Func<CancellationToken,Task>,CancellationToken,Task<T>> request,
        CancellationToken caller,CancellationToken token)
    {
        await RefreshAsync(owned,caller,token).ConfigureAwait(false);
        return await _accounts.ReadDriveAsync(owned.Work!.Capture.Inventory.Target.AccountId,async(connection,revision,accountToken)=>
        {
            CheckAccount(owned,revision,caller,accountToken);
            if(connection is not IGoogleOriginalTransfer transfer)throw new InvalidOperationException("The owned account does not support original transfer.");
            var result=await request(transfer,admission=>AdmitAsync(owned,caller,admission),accountToken).ConfigureAwait(false);Check(caller,accountToken);return result;
        },token).ConfigureAwait(false);
    }
    private async Task RefreshAsync(Attempt owned,CancellationToken caller,CancellationToken token)
    {
        Check(caller,token);var revision=await _authority.RefreshAsync(owned.Work!,token).ConfigureAwait(false);
        lock(_gate){Check(caller,token);if(revision<1)throw new StaleDriveConnectionException();owned.AccessRevision=revision;}
    }
    private Task<bool> SaveProgressAsync(Attempt owned,DriveOriginalUploadProgress progress,CancellationToken caller,CancellationToken token)
        =>_state.ExecuteAsync(database=>
        {
            lock(_gate)
            {
                Check(caller,token);var work=owned.Work!;
                if(progress.TransferId!=work.Lease.JobId||progress.FileId!=work.FileId)throw new InvalidDataException("Original transport substituted its captured identity.");
                _authority.AssertCurrent(database,work);owned.Work=new TakeoutOriginalJobRepository(database).ObserveAcknowledgedBytes(work,progress.AcknowledgedBytes);return true;
            }
        },token);
    private async Task<string> VerifyRemoteAsync(Attempt owned,DriveOriginalObject result,CancellationToken caller,CancellationToken token)
    {
        var work=owned.Work!;var capture=TransportCapture(work);
        if(!owned.Source!.ReplayVerified||result.AccountId!=capture.AccountId||result.FolderId!=capture.FolderId||result.FileId!=capture.FileId||result.Bytes!=capture.Bytes||result.Version<1)
            throw new InvalidDataException("Original verification lost its exact source, object or version.");
        if(result.ContentSha256 is {} sha)
        {if(sha!=capture.Sha256)throw new InvalidDataException("Provider checksum differs from the captured original.");return sha;}
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for(long offset=0;offset<capture.Bytes;)
        {
            var start=offset;var count=(int)Math.Min(TakeoutOriginalReader.MaximumReadBytes,capture.Bytes-offset);
            var bytes=await RequestAsync(owned,(transport,admit,requestToken)=>transport.ReadOriginalRangeAsync(capture,result.Version,start,count,admit,requestToken),caller,token).ConfigureAwait(false);
            try {if(bytes.Length!=count)throw new InvalidDataException("Independent original read returned a different bounded range.");hash.AppendData(bytes);offset+=count;}
            finally {CryptographicOperations.ZeroMemory(bytes);}
        }
        var verified=Convert.ToHexStringLower(hash.GetHashAndReset());
        if(verified!=capture.Sha256)throw new InvalidDataException("Independent provider bytes differ from the captured original.");return verified;
    }
    private static async Task VerifyReplayEofAsync(TakeoutOriginalReader reader,CancellationToken token)
    {if(await reader.ReadAsync(new byte[1],token).ConfigureAwait(false)!=0||!reader.ReplayVerified)throw new InvalidDataException("Original replay did not reach verified EOF.");}
    private static DriveOriginalUpload TransportCapture(TakeoutOriginalWork work)
    {var capture=work.Capture;return new(work.Lease.JobId,capture.Inventory.Target.AccountId,capture.Inventory.Target.FolderId,work.FileId??throw new InvalidOperationException("Capture the first allocated file ID before upload."),capture.Name,capture.MimeType,capture.Entry.Bytes,capture.Sha256);}
    private void CheckAccount(Attempt owned,long revision,CancellationToken caller,CancellationToken token)
    {Check(caller,token);if(revision!=owned.AccessRevision)throw new StaleDriveConnectionException();}
    private void Check(CancellationToken caller,CancellationToken token)
    {lock(_gate){caller.ThrowIfCancellationRequested();token.ThrowIfCancellationRequested();_retirement.ThrowIfCancellationRequested();if(_stopping)throw new OperationCanceledException("Original owner closed admission before result commit.",token);}}
    private async Task RetireAsync(Attempt owned)
    {
        if(owned.Source is {} source){if(!owned.Borrowed)await source.DisposeAsync().ConfigureAwait(false);owned.Source=null;}
        if(!owned.Borrowed)owned.Catalog?.Dispose();owned.Catalog=null;
        if(owned.Work is {} work&&!owned.Completed&&!owned.Returned)
        {await _state.ExecuteAsync(database=>{new DurableJobRepository(database).ReturnJoinedRead(work.Lease);return true;},CancellationToken.None).ConfigureAwait(false);owned.Returned=true;}
    }
    public ValueTask DisposeAsync()
    {lock(_gate){_stopping=true;if(_disposal is{IsFaulted:true}&&!_exitConfirmed)_disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? failure=null;try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){failure=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if(_retained is {} retained){await RetireAsync(retained).ConfigureAwait(false);lock(_gate)_retained=null;}
            if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
            _stop.Dispose();lock(_gate)_exitConfirmed=true;
        }
        catch(Exception error){lock(_gate)Failure=error;throw;}
        finally {_serial.Release();}
    }
    private sealed class Attempt
    {internal TakeoutOriginalWork? Work;internal TakeoutAssociationCatalog? Catalog;internal TakeoutOriginalReader? Source;internal bool Completed,Returned,Borrowed;internal long AccessRevision;}
    private sealed class AllocationObserver(TakeoutOriginalUploadService owner,Attempt owned) : IGoogleWorkspaceAllocationObserver
    {
        public Task RememberAllocatedIdAsync(DriveWorkspaceAllocation allocation)=>owner._state.ExecuteAsync(database=>
        {
            lock(owner._gate)
            {
                var work=owned.Work??throw new InvalidOperationException("Original allocation lost its captured intent.");
                if(allocation.AccountId!=work.Capture.Inventory.Target.AccountId)throw new InvalidDataException("Original allocation changed accounts.");
                owned.Work=new TakeoutOriginalJobRepository(database).RetainAllocatedFileId(work,allocation.FileId);return true;
            }
        },CancellationToken.None);
    }
}
