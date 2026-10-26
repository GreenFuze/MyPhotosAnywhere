using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Owned selected policy bytes and semantics, still PENDING. They share the
/// captured Root reader's lifetime, not a policy-publication or activation grant.</summary>
public sealed class LocalCurrentControlPolicies
{
    private readonly byte[] _publishers,_sources;
    public LocalCurrentControlCapture Capture {get;}
    public CatalogPublisherAuthorization Publishers {get;}
    public CatalogSourceConfiguration Sources {get;}
    public ReadOnlyMemory<byte> PublisherEnvelope=>_publishers.ToArray();
    public ReadOnlyMemory<byte> SourceEnvelope=>_sources.ToArray();
    internal LocalCurrentControlPolicies(LocalCurrentControlCapture capture,byte[] publisher,byte[] sources)
    {
        Capture=capture;_publishers=publisher.ToArray();_sources=sources.ToArray();
        var pin=capture.Slot.ConnectionDescriptor();
        Publishers=capture.Head.ReadPublishers(_publishers,pin);Sources=capture.Head.ReadSources(_sources,pin);
    }
}

public sealed partial class LocalCurrentControlReader
{
    public Task<LocalCurrentControlPolicies> ReadSelectedPoliciesAsync(LocalCurrentControlCapture capture,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return OperationAsync(async token=>
        {
            var scope=await RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
            return await _accounts.ReadDriveAsync(scope.Pin.AccountId,async(reader,revision,owned)=>
            {
                Check(caller,owned);if(revision!=scope.Attempt.AccountRevision) throw new StaleDriveConnectionException();
                if(reader is not IGoogleWorkspaceConditionalObservation source)
                    throw new InvalidOperationException("Selected policies need actual owned JSON observations.");
                var publisher=await ReadPolicyAsync(capture,source,capture.Head.Publishers.Reference,caller,owned).ConfigureAwait(false);
                // Reject a bad signature/sequence or revoked catalog selection before
                // admitting another role request. Full graph admission is separate.
                _=capture.Head.ReadPublishers(publisher,scope.Slot.ConnectionDescriptor());
                var sources=await ReadPolicyAsync(capture,source,capture.Head.Sources.Reference,caller,owned).ConfigureAwait(false);
                var policies=new LocalCurrentControlPolicies(capture,publisher,sources);
                _=await RequirePolicyCaptureAsync(capture,caller,owned).ConfigureAwait(false);return policies;
            },token).ConfigureAwait(false);
        },caller);
    }

    private Task<Scope> RequirePolicyCaptureAsync(LocalCurrentControlCapture capture,CancellationToken caller,CancellationToken token)=>
        _state.ExecuteAsync(database=>
        {
            Check(caller,token);
            if(!ReferenceEquals(capture.Owner,_identity) || capture.Head.ConnectionSha256!=capture.Attempt.ConnectionSha256 ||
                capture.Head.Sequence!=capture.Attempt.RootSequence || capture.Head.EnvelopeSha256!=capture.Attempt.ContentSha256 ||
                capture.Snapshot.Sha256!=capture.Head.EnvelopeSha256 || capture.Snapshot.Version!=capture.Attempt.Version)
                throw new CurrentControlObservationConflictException();
            var scope=new Scope(capture.Pin,capture.Slot,capture.Attempt);AssertScope(database,scope);
            new LocalCurrentControlObservations(database,_budget).AssertCaptured(capture.Attempt,token);return scope;
        },token);

    private async Task<byte[]> ReadPolicyAsync(LocalCurrentControlCapture capture,IGoogleWorkspaceConditionalObservation source,
        CatalogObjectReference reference,CancellationToken caller,CancellationToken token)
    {
        _=await RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
        if(reference.AccountId!=capture.Pin.AccountId || reference.WorkspaceId!=capture.Pin.FolderId || reference.FileId==capture.Slot.FileId)
            throw new CurrentControlObservationConflictException();
        var observer=new PolicyObserver(this,capture,reference,caller);
        var actual=await source.ObserveConditionalWorkspaceJsonAsync(reference.WorkspaceId,reference.FileId,observer,token).ConfigureAwait(false);
        Check(caller,token);
        if(!ReferenceEquals(actual,observer.Observation)) throw new CurrentControlObservationConflictException();
        _=await RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);return actual.Bytes.ToArray();
    }

    private sealed class PolicyObserver(LocalCurrentControlReader owner,LocalCurrentControlCapture capture,CatalogObjectReference reference,
        CancellationToken caller) : IGoogleWorkspaceConditionalObserver
    {
        private DriveWorkspaceConditionalMetadata? _metadata;
        private LocalCurrentGraphObservation? _durable;
        internal DriveWorkspaceConditionalJson? Observation {get;private set;}
        public async Task RememberMetadataAsync(DriveWorkspaceConditionalMetadata actual,CancellationToken token)
        {
            if(_metadata is not null) throw new CurrentControlObservationConflictException();
            Validate(actual);_=await owner.RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
            await owner._state.ExecuteAsync(database=>
            {
                owner.Check(caller,token);owner.AssertScope(database,new(capture.Pin,capture.Slot,capture.Attempt));
                new LocalCurrentControlObservations(database,owner._budget).AssertCaptured(capture.Attempt,token);
                var context=new WorkspaceObservationContext(capture.Pin.LibraryId,capture.Pin.AccountId,capture.Pin.FolderId,
                    capture.Attempt.AccountRevision,capture.Attempt.SharedDriveId);
                var known=new WorkspaceObservationRepository(database,owner._budget).Known(context).SingleOrDefault(row=>row.FileId==reference.FileId);
                if(known is not null && (actual.Version<known.Version || reference.Bytes!=known.Bytes || reference.Sha256!=known.Sha256 || known.MimeType is not null))
                    throw new WorkspaceObservationConflictException();
                _durable=new LocalCurrentGraphObservations(database,owner._budget).RememberMetadata(capture.Attempt,reference,
                    new(actual.Version,actual.ByteLength,"application/json",actual.SharedDriveId,actual.ParentsSha256),token);
                return true;
            },token).ConfigureAwait(false);
            _metadata=actual;
        }
        public async Task RememberBytesAsync(DriveWorkspaceConditionalJson actual,CancellationToken token)
        {
            Validate(actual.Metadata);
            if(_metadata is null || Observation is not null || actual.Version!=_metadata.Version || actual.EntityTag!=_metadata.EntityTag ||
                actual.Metadata.ParentsSha256!=_metadata.ParentsSha256 || _durable is null)
                throw new CurrentControlObservationConflictException();
            _=await owner.RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
            _durable=await owner._state.ExecuteAsync(database=>
            {
                owner.Check(caller,token);var records=new LocalCurrentGraphObservations(database,owner._budget);
                return actual.Sha256==reference.Sha256 ? records.RememberBytes(capture.Attempt,_durable,actual.Sha256,token) :
                    records.RememberRejectedPolicyBytes(capture.Attempt,_durable,actual.Sha256,token);
            },token).ConfigureAwait(false);
            if(actual.Sha256!=reference.Sha256) throw new CurrentControlObservationConflictException();
            Observation=actual;
        }
        private void Validate(DriveWorkspaceConditionalMetadata actual)
        {
            if(actual.AccountId!=reference.AccountId || actual.FolderId!=reference.WorkspaceId || actual.FileId!=reference.FileId ||
                actual.ByteLength!=reference.Bytes || actual.SharedDriveId!=capture.Attempt.SharedDriveId || actual.Version<1)
                throw new CurrentControlObservationConflictException();
        }
    }
}
