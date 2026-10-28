using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Read-only historical signature evidence for the immutable original
/// capture. A witness Root selected the matching policy pair; the scope digest
/// does NOT identify the exact Root present at capture time. This grants no cloud
/// action, signing, replacement, retirement or revival of a revoked publisher.</summary>
public sealed class CurrentOriginalCatalogAuthority
{
    public WorkspacePublicationScope OriginalScope {get;}
    public Guid SnapshotId {get;}
    public Guid CommitIntentId {get;}
    public string Phase {get;}
    public Guid WitnessRootIntentId {get;}
    public long WitnessRootSequence {get;}
    public string WitnessRootSha256 {get;}
    public LibraryPolicyReference Publishers {get;}
    public LibraryPolicyReference Sources {get;}
    public string PublisherKeyId {get;}
    public long? CommitSequence {get;}
    public string? CommitBodySha256 {get;}
    public string? CommitEnvelopeSha256 {get;}

    internal CurrentOriginalCatalogAuthority(CatalogGenerationCapture capture,Guid rootId,LibraryCurrentControl root,
        string publisherKeyId,ProtocolSigningIntent? intent)
    {
        OriginalScope=capture.Scope;SnapshotId=capture.SnapshotId;CommitIntentId=capture.CommitIntentId;Phase=capture.Phase;
        WitnessRootIntentId=rootId;WitnessRootSequence=root.Sequence;WitnessRootSha256=root.EnvelopeSha256;
        Publishers=root.Publishers;Sources=root.Sources;PublisherKeyId=publisherKeyId;
        CommitSequence=intent?.Sequence;CommitBodySha256=intent?.BodySha256;CommitEnvelopeSha256=intent?.EnvelopeSha256;
    }
}

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Inspect only under the actual same-nonce native inventory/final
    /// Root fence. Walk bounded observed Roots, validating original policy bytes
    /// one pair at a time. Current publisher selection is deliberately NOT used
    /// to verify old signatures. No current inventory checkpoint is manufactured.</summary>
    public CurrentOriginalCatalogAuthority InspectOriginalCatalogAuthority(Guid generationId,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(fenced);
        using var transaction=_database.Connection.BeginTransaction();
        if(fenced.Phase!="fenced") throw new CurrentControlObservationConflictException();
        var actual=new CurrentControlTransitionJournal(_database,_budget).RequireSigningInventoryIn(signing,fenced,transaction,token);
        var pin=LibraryConnectionDescriptor.Decode(actual.ConnectionCode);
        var capture=RequiredCapture(generationId,transaction);var scope=capture.Scope;
        if(scope.LibraryId!=pin.LibraryId || scope.AccountId!=pin.Workspace.AccountId || scope.FolderId!=pin.Workspace.FolderId ||
            scope.AccountRevision>fenced.AccountRevision) throw new CatalogGenerationCaptureConflictException();
        var journal=new ProtocolSigningJournal(_database,_budget);
        var intent=journal.Read(capture.CommitIntentId,transaction);
        var publication=ReadPublication(capture,transaction);
        if(intent is not null)
        {
            AssertIntent(capture,publication?.Plan ?? throw new CatalogGenerationCaptureConflictException(),intent);
        }
        foreach(var witness in actual.Roots)
        {
            token.ThrowIfCancellationRequested();
            if(!witness.Observed) continue;
            var rootIntent=journal.Read(witness.Intent.Id,transaction) ?? throw new SigningHistoryRecoveryRequiredException();
            var root=LibraryCurrentControl.Read((rootIntent.Envelope ?? throw new SigningHistoryRecoveryRequiredException()).Span,pin);
            var publishers=root.ReadPublishers(journal.RetainedControllerReference(pin,root.Publishers,"library-publisher-authorization",transaction).Span,pin);
            var sources=root.ReadSources(journal.RetainedControllerReference(pin,root.Sources,"library-source-configuration",transaction).Span,pin);
            if(publishers.Publisher is not {} actor || actor.ActorId!=scope.ActorId || actor.Epoch!=scope.ActorEpoch ||
                sources.Revision!=scope.ConfigurationRevision) continue;
            var key=publishers.PublisherKeyId ?? throw new InvalidDataException("Historical publisher lost its verified key identity.");
            var descriptor=new LocalPublicationAuthorityDescriptor(pin.LibraryId,pin.ControllerId,pin.ControllerEpoch,pin.ControllerKeyId,
                actor.ActorId,actor.Epoch,key,publishers.Sequence,publishers.EnvelopeSha256,
                root.Sources.Sequence,root.Sources.Reference.Sha256,scope.AccountId,scope.FolderId,scope.ConfigurationRevision);
            if(descriptor.Sha256()!=scope.AuthoritySha256) continue;
            if(publication is not null && publication.Basis.SourcePolicySha256!=root.Sources.Reference.Sha256)
                throw new InvalidDataException("Original publication basis differs from its witnessed source policy.");
            if(intent?.Envelope is {} envelope)
            {
                var document=new SignedProtocolDocuments([CatalogPublicationReader.Capability,DriveCatalogFacts.Capability]).VerifyAndRead(envelope.Span,actor);
                _=new CatalogPublicationReader(pin.Workspace).ReadCommit(document);
            }
            if(intent is not null && !actual.Signing.Actors.Any(value=>value.ActorId==scope.ActorId && value.Epoch==scope.ActorEpoch && value.KeyId==key))
                throw new SigningHistoryRecoveryRequiredException();
            var result=new CurrentOriginalCatalogAuthority(capture,witness.Intent.Id,root,key,intent);
            token.ThrowIfCancellationRequested();transaction.Commit();return result;
        }
        // No policy cross-product, guessed key or current-policy substitution.
        // Preserve captures created before a retained Root can prove their origin.
        throw new SigningHistoryRecoveryRequiredException();
    }
}
