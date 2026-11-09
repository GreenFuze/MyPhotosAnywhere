using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices;

/// <summary>Borrowed private staging only. Await every bounded leaf write; no network,
/// recursive reader operation, activation or borrowed-owner retirement. Earlier leaves
/// may remain after failure and cannot become a complete/active generation.</summary>
public interface ILocalCurrentCatalogStage
{
    Task AddAsync(CatalogFactsLeaf leaf,CancellationToken cancellationToken);
}

/// <summary>Normally completed selected ancestry/covered-facts read, still PENDING.
/// Final current fencing, local policy/history/human approval and atomic staging
/// admission remain mandatory. Never infer an active head from a higher orphan commit.</summary>
public sealed class LocalCurrentControlGraph
{
    internal object Owner {get;}
    internal ILocalCurrentCatalogStage Stage {get;}
    public LocalCurrentControlPolicies Policies {get;}
    public CatalogObjectReference? Catalog=>Policies.Capture.Head.Catalog;
    public long Sequence {get;}
    public int Commits {get;}
    public long Assets {get;}
    public ReadOnlyCollection<LocalCurrentGraphObservation> Observations {get;}
    internal LocalCurrentControlGraph(object owner,ILocalCurrentCatalogStage stage,LocalCurrentControlPolicies policies,long sequence,int commits,long assets,
        IEnumerable<LocalCurrentGraphObservation> observations)
    {Owner=owner;Stage=stage;Policies=policies;Sequence=sequence;Commits=commits;Assets=assets;Observations=observations.ToList().AsReadOnly();}
}

/// <summary>Complete selected staging read plus the last actual current fence, tied
/// to this live reader. Local authority/history and serialized atomic activation are
/// separate; persisted phases alone cannot reconstruct this proof after restart.</summary>
public sealed class LocalCurrentGraphFence
{
    internal object Owner {get;}
    public LocalCurrentControlGraph Graph {get;}
    public LocalCurrentControlFence Current {get;}
    internal LocalCurrentGraphFence(object owner,LocalCurrentControlGraph graph,LocalCurrentControlFence current)
    {Owner=owner;Graph=graph;Current=current;}
}

public sealed partial class LocalCurrentControlReader
{
    private LocalCurrentControlGraph? _graph;

    public Task<LocalCurrentControlGraph> ReadSelectedGraphAsync(LocalCurrentControlPolicies policies,ILocalCurrentCatalogStage stage,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(policies);ArgumentNullException.ThrowIfNull(stage);
        return OperationAsync(async token=>
        {
            _graph=null;var capture=policies.Capture;
            var scope=await RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
            var pin=scope.Slot.ConnectionDescriptor();
            _=capture.Head.ReadPublishers(policies.PublisherEnvelope.Span,pin);_=capture.Head.ReadSources(policies.SourceEnvelope.Span,pin);
            if(capture.Head.Catalog is null)
            {
                _=await RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
                return _graph=new(_identity,stage,policies,0,0,0,[]);
            }
            var publisher=policies.Publishers.Publisher ?? throw new ProtocolFormatException("A revoked publisher cannot select a catalog.");
            return await _accounts.ReadDriveAsync(scope.Pin.AccountId,async(reader,revision,owned)=>
            {
                Check(caller,owned);if(revision!=scope.Attempt.AccountRevision) throw new StaleDriveConnectionException();
                if(reader is not IGoogleWorkspaceObservedObjectReader transport)
                    throw new InvalidOperationException("Current graph reads require joined actual metadata/byte checkpoints.");
                var source=new CurrentGraphObjects(this,capture,transport,capture.Head,caller);
                VerifiedProtocolDocument? head=null;var commits=0;long assets=0;
                await foreach(var entry in new CatalogCommitHistoryTraversal(pin.Workspace,publisher,source).ReadAsync(capture.Head.Catalog,owned).ConfigureAwait(false))
                {
                    await _state.ExecuteAsync(database=>
                    {
                        Check(caller,owned);_keys.AssertControllerIdentity(capture.Pin);
                        new LocalCurrentControlObservations(database,_budget).AssertCaptured(capture.Attempt,owned);
                        var native=_keys.PublicIdentity;
                        if(entry.Verified.ActorId==native.PublisherId && entry.Verified.Epoch==native.PublisherEpoch)
                            _=new ProtocolSigningJournal(database,_budget).AssertRetainedCloudEnvelope(native.PublisherId,native.PublisherEpoch,entry.Envelope.Span);
                        return true;
                    },owned).ConfigureAwait(false);
                    head ??=entry.Verified;commits=checked(commits+1);source.CommitIds.Add(entry.Reference.FileId);
                }
                if(head is null) throw new InvalidOperationException("The selected complete history lost its first commit.");
                source.BeginFacts();
                await foreach(var leaf in new CatalogFactsTraversal(pin.LibraryId,pin.Workspace,source,WorkspaceObservationRepository.MaximumDocuments)
                    .ReadCoveredLeavesAsync(head,(coverage,cancellation)=>
                    {
                        Check(caller,cancellation);policies.Sources.ValidateCoverage(coverage);return Task.CompletedTask;
                    },owned).ConfigureAwait(false))
                {
                    _=await RequirePolicyCaptureAsync(capture,caller,owned).ConfigureAwait(false);
                    await stage.AddAsync(leaf,owned).ConfigureAwait(false);
                    _=await RequirePolicyCaptureAsync(capture,caller,owned).ConfigureAwait(false);
                    assets=checked(assets+leaf.Facts.Assets.Count);
                }
                _=await RequirePolicyCaptureAsync(capture,caller,owned).ConfigureAwait(false);
                return _graph=new(_identity,stage,policies,head.Sequence,commits,assets,source.Observations.Values);
            },token).ConfigureAwait(false);
        },caller);
    }

