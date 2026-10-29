using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Actual Root head and condition, still PENDING. It cannot select policies,
/// activate a canonical generation or admit a write without the remaining owners.</summary>
public sealed class LocalCurrentControlCapture
{
    internal object Owner { get; }
    internal LibraryControllerPin Pin { get; }
    internal LibraryCurrentControlSlot Slot { get; }
    internal DriveWorkspaceConditionalJson Snapshot { get; }
    public LocalCurrentControlAttempt Attempt { get; }
    public LibraryCurrentControl Head { get; }
    internal LocalCurrentControlCapture(object owner,LibraryControllerPin pin,LibraryCurrentControlSlot slot,
        DriveWorkspaceConditionalJson snapshot,LocalCurrentControlAttempt attempt,LibraryCurrentControl head)
    {Owner=owner;Pin=pin;Slot=slot;Snapshot=snapshot;Attempt=attempt;Head=head;}
}

/// <summary>A fresh final-fence result bound to this live reader. The stored phase
/// alone is historical evidence after restart; full graph/atomic admission is separate.</summary>
public sealed class LocalCurrentControlFence
{
    internal object Owner { get; }
    public LocalCurrentControlCapture Capture { get; }
    public LocalCurrentControlAttempt Attempt { get; }
    internal LocalCurrentControlFence(object owner,LocalCurrentControlCapture capture,LocalCurrentControlAttempt attempt)
    {Owner=owner;Capture=capture;Attempt=attempt;}
}

