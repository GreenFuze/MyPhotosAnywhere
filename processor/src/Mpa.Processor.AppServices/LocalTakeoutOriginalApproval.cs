using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Called only for an authenticated explicit PC import action. One
    /// native signed command binds the complete selected manifest and destination.
    /// No original is opened/uploaded here and no metadata authority is reused.</summary>
    public async Task<ProtocolSigningIntent> ApproveTakeoutImportAsync(TakeoutCompletedInventory completed,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(completed);
        // A previously admitted local human command may finish its original
        // signature after restart/configuration changes. This is bookkeeping,
        // never renewed permission to upload under its obsolete capture.
        var retained=await StateOnlyOperationAsync(token=>_state.ExecuteAsync<ProtocolSigningIntent?>(database=>
        {
            lock(_gate)
            {
                Check(caller,token);var approvals=new TakeoutOriginalApprovalRepository(database,_budget);var pending=approvals.ReadCaptured(completed);
                if(pending is null)return null;
                _keys.AssertControllerIdentity(new LibraryAuthorityRepository(database,_budget).Controller()??throw new ProtocolSigningConflictException());
                if(pending.Envelope is null)
                {
                    var signature=_keys.SignControllerDocument(pending.Body.Span);Check(caller,token);
                    _=new ProtocolSigningJournal(database,_budget).Seal(pending.Id,pending.BodySha256,signature);
                }
                Check(caller,token);return approvals.RequireSealed(completed);
            }
        },token),caller).ConfigureAwait(false);
        if(retained is not null)return retained;
        var capture=await CaptureAsync(caller).ConfigureAwait(false);
        var policies=await ReadSelectedPoliciesAsync(capture,caller).ConfigureAwait(false);
        var fence=await FenceAsync(capture,caller).ConfigureAwait(false);
        return await StateOnlyOperationAsync(token=>_state.ExecuteAsync(database=>
        {
            lock(_gate)
            {
                Check(caller,token);_=AssertTakeoutDestination(database,completed,policies,fence,true,token);
                var approvals=new TakeoutOriginalApprovalRepository(database,_budget);var intent=approvals.CaptureIntent(completed);
                Check(caller,token);
                if(intent.Envelope is null)
                {
                    var signature=_keys.SignControllerDocument(intent.Body.Span);Check(caller,token);
                    _=AssertTakeoutDestination(database,completed,policies,fence,true,token);
                    intent=new ProtocolSigningJournal(database,_budget).Seal(intent.Id,intent.BodySha256,signature);
                }
                Check(caller,token);var actual=approvals.RequireSealed(completed);
                if(actual.Id!=intent.Id||actual.BodySha256!=intent.BodySha256||actual.EnvelopeSha256!=intent.EnvelopeSha256)throw new ProtocolSigningConflictException();
                return actual;
            }
        },token),caller).ConfigureAwait(false);
    }

    /// <summary>Reopen an already signed explicit import command. Possession alone
    /// admits nothing; the upload owner must refresh before each bounded request.</summary>
    public Task<ITakeoutOriginalAuthority> OpenTakeoutOriginalAuthorityAsync(TakeoutCompletedInventory completed,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(completed);
        return StateOnlyOperationAsync(token=>_state.ExecuteAsync(database=>
        {
            lock(_gate)
            {
                Check(caller,token);var approval=new TakeoutOriginalApprovalRepository(database,_budget).RequireSealed(completed);
                Check(caller,token);return(ITakeoutOriginalAuthority)new TakeoutOriginalAuthority(this,completed,approval);
            }
        },token),caller);
    }
    private long AssertTakeoutDestination(ProcessorStateDatabase database,TakeoutCompletedInventory completed,
        LocalCurrentControlPolicies policies,LocalCurrentControlFence fence,bool approving,CancellationToken token)
    {
        Check(token,token);
        if(!ReferenceEquals(fence.Owner,_identity)||!ReferenceEquals(fence.Capture.Owner,_identity)||!ReferenceEquals(policies.Capture,fence.Capture))
            throw new CurrentControlObservationConflictException();
        AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
        new LocalCurrentControlObservations(database,_budget).AssertFenced(fence.Attempt,token);
        var target=completed.Target;var configuration=policies.Sources;var local=new LibrarySourceRepository(database).Snapshot();
        var source=configuration.Sources.SingleOrDefault(value=>value.AccountId==target.AccountId&&value.FolderId==target.FolderId&&value.Enabled);
        var selected=local.Sources.SingleOrDefault(value=>value.AccountId==target.AccountId&&value.FolderId==target.FolderId&&value.Enabled);
        if(target.LibraryId!=fence.Capture.Pin.LibraryId||configuration.LibraryId!=target.LibraryId||local.Revision!=configuration.Revision||
            target.ConfigurationRevision>configuration.Revision||approving&&target.ConfigurationRevision!=configuration.Revision||
            !configuration.Accounts.Contains(target.AccountId,StringComparer.Ordinal)||source is null||selected is null||
            source.Id!=selected.Id||source.Revision!=selected.Revision||source.IncludeDescendants!=selected.IncludeDescendants)
            throw new StaleLibraryConfigurationException();
        var account=new DriveAccountRepository(database).Accounts().SingleOrDefault(value=>value.PermissionId==target.AccountId&&value.Connected);
        if(account is null||account.Revision<target.AccountRevision)throw new StaleDriveConnectionException();
        var parent=new DurableJobRepository(database).Summary(completed.ImportId);
        if(parent.Paused||parent.IsCanceled||parent.Ready!=1)throw new StaleStageLeaseException();
        Check(token,token);return account.Revision;
    }
    private sealed class TakeoutOriginalAuthority(LocalCurrentControlReader owner,TakeoutCompletedInventory completed,ProtocolSigningIntent approval) : ITakeoutOriginalAuthority
    {
        private LocalCurrentControlPolicies? _policies;
        private LocalCurrentControlFence? _fence;
        private long _accountRevision;
        public async Task<long> RefreshAsync(TakeoutOriginalWork work,CancellationToken token)
        {
            await owner.StateOnlyOperationAsync(owned=>owner._state.ExecuteAsync(database=>
            {lock(owner._gate){owner.Check(token,owned);AssertOriginal(database,work);return true;}},owned),token).ConfigureAwait(false);
            var capture=await owner.CaptureAsync(token).ConfigureAwait(false);
            var policies=await owner.ReadSelectedPoliciesAsync(capture,token).ConfigureAwait(false);
            var fence=await owner.FenceAsync(capture,token).ConfigureAwait(false);
            return await owner.StateOnlyOperationAsync(owned=>owner._state.ExecuteAsync(database=>
            {
                lock(owner._gate)
                {
                    owner.Check(token,owned);AssertOriginal(database,work);
                    var revision=owner.AssertTakeoutDestination(database,completed,policies,fence,false,owned);
                    owner.Check(token,owned);_policies=policies;_fence=fence;_accountRevision=revision;return revision;
                }
            },owned),token).ConfigureAwait(false);
        }
        public void AssertCurrent(ProcessorStateDatabase database,TakeoutOriginalWork work)
        {
            lock(owner._gate)
            {
                ObjectDisposedException.ThrowIf(owner._stopping,owner);AssertOriginal(database,work);
                var actual=owner.AssertTakeoutDestination(database,completed,_policies??throw new CurrentControlObservationConflictException(),
                    _fence??throw new CurrentControlObservationConflictException(),false,CancellationToken.None);
                if(actual!=_accountRevision)throw new StaleDriveConnectionException();
            }
        }
        private void AssertOriginal(ProcessorStateDatabase database,TakeoutOriginalWork work)
        {
            ArgumentNullException.ThrowIfNull(work);
            if(work.Capture.Inventory.ImportId!=completed.ImportId||work.Capture.Inventory.Target!=completed.Target||work.ApprovalId!=approval.Id)
                throw new ProtocolSigningConflictException();
            new TakeoutOriginalJobRepository(database).AssertCurrent(work);
            var actual=new TakeoutOriginalApprovalRepository(database,owner._budget).RequireSealed(work.Capture.Inventory);
            if(actual.Id!=approval.Id||actual.BodySha256!=approval.BodySha256||actual.EnvelopeSha256!=approval.EnvelopeSha256)throw new ProtocolSigningConflictException();
        }
    }
}