    public Task<LocalCurrentGraphFence> FenceSelectedGraphAsync(LocalCurrentControlGraph graph,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return OperationAsync(async token=>
        {
            if(!ReferenceEquals(graph.Owner,_identity) || !ReferenceEquals(graph,_graph)) throw new CurrentControlObservationConflictException();
            var fenced=await FenceCapturedCoreAsync(graph.Policies.Capture,caller,token).ConfigureAwait(false);
            Check(caller,token);return new LocalCurrentGraphFence(_identity,graph,fenced);
        },caller);
    }

    private sealed class CurrentGraphObjects(LocalCurrentControlReader owner,LocalCurrentControlCapture capture,
        IGoogleWorkspaceObservedObjectReader transport,LibraryCurrentControl target,CancellationToken caller) : ICatalogObjectSource
    {
        private readonly Dictionary<string,CatalogObjectReference> _references=new(StringComparer.Ordinal);
        private bool _facts;
        internal HashSet<string> CommitIds {get;}=new(StringComparer.Ordinal);
        internal Dictionary<string,LocalCurrentGraphObservation> Observations {get;}=new(StringComparer.Ordinal);
        internal void BeginFacts()=>_facts=true;
        public async Task<ReadOnlyMemory<byte>> ReadAsync(CatalogObjectReference reference,CancellationToken token)
        {
            _=await owner.RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
            if(reference.AccountId!=capture.Pin.AccountId || reference.WorkspaceId!=capture.Pin.FolderId || reference.FileId==capture.Slot.FileId ||
                reference.FileId==target.Publishers.Reference.FileId || reference.FileId==target.Sources.Reference.FileId ||
                _facts && CommitIds.Contains(reference.FileId)) throw new CurrentControlObservationConflictException();
            if(_references.TryGetValue(reference.FileId,out var previous))
            {if(previous!=reference) throw new WorkspaceObservationConflictException();}
            else
            {
                if(_references.Count>=WorkspaceObservationRepository.MaximumDocuments) throw new CatalogTraversalLimitException();
                _references.Add(reference.FileId,reference);
            }
            var observer=new GraphObjectObserver(owner,capture,reference,!_facts,caller);
            var actual=await transport.ReadObservedWorkspaceObjectAsync(reference.WorkspaceId,reference.FileId,reference.Bytes,reference.Sha256,observer,token).ConfigureAwait(false);
            _=await owner.RequirePolicyCaptureAsync(capture,caller,token).ConfigureAwait(false);
            var observed=observer.Observed ?? throw new InvalidOperationException("A complete graph read lost its actual byte checkpoint.");
            var bytes=actual.Bytes.ToArray();
            if(actual.AccountId!=reference.AccountId || actual.FolderId!=reference.WorkspaceId || actual.FileId!=reference.FileId ||
                actual.SharedDriveId!=capture.Attempt.SharedDriveId || actual.MimeType!=observed.Metadata.MimeType ||
                actual.DriveVersion!=observed.Metadata.Version || bytes.LongLength!=reference.Bytes ||
                Convert.ToHexStringLower(SHA256.HashData(bytes))!=reference.Sha256 || observed.BodySha256!=reference.Sha256)
                throw new WorkspaceObservationConflictException();
            Observations[reference.FileId]=observed;return bytes;
        }
    }

    private sealed class GraphObjectObserver(LocalCurrentControlReader owner,LocalCurrentControlCapture capture,CatalogObjectReference reference,
        bool jsonOnly,CancellationToken caller) : IGoogleWorkspaceObjectObserver
    {
        private LocalCurrentGraphObservation? _metadata;
        internal LocalCurrentGraphObservation? Observed {get;private set;}
        public async Task RememberMetadataAsync(DriveWorkspaceObjectMetadata actual,CancellationToken token)
        {
            Validate(actual);
            _metadata=await owner._state.ExecuteAsync(database=>
            {
                // The repository checks the full Root nonce/pin/slot/account and
                // retained history in THIS transaction. Do not perform the same
                // expensive verification again in a preceding state operation.
                owner.Check(caller,token);owner._keys.AssertControllerIdentity(capture.Pin);
                return new LocalCurrentGraphObservations(database,owner._budget).RememberMetadata(capture.Attempt,reference,Metadata(actual),token);
            },token).ConfigureAwait(false);
        }
        public async Task RememberBytesAsync(DriveWorkspaceObjectObservation actual,CancellationToken token)
        {
            Validate(actual.Metadata);
            if(_metadata is null || Observed is not null || _metadata.Metadata!=Metadata(actual.Metadata)) throw new WorkspaceObservationConflictException();
            Observed=await owner._state.ExecuteAsync(database=>
            {
                owner.Check(caller,token);owner._keys.AssertControllerIdentity(capture.Pin);
                return new LocalCurrentGraphObservations(database,owner._budget).RememberBytes(capture.Attempt,_metadata,actual.Sha256,token);
            },token).ConfigureAwait(false);
        }
        private void Validate(DriveWorkspaceObjectMetadata actual)
        {
            if(actual.AccountId!=reference.AccountId || actual.FolderId!=reference.WorkspaceId || actual.FileId!=reference.FileId ||
                actual.ByteLength!=reference.Bytes || actual.SharedDriveId!=capture.Attempt.SharedDriveId || jsonOnly && actual.MimeType!="application/json")
                throw new CurrentControlObservationConflictException();
        }
        private static LocalCurrentGraphMetadata Metadata(DriveWorkspaceObjectMetadata actual)=>
            new(actual.Version,actual.ByteLength,actual.MimeType,actual.SharedDriveId,actual.ParentsSha256);
    }
}
