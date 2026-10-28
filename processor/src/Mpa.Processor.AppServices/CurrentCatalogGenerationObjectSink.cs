using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Joined create-only sink for one original current generation. Owns its
/// allocator/uploader, borrows actual reader/state/account/storage owners, and
/// never signs, selects a catalog, reads an original or updates existing media.</summary>
public sealed class CurrentCatalogGenerationObjectSink(Guid generationId,ProcessorStateCoordinator state,ProcessorAccountService accounts,
    StorageBudgetManager budget,LocalCurrentControlReader reader) : ICatalogGenerationObjectSink,IAsyncDisposable
{
    private readonly Guid _generation=Generation(generationId);
    private readonly LocalCurrentControlReader _reader=Borrow(state,accounts,budget,reader);
    private readonly CurrentCatalogDependencyAllocationService _allocation=new(state,accounts,budget,reader);
    private readonly CurrentCatalogDependencyUploadService _upload=new(accounts,reader);
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure;}}

    public Task<CatalogObjectReference> StoreAsync(string role,string mimeType,ReadOnlyMemory<byte> payload,CancellationToken cancellationToken)
    {
        lock(_gate) ObjectDisposedException.ThrowIf(_stopping,this);
        cancellationToken.ThrowIfCancellationRequested();
        // The immutable payload owns caller bytes BEFORE this sink's queue. The
        // reader's typed local entry consumes it without touching caller memory.
        var owned=CatalogDependencyPayload.Capture(role,mimeType,payload.Span);
        return StoreOwnedAsync(owned,cancellationToken);
    }
    private async Task<CatalogObjectReference> StoreOwnedAsync(CatalogDependencyPayload input,CancellationToken caller)
    {
        CancellationTokenSource linked;
        lock(_gate) {ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                Check(caller,linked.Token);
                var row=await _reader.PrepareCurrentDependencyAsync(_generation,input,linked.Token).ConfigureAwait(false);
                if(row.Scope.GenerationId!=_generation || row.Role!=input.Role || row.MimeType!=input.MimeType || row.Sha256!=input.Sha256 ||
                    row.Id!=input.IdFor(row.Scope)) throw new WorkspacePublicationConflictException();
                var admission=await ObserveAsync(row.Id,linked.Token).ConfigureAwait(false);
                var allocated=await _allocation.AllocateAsync(admission,linked.Token).ConfigureAwait(false);RequireSame(row,allocated);
                if(allocated.FileId is null) throw new WorkspacePublicationConflictException();
                // A real assignment spends its observation. A read-only exact
                // already-assigned retry made no delta, so its actual grant stays
                // subject to the same per-request state check without forging one.
                var uploadAdmission=row.FileId==allocated.FileId?admission:await ObserveAsync(row.Id,linked.Token).ConfigureAwait(false);
                var verified=await _upload.UploadAsync(uploadAdmission,linked.Token).ConfigureAwait(false);RequireSame(allocated,verified);
                if(verified.FileId is null || verified.VerifiedVersion is null) throw new WorkspacePublicationConflictException();
                var result=new CatalogObjectReference(verified.Scope.AccountId,verified.FileId,verified.Scope.FolderId,verified.Payload.Length,verified.Sha256);
                lock(_gate) {Check(caller,linked.Token);_failure=null;return result;}
            }
            catch(Exception error) {lock(_gate) _failure=error;throw;}
            finally {_serial.Release();}
        }
    }
    private async Task<LocalCurrentCatalogDependencyCloudAdmission> ObserveAsync(Guid id,CancellationToken token)
    {
        var capture=await _reader.CaptureAsync(token).ConfigureAwait(false);
        var inventory=await _reader.ObserveWorkspaceInventoryAsync(capture,token).ConfigureAwait(false);
        return await _reader.InspectCatalogDependencyCloudAsync(inventory,id,token).ConfigureAwait(false);
    }
    private static void RequireSame(WorkspacePublicationRecord expected,WorkspacePublicationRecord actual)
    {
        if(expected.Id!=actual.Id || expected.Scope!=actual.Scope || expected.Role!=actual.Role || expected.MimeType!=actual.MimeType ||
            expected.Sha256!=actual.Sha256 || !expected.Payload.Span.SequenceEqual(actual.Payload.Span) ||
            expected.FileId is {} file && actual.FileId!=file || expected.VerifiedVersion is {} version && actual.VerifiedVersion!=version)
            throw new WorkspacePublicationConflictException();
    }
    private void Check(CancellationToken caller,CancellationToken token)
    {lock(_gate) if(_stopping || caller.IsCancellationRequested || token.IsCancellationRequested) throw new OperationCanceledException("Current dependency sink retired before result admission.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;try {await _stop.CancelAsync().ConfigureAwait(false);} catch(Exception error) {cancellationFailure=error;}
        // Account lease retirement is insufficient: join the actual allocator's
        // failed-known-ID checkpoint and the uploader before borrowed owners close.
        var allocations=_allocation.DisposeAsync().AsTask();var uploads=_upload.DisposeAsync().AsTask();
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(allocations,uploads).ConfigureAwait(false);
            if(!_allocation.ExitConfirmed || !_upload.ExitConfirmed) throw new InvalidOperationException("An actual dependency child remains owned.");
            if(cancellationFailure is not null) throw new AggregateException("Dependency sink cancellation needs attention; retain borrowed owners.",cancellationFailure);
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        finally {_serial.Release();}
    }
    private static Guid Generation(Guid id)=>id!=Guid.Empty?id:throw new ArgumentException("An original generation is required.",nameof(id));
    private static LocalCurrentControlReader Borrow(ProcessorStateCoordinator state,ProcessorAccountService accounts,StorageBudgetManager budget,LocalCurrentControlReader reader)
    {
        ArgumentNullException.ThrowIfNull(state);ArgumentNullException.ThrowIfNull(accounts);ArgumentNullException.ThrowIfNull(budget);ArgumentNullException.ThrowIfNull(reader);
        if(!reader.BorrowsDependencyAllocationOwners(state,accounts,budget)) throw new InvalidOperationException("Current dependency sink must borrow the reader's exact owners.");
        return reader;
    }
}
