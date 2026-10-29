using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Historical verified first publication, not ongoing freshness, canonical
/// activation, a finished setup state or permission to update original files.</summary>
public sealed record PublishedInitialCurrentControl(LibraryCurrentControlSlot Slot,InitialCurrentControlPublicationReceipt Receipt);

/// <summary>Joined create-only owner for the fixed initial no-catalog control head.
/// Allocation and sealed bytes are durable before I/O. Uncertain completion retries
/// the same ID/bytes; a conflicting later head is never overwritten or replaced.
/// This owner must not rerun the old two-object bootstrap inventory after handoff.
/// Runtime composition, later conditional updates and independent consumer admission
/// remain separate. Retire before borrowed keys/accounts/state.</summary>
public sealed class InitialCurrentControlPublicationService : IAsyncDisposable
{
    private readonly ProcessorStateCoordinator _state;
    private readonly ProcessorAccountService _accounts;
    private readonly LibrarySigningIdentityOwner _keys;
    private readonly StorageBudgetManager _budget;
    private readonly CurrentControlAllocationService _allocation;
    private readonly JournaledProtocolSigner _signer;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private bool _stopping,_exitConfirmed;
    private Task? _disposal;
    private Exception? _failure;
    public bool ExitConfirmed { get { lock(_gate) return _exitConfirmed; } }
    public Exception? Failure { get { lock(_gate) return _failure; } }

    public InitialCurrentControlPublicationService(ProcessorStateCoordinator state,ProcessorAccountService accounts,
        LibrarySigningIdentityOwner keys,StorageBudgetManager budget)
    {
        _state=state ?? throw new ArgumentNullException(nameof(state));_accounts=accounts ?? throw new ArgumentNullException(nameof(accounts));
        _keys=keys ?? throw new ArgumentNullException(nameof(keys));_budget=budget ?? throw new ArgumentNullException(nameof(budget));
        _allocation=new(state,accounts,keys,budget);_signer=new(state,keys,budget);
    }

