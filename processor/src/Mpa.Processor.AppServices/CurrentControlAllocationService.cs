using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Joined allocation owner borrowing native keys, accounts and serial state.
/// A returned slot is historical identity evidence, never cloud availability, current
/// publication authority or setup readiness. Retire it before its borrowed resources.
/// GenerateIds creates no file: no signature, export or create may precede durable ID.
/// An unknown unused provider ID after a hard crash is not a published media identity.
/// Known live receipts are retried unchanged before any further provider allocation.</summary>
public sealed class CurrentControlAllocationService(ProcessorStateCoordinator state,ProcessorAccountService accounts,
    LibrarySigningIdentityOwner keys,StorageBudgetManager budget) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state=state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts=accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly LibrarySigningIdentityOwner _keys=keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private Pending? _pending;
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed { get { lock(_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock(_gate) return _failure; } }

    public async Task<LibraryCurrentControlSlot> PrepareAsync(CancellationToken caller)
    {
        CancellationTokenSource linked;
        lock(_gate) { ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token); }
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await PersistPendingAsync().ConfigureAwait(false);
                ThrowIfCanceled(caller,linked.Token);
                var captured=await CaptureAsync(linked.Token).ConfigureAwait(false);
                if (captured.Slot.FileId is not null) return Admit(captured.Slot,caller,linked.Token);
                var root=LibraryConnectionDescriptor.Decode(captured.Slot.RootConnectionCode);
                var result=await _accounts.ReadDriveAsync(root.Workspace.AccountId,async(connection,revision,token)=>
                {
                    ThrowIfCanceled(caller,token);
                    var current=await CaptureAsync(token).ConfigureAwait(false);
                    if (current.Slot.Id!=captured.Slot.Id || current.Slot.RootConnectionCode!=captured.Slot.RootConnectionCode ||
                        revision!=current.Authority.AccountRevision || current.Authority.AccountId!=root.Workspace.AccountId)
                        throw new StaleLibraryConfigurationException();
                    if (current.Slot.FileId is not null) return current.Slot;
                    if (connection is not IGoogleWorkspacePublication writer) throw new InvalidOperationException("The owned Drive connection cannot allocate metadata identities.");
                    var allocated=await writer.AllocateWorkspaceObjectIdAsync(token).ConfigureAwait(false);
                    // A late returned ID is known evidence even if the caller/selector
                    // retired during I/O. Checkpoint it without granting fresh access.
                    _pending=new(current.Slot,root.Workspace.AccountId,revision,allocated);
                    var saved=await PersistPendingAsync().ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The returned allocation was not retained.");
                    ThrowIfCanceled(caller,token);
                    var after=await CaptureAsync(token).ConfigureAwait(false);
                    if (after.Slot!=saved || after.Authority!=current.Authority) throw new StaleLibraryConfigurationException();
                    return saved;
                },linked.Token).ConfigureAwait(false);
                return Admit(result,caller,linked.Token);
            }
            catch(Exception error) { lock(_gate) _failure=error;throw; }
            finally { _serial.Release(); }
        }
    }

    private Task<Captured> CaptureAsync(CancellationToken token)=>_state.ExecuteAsync(database=>
    {
        var authority=new LibraryAuthorityRepository(database,_budget);
        var local=authority.CaptureLocalPublicationAuthority();
        _keys.AssertPublicationIdentity(authority.Controller() ?? throw new InvalidDataException("Current allocation lost its independently pinned controller."),local);
        return new Captured(new LibraryCurrentControlSlotRepository(database,_budget).Prepare(),local);
    },token);

    private async Task<LibraryCurrentControlSlot?> PersistPendingAsync()
    {
        if (_pending is not { } pending) return null;
        var saved=await _state.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,_budget)
            .CaptureAllocatedFile(pending.Slot,pending.AccountId,pending.AccountRevision,pending.FileId),CancellationToken.None).ConfigureAwait(false);
        _pending=null;return saved;
    }
    private LibraryCurrentControlSlot Admit(LibraryCurrentControlSlot saved,CancellationToken caller,CancellationToken token)
    { lock(_gate) { ThrowIfCanceled(caller,token);_failure=null;return saved; } }
    private void ThrowIfCanceled(CancellationToken caller,CancellationToken token)
    { lock(_gate) if (_stopping || caller.IsCancellationRequested || token.IsCancellationRequested) throw new OperationCanceledException("Current allocation retired before result admission.",token); }

    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            _stopping=true;
            if (_disposal is { IsFaulted:true } && !_exitConfirmed) _disposal=null;
            return new(_disposal ??=StopAsync());
        }
    }
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch(Exception error) { cancellationFailure=error; }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await PersistPendingAsync().ConfigureAwait(false);
            if (cancellationFailure is not null) throw new AggregateException("Allocation cancellation needs attention; retain borrowed owners.",cancellationFailure);
            _stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        catch(Exception error) { lock(_gate) _failure=error;throw; }
        finally { _serial.Release(); }
    }
    private sealed record Captured(LibraryCurrentControlSlot Slot,LocalCatalogPublicationAuthority Authority);
    private sealed record Pending(LibraryCurrentControlSlot Slot,string AccountId,long AccountRevision,string FileId);
}