/// <summary>Joined producer current reader borrowing account/state/native-key owners.
/// Metadata persists before body, complete bytes before Root interpretation, and only
/// retained local Root signatures may advance high-water. Root observation never needs
/// an active publisher. Graph reads must join this same lifetime before activation is
/// added; current capture/fence alone are not complete policy/catalog admission.</summary>
public sealed partial class LocalCurrentControlReader(ProcessorStateCoordinator state,ProcessorAccountService accounts,
    LibrarySigningIdentityOwner keys,StorageBudgetManager budget) : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state=state ?? throw new ArgumentNullException(nameof(state));
    private readonly ProcessorAccountService _accounts=accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly LibrarySigningIdentityOwner _keys=keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly StorageBudgetManager _budget=budget ?? throw new ArgumentNullException(nameof(budget));
    private readonly object _gate=new(),_identity=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed {get {lock(_gate) return _exitConfirmed;}}
    public Exception? Failure {get {lock(_gate) return _failure;}}

    public Task<LocalCurrentControlCapture> CaptureAsync(CancellationToken caller)=>OperationAsync(token=>CaptureCoreAsync(caller,token),caller);

    private async Task<LocalCurrentControlCapture> CaptureCoreAsync(CancellationToken caller,CancellationToken token)
    {
        var scope=await _state.ExecuteAsync(database=>
        {
            Check(caller,token);var pin=new LibraryAuthorityRepository(database,_budget).Controller() ?? throw new InvalidOperationException("Independently approve the Root first.");
            _keys.AssertControllerIdentity(pin);
            var slot=new LibraryCurrentControlSlotRepository(database,_budget).Current() ?? throw new InvalidOperationException("Publish the initial current file first.");
            var account=new DriveAccountRepository(database).Accounts().SingleOrDefault(row=>row.PermissionId==pin.AccountId && row.Connected)
                ?? throw new StaleDriveConnectionException();
            return new Scope(pin,slot,new LocalCurrentControlObservations(database,_budget).Begin(account.Revision,token));
        },token).ConfigureAwait(false);
        return await _accounts.ReadDriveAsync(scope.Pin.AccountId,async(reader,revision,owned)=>
        {
            Check(caller,owned);if(revision!=scope.Attempt.AccountRevision) throw new StaleDriveConnectionException();
            await AssertScopeAsync(scope,caller,owned).ConfigureAwait(false);
            var folder=await reader.ReadFolderAsync(scope.Pin.FolderId,owned).ConfigureAwait(false);Check(caller,owned);
            if(folder.Id!=scope.Pin.FolderId || !folder.CanListChildren || folder.SharedDriveId!=scope.Attempt.SharedDriveId) throw new CurrentControlObservationConflictException();
            await AssertScopeAsync(scope,caller,owned).ConfigureAwait(false);
            if(reader is not IGoogleWorkspaceConditionalObservation source) throw new InvalidOperationException("The owned Google connection cannot retain actual current observations.");
            var observer=new CaptureObserver(this,scope,caller);
            var snapshot=await source.ObserveConditionalWorkspaceJsonAsync(scope.Pin.FolderId,scope.Slot.FileId!,observer,owned).ConfigureAwait(false);
            Check(caller,owned);ValidateActual(scope,snapshot.Metadata);
            var observed=await _state.ExecuteAsync(database=>
            {
                Check(caller,owned);AssertScope(database,scope);
                return new LocalCurrentControlObservations(database,_budget).Observe(observer.Attempt,snapshot.Bytes.Span,owned);
            },owned).ConfigureAwait(false);
            Check(caller,owned);return new LocalCurrentControlCapture(_identity,scope.Pin,scope.Slot,snapshot,observed.Attempt,observed.Head);
        },token).ConfigureAwait(false);
    }

    public Task<LocalCurrentControlFence> FenceAsync(LocalCurrentControlCapture capture,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return OperationAsync(token=>FenceCapturedCoreAsync(capture,caller,token),caller);
    }

    private async Task<LocalCurrentControlFence> FenceCapturedCoreAsync(LocalCurrentControlCapture capture,CancellationToken caller,CancellationToken token)
    {
        if(!ReferenceEquals(capture.Owner,_identity) || capture.Head.ConnectionSha256!=capture.Attempt.ConnectionSha256 ||
            capture.Head.Sequence!=capture.Attempt.RootSequence || capture.Head.EnvelopeSha256!=capture.Attempt.ContentSha256 ||
            capture.Snapshot.Sha256!=capture.Head.EnvelopeSha256) throw new CurrentControlObservationConflictException();
        var scope=new Scope(capture.Pin,capture.Slot,capture.Attempt);
        await _state.ExecuteAsync(database=>{Check(caller,token);AssertScope(database,scope);new LocalCurrentControlObservations(database,_budget).AssertCaptured(capture.Attempt,token);return true;},token).ConfigureAwait(false);
        return await _accounts.ReadDriveAsync(scope.Pin.AccountId,async(reader,revision,owned)=>
        {
            Check(caller,owned);if(revision!=scope.Attempt.AccountRevision) throw new StaleDriveConnectionException();
            if(reader is not IGoogleWorkspaceConditionalObservation source) throw new InvalidOperationException("The owned Google connection cannot fence current observations.");
            var observer=new FenceObserver(this,scope,caller);
            // Last transport operation: its metadata callback blocks changes before
            // any final body. Its byte callback commits the exact fence after all I/O.
            _=await source.ObserveConditionalWorkspaceJsonAsync(scope.Pin.FolderId,scope.Slot.FileId!,observer,owned).ConfigureAwait(false);
            var fenced=observer.Fenced ?? throw new InvalidOperationException("The actual final read lost its durable fence.");
            await _state.ExecuteAsync(database=>{Check(caller,owned);AssertScope(database,scope);new LocalCurrentControlObservations(database,_budget).AssertFenced(fenced,owned);return true;},owned).ConfigureAwait(false);
            Check(caller,owned);return new LocalCurrentControlFence(_identity,capture,fenced);
        },token).ConfigureAwait(false);
    }

    private Task<bool> AssertScopeAsync(Scope scope,CancellationToken caller,CancellationToken token)=>_state.ExecuteAsync(database=>
    {
        Check(caller,token);AssertScope(database,scope);
        if(new LocalCurrentControlObservations(database,_budget).Current(token)!=scope.Attempt) throw new CurrentControlObservationConflictException();
        return true;
    },token);
    private void AssertScope(ProcessorStateDatabase database,Scope scope)
    {
        _keys.AssertControllerIdentity(scope.Pin);
        if(new LibraryAuthorityRepository(database,_budget).Controller()!=scope.Pin || new LibraryCurrentControlSlotRepository(database,_budget).Current()!=scope.Slot ||
            !new DriveAccountRepository(database).Accounts().Any(row=>row.PermissionId==scope.Pin.AccountId && row.Connected && row.Revision==scope.Attempt.AccountRevision))
            throw new StaleDriveConnectionException();
    }
    private static void ValidateActual(Scope scope,DriveWorkspaceConditionalMetadata metadata)
    {
        if(metadata.AccountId!=scope.Pin.AccountId || metadata.FolderId!=scope.Pin.FolderId || metadata.FileId!=scope.Slot.FileId)
            throw new CurrentControlObservationConflictException();
    }
    private static LocalCurrentControlMetadata Metadata(DriveWorkspaceConditionalMetadata actual)=>
        new(actual.Version,actual.ByteLength,actual.EntityTag,actual.ParentsSha256,actual.SharedDriveId);

    private sealed class CaptureObserver(LocalCurrentControlReader owner,Scope scope,CancellationToken caller) : IGoogleWorkspaceConditionalObserver
    {
        internal LocalCurrentControlAttempt Attempt {get;private set;}=scope.Attempt;
        public async Task RememberMetadataAsync(DriveWorkspaceConditionalMetadata actual,CancellationToken token)
        {
            ValidateActual(scope,actual);
            Attempt=await owner._state.ExecuteAsync(database=>{owner.Check(caller,token);owner.AssertScope(database,scope);
                return new LocalCurrentControlObservations(database,owner._budget).RememberMetadata(Attempt,Metadata(actual),token);},token).ConfigureAwait(false);
        }
        public async Task RememberBytesAsync(DriveWorkspaceConditionalJson actual,CancellationToken token)
        {
            ValidateActual(scope,actual.Metadata);
            if(actual.Version!=Attempt.Version || actual.ByteLength!=Attempt.DeclaredBytes || actual.EntityTag!=Attempt.EntityTag ||
                actual.Metadata.ParentsSha256!=Attempt.ParentsSha256 || actual.SharedDriveId!=Attempt.SharedDriveId)
                throw new CurrentControlObservationConflictException();
            Attempt=await owner._state.ExecuteAsync(database=>{owner.Check(caller,token);owner.AssertScope(database,scope);
                return new LocalCurrentControlObservations(database,owner._budget).RememberBytes(Attempt,actual.Bytes.Span,token);},token).ConfigureAwait(false);
        }
    }
    private sealed class FenceObserver(LocalCurrentControlReader owner,Scope scope,CancellationToken caller) : IGoogleWorkspaceConditionalObserver
    {
        internal LocalCurrentControlAttempt? Fenced {get;private set;}
        public async Task RememberMetadataAsync(DriveWorkspaceConditionalMetadata actual,CancellationToken token)
        {
            ValidateActual(scope,actual);
            var same=await owner._state.ExecuteAsync(database=>{owner.Check(caller,token);owner.AssertScope(database,scope);
                return new LocalCurrentControlObservations(database,owner._budget).CheckFenceMetadata(scope.Attempt,Metadata(actual),token);},token).ConfigureAwait(false);
            if(!same) throw new CurrentControlObservationConflictException();
        }
        public async Task RememberBytesAsync(DriveWorkspaceConditionalJson actual,CancellationToken token)
        {
            ValidateActual(scope,actual.Metadata);
            Fenced=await owner._state.ExecuteAsync(database=>{owner.Check(caller,token);owner.AssertScope(database,scope);
                return new LocalCurrentControlObservations(database,owner._budget).Fence(scope.Attempt,Metadata(actual.Metadata),actual.Sha256,token);},token).ConfigureAwait(false);
        }
    }

    private async Task<T> OperationAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken caller)
    {
        CancellationTokenSource linked;lock(_gate) {ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token);}
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try {Check(caller,linked.Token);var result=await action(linked.Token).ConfigureAwait(false);lock(_gate) {Check(caller,linked.Token);_failure=null;return result;}}
            catch(Exception error) {_completedDocumentReads=null;lock(_gate) _failure=error;throw;}
            finally {_serial.Release();}
        }
    }
    private void Check(CancellationToken caller,CancellationToken token)
    {lock(_gate) if(_stopping || caller.IsCancellationRequested || token.IsCancellationRequested) throw new OperationCanceledException("The current reader retired before admission.",token);}
    public ValueTask DisposeAsync()
    {lock(_gate) {_stopping=true;if(_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal ??=StopAsync());}}
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;try {await _stop.CancelAsync().ConfigureAwait(false);}catch(Exception error){cancellationFailure=error;}
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stateAdmissionSerial.WaitAsync().ConfigureAwait(false);
            try
            {
                if(cancellationFailure is not null) throw new AggregateException("Current-reader cancellation needs attention; retain borrowed owners.",cancellationFailure);
                _completedDocumentReads=null;_stop.Dispose();lock(_gate) _exitConfirmed=true;
            }
            finally{_stateAdmissionSerial.Release();}
        }
        catch(Exception error){lock(_gate) _failure=error;throw;}
        finally{_serial.Release();}
        _stateAdmissionSerial.Dispose();_serial.Dispose();
    }
    private sealed record Scope(LibraryControllerPin Pin,LibraryCurrentControlSlot Slot,LocalCurrentControlAttempt Attempt);
}