    public async Task<PublishedInitialCurrentControl> PublishAsync(CancellationToken caller)
    {
        CancellationTokenSource linked;
        lock(_gate) { ObjectDisposedException.ThrowIf(_stopping,this);linked=CancellationTokenSource.CreateLinkedTokenSource(caller,_stop.Token); }
        using(linked)
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ThrowIfCanceled(caller,linked.Token);
                var slot=await _allocation.PrepareAsync(linked.Token).ConfigureAwait(false);
                var intent=await _signer.SignInitialCurrentControlAsync(linked.Token).ConfigureAwait(false);
                var envelope=intent.Envelope?.ToArray() ?? throw new InvalidDataException("Initial current signature is missing.");
                var connection=slot.ConnectionDescriptor();var control=LibraryCurrentControl.Read(envelope,connection);
                if (control.Catalog is not null) throw new InvalidDataException("Initial current cannot select a catalog.");
                var before=await CaptureAsync(slot,intent.EnvelopeSha256!,linked.Token).ConfigureAwait(false);
                var receipt=await _accounts.ReadDriveAsync(connection.Workspace.AccountId,async(reader,revision,token)=>
                {
                    ThrowIfCanceled(caller,token);
                    if (revision!=before.Authority.AccountRevision) throw new StaleLibraryConfigurationException();
                    if (reader is not IGoogleWorkspaceSetup setup || reader is not IGoogleWorkspacePublication writer)
                        throw new InvalidOperationException("Initial current needs actual workspace inspection and create-only publication.");
                    var access=await InspectAsync(reader,setup,connection,before.AllowShared,token).ConfigureAwait(false);
                    if (access.SharedDriveId!=before.Completion.Observation.Context.SharedDriveId) throw new DriveWorkspaceObjectChangedException();
                    await ReadPoliciesAsync(reader,connection,control,before.Completion,token).ConfigureAwait(false);
                    await AssertCurrentAsync(slot,intent.EnvelopeSha256!,before,caller,token).ConfigureAwait(false);
                    var created=await writer.CreateWorkspaceObjectAsync(connection.Workspace.FolderId,slot.FileId!,"application/json",envelope,
                        control.EnvelopeSha256,token).ConfigureAwait(false);
                    // A late success never authorizes admission. Fixed identity/bytes
                    // remain recoverable from the slot and signing journal on restart.
                    ThrowIfCanceled(caller,token);AssertActual(created,connection,slot.FileId!,envelope.LongLength,control.EnvelopeSha256);
                    await ReadPoliciesAsync(reader,connection,control,before.Completion,token).ConfigureAwait(false);
                    var after=await InspectAsync(reader,setup,connection,before.AllowShared,token).ConfigureAwait(false);
                    if (after!=access) throw new WorkspaceInventoryIncompleteException();
                    // Last network read in this attempt: no policy, access, folder or
                    // metadata I/O can follow the exact current-file fence.
                    var fenced=await reader.ReadWorkspaceObjectAsync(connection.Workspace.FolderId,slot.FileId!,envelope.LongLength,
                        control.EnvelopeSha256,token).ConfigureAwait(false);
                    ThrowIfCanceled(caller,token);AssertActual(fenced,connection,slot.FileId!,envelope.LongLength,control.EnvelopeSha256);
                    if (fenced.DriveVersion<created.DriveVersion || before.Receipt is { } old && fenced.DriveVersion<old.DriveVersion)
                        throw new DriveWorkspaceObjectChangedException();
                    await AssertCurrentAsync(slot,intent.EnvelopeSha256!,before,caller,token).ConfigureAwait(false);
                    return await _state.ExecuteAsync(database=>
                    {
                        ThrowIfCanceled(caller,token);
                        return new InitialCurrentControlPublicationRepository(database,_budget).RecordObserved(slot,control.EnvelopeSha256,fenced.DriveVersion,revision);
                    },token).ConfigureAwait(false);
                },linked.Token).ConfigureAwait(false);
                lock(_gate) { ThrowIfCanceled(caller,linked.Token);_failure=null;return new(slot,receipt); }
            }
            catch(Exception error) { lock(_gate) _failure=error;throw; }
            finally { _serial.Release(); }
        }
    }

    private Task<Captured> CaptureAsync(LibraryCurrentControlSlot slot,string sha,CancellationToken token)=>_state.ExecuteAsync(database=>
    {
        var authority=new LibraryAuthorityRepository(database,_budget);var local=authority.CaptureLocalPublicationAuthority();
        _keys.AssertPublicationIdentity(authority.Controller() ?? throw new InvalidDataException("Initial current lost its independent Root pin."),local);
        if (new LibraryCurrentControlSlotRepository(database,_budget).Current()!=slot) throw new InvalidDataException("Initial current changed its retained slot.");
        var intent=new ProtocolSigningJournal(database,_budget).Read(slot.Id);
        if (intent?.EnvelopeSha256!=sha || intent.Envelope is null) throw new ProtocolSigningConflictException();
        return new Captured(local,authority.AssertBootstrapSourcesCurrent().AllowSharedMetadata,
            authority.InitialPublicationCompletion() ?? throw new InvalidDataException("Initial current lost its policy completion."),
            new InitialCurrentControlPublicationRepository(database,_budget).Read());
    },token);

    private async Task AssertCurrentAsync(LibraryCurrentControlSlot slot,string sha,Captured expected,CancellationToken caller,CancellationToken token)
    {
        ThrowIfCanceled(caller,token);
        // Reconstructing the fixed intent rechecks both initial policy selections;
        // merely comparing a local digest cannot revive a superseded initial path.
        var current=await _signer.SignInitialCurrentControlAsync(token).ConfigureAwait(false);
        if (current.Id!=slot.Id || current.EnvelopeSha256!=sha || await CaptureAsync(slot,sha,token).ConfigureAwait(false)!=expected)
            throw new StaleLibraryConfigurationException();
        ThrowIfCanceled(caller,token);
    }

    private static async Task<Access> InspectAsync(IGoogleLibraryConnection reader,IGoogleWorkspaceSetup setup,LibraryConnectionDescriptor connection,
        bool allowShared,CancellationToken token)
    {
        var workspace=connection.Workspace;var access=await setup.InspectWorkspaceAsync(workspace.FolderId,token).ConfigureAwait(false);
        var folder=await reader.ReadFolderAsync(workspace.FolderId,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        if (access.AccountId!=workspace.AccountId || access.FolderId!=workspace.FolderId || access.DriveVersion<1 || folder.Id!=workspace.FolderId ||
            !folder.CanListChildren || folder.SharedDriveId is not null && !access.Shared) throw new DriveWorkspaceObjectChangedException();
        if (access.Shared && !allowShared) throw new LibraryWorkspaceApprovalException("Review this workspace's sharing before publishing private metadata.");
        return new(access.DriveVersion,access.Shared,folder.SharedDriveId);
    }

    private static async Task ReadPoliciesAsync(IGoogleLibraryConnection reader,LibraryConnectionDescriptor connection,LibraryCurrentControl control,
        LibraryInitialPublicationCompletion completion,CancellationToken token)
    {
        var publisher=control.Publishers.Reference;
        var rawPublisher=await reader.ReadWorkspaceObjectAsync(publisher.WorkspaceId,publisher.FileId,publisher.Bytes,publisher.Sha256,token).ConfigureAwait(false);
        var publisherBytes=AssertActual(rawPublisher,connection,publisher.FileId,publisher.Bytes,publisher.Sha256);
        if (rawPublisher.DriveVersion<completion.Publisher.Version) throw new DriveWorkspaceObjectChangedException();
        _=control.ReadPublishers(publisherBytes,connection);token.ThrowIfCancellationRequested();
        var sources=control.Sources.Reference;
        var rawSources=await reader.ReadWorkspaceObjectAsync(sources.WorkspaceId,sources.FileId,sources.Bytes,sources.Sha256,token).ConfigureAwait(false);
        var sourceBytes=AssertActual(rawSources,connection,sources.FileId,sources.Bytes,sources.Sha256);
        if (rawSources.DriveVersion<completion.Sources.Version) throw new DriveWorkspaceObjectChangedException();
        _=control.ReadSources(sourceBytes,connection);token.ThrowIfCancellationRequested();
    }

    private static byte[] AssertActual(DriveWorkspaceObject actual,LibraryConnectionDescriptor connection,string file,long bytes,string sha)
    {
        if (actual.AccountId!=connection.Workspace.AccountId || actual.FolderId!=connection.Workspace.FolderId || actual.FileId!=file ||
            actual.DriveVersion<1 || actual.Bytes.Length!=bytes) throw new DriveWorkspaceObjectChangedException();
        var owned=actual.Bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(owned))!=sha) throw new DriveWorkspaceObjectChangedException();
        return owned;
    }
    private void ThrowIfCanceled(CancellationToken caller,CancellationToken token)
    { lock(_gate) if (_stopping || caller.IsCancellationRequested || token.IsCancellationRequested) throw new OperationCanceledException("Initial-current publication retired before admission.",token); }
    public ValueTask DisposeAsync()
    {
        lock(_gate) { _stopping=true;if (_disposal is {IsFaulted:true} && !_exitConfirmed) _disposal=null;return new(_disposal ??=StopAsync()); }
    }
    private async Task StopAsync()
    {
        Exception? cancellationFailure=null;
        try { await _stop.CancelAsync().ConfigureAwait(false); } catch(Exception error) { cancellationFailure=error; }
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await _allocation.DisposeAsync().ConfigureAwait(false);
            if (cancellationFailure is not null) throw new AggregateException("Initial-current cancellation needs attention; retain borrowed owners.",cancellationFailure);
            _stop.Dispose();lock(_gate) _exitConfirmed=true;
        }
        catch(Exception error) { lock(_gate) _failure=error;throw; }
        finally { _serial.Release(); }
    }
    private sealed record Access(long Version,bool Shared,string? SharedDriveId);
    private sealed record Captured(LocalCatalogPublicationAuthority Authority,bool AllowShared,LibraryInitialPublicationCompletion Completion,
        InitialCurrentControlPublicationReceipt? Receipt);
}
