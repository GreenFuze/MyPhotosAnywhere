using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Actual same-file bytes observed, still PENDING canonical admission.
/// This receipt grants no active catalog, PublishedHead or snapshot retirement.</summary>
public sealed record PendingCurrentControlPublication(Guid TransitionId,string ConnectionCode,string RootSha256,long DriveVersion,bool AlreadyPresent);

public sealed partial class LocalCurrentControlReader
{
    /// <summary>Reconcile the exact durable transition, verify its complete desired
    /// graph under an ACTUAL captured Root, fence that Root and conditionally replace
    /// the same file. A lost acknowledgement retries the same bytes/identity. Desired
    /// bytes are never substituted for a capture or a canonical staging header.
    /// A fresh selected-graph read/fence/atomic activation must follow this method.
    /// Every operation joins this reader's lifetime before its borrowers can retire.</summary>
    public Task<PendingCurrentControlPublication> PublishTransitionAsync(Guid transitionId,CancellationToken caller)=>OperationAsync(async token=>
    {
        var signed=await new JournaledProtocolSigner(_state,_keys,_budget).SignCurrentControlTransitionAsync(transitionId,token).ConfigureAwait(false);
        var capture=await CaptureCoreAsync(caller,token).ConfigureAwait(false);
        var plan=await _state.ExecuteAsync(database=>
        {
            Check(caller,token);_keys.AssertControllerIdentity(capture.Pin);
            return new CurrentControlTransitionJournal(database,_budget).AssertCapturedCurrent(transitionId,capture.Attempt,token);
        },token).ConfigureAwait(false);
        var pin=capture.Slot.ConnectionDescriptor();
        var envelope=signed.Envelope?.ToArray() ?? throw new SigningHistoryRecoveryRequiredException();
        var target=LibraryCurrentControl.Read(envelope,pin);
        if(target.Sequence!=plan.Sequence || target.Publishers!=plan.Target.Publishers || target.Sources!=plan.Target.Sources || target.Catalog!=plan.Catalog)
            throw new ProtocolSigningConflictException();
        var observations=await ReadTransitionTargetsAsync(capture,plan,target,caller,token).ConfigureAwait(false);
        var fence=await FenceCapturedCoreAsync(capture,caller,token).ConfigureAwait(false);
        return await _accounts.ReadDriveAsync(capture.Pin.AccountId,async(reader,revision,owned)=>
        {
            Check(caller,owned);if(revision!=capture.Attempt.AccountRevision) throw new StaleDriveConnectionException();
            if(reader is not IGoogleWorkspaceConditionalPublication writer) throw new InvalidOperationException("Current publication requires the actual owned conditional Google connection and joined local admission.");
            async Task AssertReadyAsync()=>await _state.ExecuteAsync(database=>
            {
                Check(caller,owned);_keys.AssertControllerIdentity(capture.Pin);AssertScope(database,new(capture.Pin,capture.Slot,fence.Attempt));
                if(new CurrentControlTransitionJournal(database,_budget).AssertPublicationCurrent(transitionId,fence.Attempt,observations,owned)!=plan)
                    throw new CurrentControlObservationConflictException();
                return true;
            },owned).ConfigureAwait(false);
            await AssertReadyAsync().ConfigureAwait(false);
            var already=capture.Head.EnvelopeSha256==target.EnvelopeSha256;
            var actual=already ? capture.Snapshot : await writer.ReplaceAdmittedConditionalWorkspaceJsonAsync(capture.Snapshot,envelope,target.EnvelopeSha256,
                async admissionToken=>{Check(caller,admissionToken);await AssertReadyAsync().ConfigureAwait(false);},owned).ConfigureAwait(false);
            Check(caller,owned);ValidateActual(new(capture.Pin,capture.Slot,capture.Attempt),actual.Metadata);
            if(actual.Sha256!=target.EnvelopeSha256 || actual.SharedDriveId!=capture.Attempt.SharedDriveId || actual.Metadata.ParentsSha256!=capture.Attempt.ParentsSha256 ||
                actual.Version<=plan.PreviousVersion || !already && actual.Version<=capture.Snapshot.Version)
                throw new CurrentControlObservationConflictException();
            await AssertReadyAsync().ConfigureAwait(false);
            return new PendingCurrentControlPublication(plan.Id,plan.ConnectionCode,actual.Sha256,actual.Version,already);
        },token).ConfigureAwait(false);
    },caller);

