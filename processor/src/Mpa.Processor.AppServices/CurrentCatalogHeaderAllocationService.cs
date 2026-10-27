using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

// Do not expose a caller-created scope/payload or a scope-wide old-source grant.
public sealed class LocalCurrentCatalogHeaderCloudAdmission
{
    internal object Owner {get;}
    internal LocalCurrentWorkspaceInventory Inventory {get;}
    public CurrentCatalogHeaderCloudInspection Original {get;}
    internal LocalCurrentCatalogHeaderCloudAdmission(object owner,LocalCurrentWorkspaceInventory inventory,
        CurrentCatalogHeaderCloudInspection original)
    {Owner=owner;Inventory=inventory;Original=original;}
}

/// <summary>GenerateIds-only owner. It never creates/reads an image or even uploads
/// a metadata header. Known allocation is durably retained before cancellation is
/// admitted. The returned row is progress, never refreshed inventory or authority.</summary>
public sealed partial class CurrentCatalogHeaderAllocationService(ProcessorStateCoordinator state,ProcessorAccountService accounts,
    StorageBudgetManager budget,LocalCurrentControlReader reader) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state=state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts=accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly LocalCurrentControlReader _reader=reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private Pending? _pending;
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure;}}

    public async Task<WorkspacePublicationRecord> AllocateAsync(LocalCurrentCatalogHeaderCloudAdmission admission,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(admission);
        CancellationTokenSource linked;
        lock(_gate) {ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                // Retain ANY previous known returned allocation first, even if this
                // caller selected another generation. No new provider IO precedes it.
                var retained=await PersistPendingAsync().ConfigureAwait(false);Check(caller,linked.Token);
                _reader.AssertCatalogHeaderCloudAdmissionOwner(admission);
                if(retained is not null && retained.Id==admission.Original.Header.Id)
                {
                    RequireSame(admission.Original.Header,retained);return Admit(retained,caller,linked.Token);
                }
                var header=admission.Original.Header;
                if(header.FileId is not null)
                    return Admit(await _reader.AssertCatalogHeaderCloudAdmissionAsync(admission,linked.Token).ConfigureAwait(false),caller,linked.Token);
                var row=await _accounts.ReadDriveAsync(header.Scope.AccountId,async(connection,revision,token)=>
                {
                    Check(caller,token);
                    if(revision!=admission.Inventory.Checkpoint.Context.AccountRevision) throw new StaleDriveConnectionException();
                    if(connection is not IGoogleWorkspaceObservedAllocation writer)
                        throw new InvalidOperationException("The owned Drive connection cannot join an observed allocation checkpoint.");
                    // The observed boundary performs the full current-state check
                    // immediately before EVERY physical request, including retries.
                    // Earlier duplicate scans add latency without admitting any IO.
                    return await AllocateObservedAsync(writer,admission,header,caller,token).ConfigureAwait(false);
                },linked.Token).ConfigureAwait(false);
                // Assignment spent the inventory. Deliberately no attempt to bless
                // that old inventory again or perform a second provider operation.
                return Admit(row,caller,linked.Token);
            }
            catch(Exception error) {lock(_gate) _failure=error;throw;}
            finally {_serial.Release();}
        }
    }
    private async Task<WorkspacePublicationRecord?> PersistPendingAsync()
    {
        if(_pending is not {} pending) return null;
        var saved=await _state.ExecuteAsync(database=>
        {
            var journal=new WorkspacePublicationRepository(database,_budget);
            var actual=journal.Read(pending.Header.Id) ?? throw new WorkspacePublicationConflictException();
            RequireSame(pending.Header,actual);
            // The narrow Core transaction checks aliases and original first bytes,
            // retaining its storage claim through commit. Known-ID recovery never
            // substitutes renewed source authority for original immutable history.
            return new CatalogGenerationCaptureRepository(database,_budget)
                .RetainReturnedOriginalHeaderAllocation(pending.Header,pending.FileId);
        },CancellationToken.None).ConfigureAwait(false);
        _pending=null;return saved;
    }
    private static void RequireSame(WorkspacePublicationRecord expected,WorkspacePublicationRecord actual)
    {
        if(expected.Id!=actual.Id || expected.Scope!=actual.Scope || actual.Role!="catalog-commit" || actual.MimeType!="application/json" ||
            expected.Role!=actual.Role || expected.MimeType!=actual.MimeType || expected.Sha256!=actual.Sha256 ||
            !expected.Payload.Span.SequenceEqual(actual.Payload.Span)) throw new WorkspacePublicationConflictException();
    }
    private WorkspacePublicationRecord Admit(WorkspacePublicationRecord row,CancellationToken caller,CancellationToken token)
    {lock(_gate) {Check(caller,token);_failure=null;return row;}}
    private void Check(CancellationToken caller,CancellationToken token)
    {lock(_gate) if(_stopping || caller.IsCancellationRequested || token.IsCancellationRequested) throw new OperationCanceledException("Header allocation retired before result admission.",token);}
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            _stopping=true;
            if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;
            return new(_disposal ??=StopAsync());
        }
    }
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;
        try {await _stop.CancelAsync().ConfigureAwait(false);} catch(Exception error) {cancellationFailure=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await PersistPendingAsync().ConfigureAwait(false);
            if(cancellationFailure is not null) throw new AggregateException("Header allocation cancellation needs attention; retain borrowed owners.",cancellationFailure);
            _stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        catch(Exception error) {lock(_gate) _failure=error;throw;}
        finally {_serial.Release();}
    }
    private sealed record Pending(WorkspacePublicationRecord Header,string FileId);
}
