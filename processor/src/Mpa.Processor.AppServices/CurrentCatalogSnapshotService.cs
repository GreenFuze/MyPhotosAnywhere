using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Owns actual current-authority private publication snapshots. Resumes the
/// same capturing/frozen file and immutable identities before new work. Borrows the
/// current reader/state; retire this service before its borrowed owners. No upload,
/// native signing, catalog activation or original-media permission exists here.</summary>
public sealed class CurrentCatalogSnapshotService : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly string _directory;
    private readonly Guid _installation;
    private readonly StorageBudgetManager _budget;
    private readonly LocalCurrentControlReader _reader;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private CatalogGenerationCaptureService? _capture;
    private Task? _disposal;
    private Exception? _failure;
    private bool _stopping,_exitConfirmed;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure;}}

    public CurrentCatalogSnapshotService(ProcessorStateCoordinator state,string directory,Guid installationId,StorageBudgetManager budget,LocalCurrentControlReader reader)
    {
        _state=state ?? throw new ArgumentNullException(nameof(state));_budget=budget ?? throw new ArgumentNullException(nameof(budget));
        _reader=reader ?? throw new ArgumentNullException(nameof(reader));_directory=Path.GetFullPath(directory);
        _installation=installationId!=Guid.Empty ? installationId : throw new ArgumentException("An installation owner is required.",nameof(installationId));
        if(!reader.BorrowsSnapshotState(state,budget)) throw new InvalidOperationException("Snapshot work must borrow its actual current reader/state/budget owners.");
    }
    public async Task<CatalogGenerationCapture> PrepareAsync(CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {AssertRunning();linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock(_gate) AssertRunning();
                return await _reader.WithCurrentSnapshotAsync(async(claim,permission,token)=>
                {
                    if(_capture is not null) throw new InvalidOperationException("Previous private snapshot owner is still retained.");
                    _capture=new(_state,_directory,_installation,_budget,permission);
                    CatalogGenerationCapture result;
                    try
                    {
                        result=await _capture.RunAsync(claim.Scope,claim.SnapshotId,claim.CommitIntentId,(frozenClaim,frozen,owned)=>
                        {
                            owned.ThrowIfCancellationRequested();var descriptor=frozenClaim.Snapshot;
                            if(frozenClaim.Phase!="frozen" || descriptor is null || descriptor.Assets!=frozen.Count ||
                                descriptor.LibraryId!=frozen.LibraryId || descriptor.ConfigurationRevision!=frozen.ConfigurationRevision ||
                                descriptor.DiscoveryRevision!=frozen.DiscoveryRevision) throw new CatalogGenerationCaptureConflictException();
                            return Task.FromResult(frozenClaim);
                        },token).ConfigureAwait(false);
                    }
                    catch(Exception error)
                    {
                        try {await CloseCaptureAsync().ConfigureAwait(false);}catch(Exception cleanup)
                        {throw new AggregateException("Current snapshot work and joined cleanup failed; retain its owners.",error,cleanup);}
                        throw;
                    }
                    await CloseCaptureAsync().ConfigureAwait(false);return result;
                },linked.Token).ConfigureAwait(false);
            }
            finally {_serial.Release();}
        }
    }
    private async Task CloseCaptureAsync()
    {
        if(_capture is null) return;
        try {await _capture.DisposeAsync().ConfigureAwait(false);}
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        if(!_capture.ExitConfirmed) {var error=new InvalidOperationException("Actual snapshot retirement is not confirmed.");lock(_gate) _failure=error;throw error;}
        _capture=null;
    }
    private void AssertRunning()
    {
        ObjectDisposedException.ThrowIf(_stopping,this);
        if(_failure is not null) throw new InvalidOperationException("Current snapshot cleanup is unconfirmed; retain borrowed owners.",_failure);
    }
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal ??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error) {cancellationFailure=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCaptureAsync().ConfigureAwait(false);
            if(cancellationFailure is not null) {lock(_gate) _failure=cancellationFailure;throw cancellationFailure;}
            _stop.Dispose();lock(_gate) {_failure=null;_exitConfirmed=true;}
        }
        finally {_serial.Release();}
    }
}