    private async Task<IReadOnlyList<LocalCurrentGraphObservation>> ReadTransitionTargetsAsync(LocalCurrentControlCapture capture,
        CurrentControlTransitionPlan plan,LibraryCurrentControl target,CancellationToken caller,CancellationToken token)
    {
        var scope=await RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
        var allowShared=await _state.ExecuteAsync(database=>
            new LibraryAuthorityRepository(database,_budget).BootstrapSources()?.AllowSharedMetadata ??
                throw new LibraryWorkspaceApprovalException("Review metadata workspace sharing before publication."),token).ConfigureAwait(false);
        return await _accounts.ReadDriveAsync(scope.Pin.AccountId,async(reader,revision,owned)=>
        {
            Check(caller,owned);if(revision!=scope.Attempt.AccountRevision) throw new StaleDriveConnectionException();
            if(reader is not IGoogleWorkspaceConditionalObservation policies || reader is not IGoogleWorkspaceObservedObjectReader objects || reader is not IGoogleWorkspaceSetup setup)
                throw new InvalidOperationException("Current target verification requires actual metadata, byte and workspace observations.");
            var before=await InspectTransitionWorkspaceAsync(reader,setup,capture,allowShared,owned).ConfigureAwait(false);
            var pin=capture.Slot.ConnectionDescriptor();
            var publisher=target.ReadPublishers(await ReadPolicyAsync(capture,policies,target.Publishers.Reference,plan.Id,caller,owned).ConfigureAwait(false),pin);
            var sources=target.ReadSources(await ReadPolicyAsync(capture,policies,target.Sources.Reference,plan.Id,caller,owned).ConfigureAwait(false),pin);
            var source=new CurrentGraphObjects(this,capture,objects,target,caller);
            if(target.Catalog is { } selected)
            {
                var actor=publisher.Publisher ?? throw new ProtocolFormatException("A revoked publisher cannot select a catalog.");
                VerifiedProtocolDocument? head=null;
                await foreach(var entry in new CatalogCommitHistoryTraversal(pin.Workspace,actor,source).ReadAsync(selected,owned).ConfigureAwait(false))
                {
                    await _state.ExecuteAsync(database=>
                    {
                        Check(caller,owned);_keys.AssertControllerIdentity(capture.Pin);
                        new CurrentControlTransitionJournal(database,_budget).AssertCapturedCurrent(plan.Id,capture.Attempt,owned);
                        var native=_keys.PublicIdentity;
                        if(entry.Verified.ActorId==native.PublisherId && entry.Verified.Epoch==native.PublisherEpoch)
                            _=new ProtocolSigningJournal(database,_budget).AssertRetainedCloudEnvelope(native.PublisherId,native.PublisherEpoch,entry.Envelope.Span);
                        return true;
                    },owned).ConfigureAwait(false);
                    head ??=entry.Verified;source.CommitIds.Add(entry.Reference.FileId);
                }
                if(head is null) throw new InvalidDataException("The complete desired catalog lost its first commit.");
                source.BeginFacts();
                await foreach(var leaf in new CatalogFactsTraversal(pin.LibraryId,pin.Workspace,source,WorkspaceObservationRepository.MaximumDocuments)
                    .ReadCoveredLeavesAsync(head,(coverage,cancellation)=>
                    {Check(caller,cancellation);sources.ValidateCoverage(coverage);return Task.CompletedTask;},owned).ConfigureAwait(false))
                {
                    // Complete bounded decode/coverage only. No synthetic Root capture,
                    // canonical stage, duplicate persistent table or original read.
                    Check(caller,owned);_=leaf.Facts.Assets.Count;
                }
            }
            var after=await InspectTransitionWorkspaceAsync(reader,setup,capture,allowShared,owned).ConfigureAwait(false);
            if(before!=after) throw new WorkspaceInventoryIncompleteException();
            _=await RequirePolicyCaptureAsync(capture,caller,owned).ConfigureAwait(false);
            return source.Observations.Values.ToArray();
        },token).ConfigureAwait(false);
    }

    private static async Task<(long Version,bool Shared,string? Drive)> InspectTransitionWorkspaceAsync(IGoogleLibraryConnection reader,
        IGoogleWorkspaceSetup setup,LocalCurrentControlCapture capture,bool allowShared,CancellationToken token)
    {
        var access=await setup.InspectWorkspaceAsync(capture.Pin.FolderId,token).ConfigureAwait(false);
        var folder=await reader.ReadFolderAsync(capture.Pin.FolderId,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        if(access.AccountId!=capture.Pin.AccountId || access.FolderId!=capture.Pin.FolderId || access.DriveVersion<1 ||
            folder.Id!=capture.Pin.FolderId || !folder.CanListChildren || folder.SharedDriveId!=capture.Attempt.SharedDriveId || folder.SharedDriveId is not null && !access.Shared)
            throw new CurrentControlObservationConflictException();
        if(access.Shared && !allowShared) throw new LibraryWorkspaceApprovalException("Review this workspace's sharing before publishing private metadata.");
        return(access.DriveVersion,access.Shared,folder.SharedDriveId);
    }
}
