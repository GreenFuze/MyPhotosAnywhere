using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Prepare only the exact native predecessor under the actual current
    /// inventory/final fence. Retired superseded commits provide signed ancestry,
    /// never selected authority or permission to reuse their obsolete facts.</summary>
    public CapturedCatalogPublication CaptureCurrentPublicationBasis(Guid generationId,CapturedCatalogBasis basis,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(basis);ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();
        new WorkspaceObservationRepository(_database,_budget).AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var capture=RequiredCapture(generationId,transaction);var scope=capture.Scope;
        var actual=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(fenced,transaction,token);
        if(scope.LibraryId!=actual.Pin.LibraryId || scope.AccountId!=actual.Pin.Workspace.AccountId || scope.FolderId!=actual.Pin.Workspace.FolderId ||
            scope.AccountRevision>inventory.Context.AccountRevision) throw new CatalogGenerationCaptureConflictException();
        var journal=new ProtocolSigningJournal(_database,_budget);
        var policy=actual.Head.ReadPublishers(journal.RetainedControllerReference(actual.Pin,actual.Head.Publishers,"library-publisher-authorization",transaction).Span,actual.Pin);
        var reader=new OwnedCatalogLineageReader(this,scope,transaction,token);
        if(policy.Publisher is not {} actor || actor.ActorId!=scope.ActorId || actor.Epoch!=scope.ActorEpoch || policy.PublisherKeyId!=reader.Publisher.KeyId ||
            reader.Publisher.Sequence!=basis.ParentSequence) throw new CatalogGenerationCaptureConflictException();
        var selected=ReadPublishedHead(transaction);
        if(actual.Head.Catalog is null ? selected is not null : selected is null || selected.Commit!=actual.Head.Catalog)
            throw new CatalogGenerationCaptureConflictException();
        reader.RequirePath(basis.Parent,basis.ParentSequence,actual.Head.Catalog);
        using var write=CapturePublicationBasisIn(generationId,basis,null,transaction);
        token.ThrowIfCancellationRequested();write.Recheck();transaction.Commit();return write.Value;
    }

    internal void AssertCurrentCandidateLineageIn(Guid generationId,CatalogObjectReference? selectedAnchor,SqliteTransaction transaction,CancellationToken token)
    {
        var inputs=SelectionInputsIn(generationId,transaction);var reader=new OwnedCatalogLineageReader(this,inputs.Publication.Capture.Scope,transaction,token);
        if(reader.Publisher.Sequence!=inputs.Intent.Sequence || inputs.Intent.Sequence!=checked(inputs.Publication.Basis.ParentSequence+1))
            throw new CatalogGenerationCaptureConflictException();
        var selected=ReadPublishedHead(transaction);
        if(selectedAnchor is null ? selected is not null : selected is null || selected.Commit!=selectedAnchor)
            throw new CatalogGenerationCaptureConflictException();
        reader.RequirePath(inputs.Publication.Basis.Parent,inputs.Publication.Basis.ParentSequence,selectedAnchor);
    }

    /// <summary>One transaction-owned iterative reader. Original capture/receipt
    /// bindings do not recursively validate previous captures. Every traversed
    /// disposition is checked here before any caller may use the resulting path.</summary>
    private sealed class OwnedCatalogLineageReader
    {
        private readonly CatalogGenerationCaptureRepository _owner;
        private readonly WorkspacePublicationScope _scope;
        private readonly SqliteTransaction _transaction;
        private readonly CancellationToken _token;
        private readonly Dictionary<long,ProtocolSigningIntentExpectation> _intents;
        private readonly Dictionary<long,CurrentOwnedCatalogSelection> _loaded=[];
        public ProtocolSigningActorExpectation Publisher {get;}

        internal OwnedCatalogLineageReader(CatalogGenerationCaptureRepository owner,WorkspacePublicationScope scope,SqliteTransaction transaction,CancellationToken token)
        {
            _owner=owner;_scope=scope;_transaction=transaction;_token=token;
            if(!ReferenceEquals(transaction.Connection,owner._database.Connection)) throw new InvalidOperationException("Owned lineage requires its original transaction.");
            var native=new ProtocolSigningJournal(owner._database,owner._budget).Inventory(scope.LibraryId,transaction);
            Publisher=native.Actors.SingleOrDefault(v=>v.ActorId==scope.ActorId && v.Epoch==scope.ActorEpoch && v.Role=="publisher")
                ?? throw new InvalidDataException("Owned lineage lost its registered publisher.");
            _intents=native.Intents.Where(v=>v.ActorId==scope.ActorId && v.Epoch==scope.ActorEpoch).ToDictionary(v=>v.Sequence);
        }

        // Native high-water is authoritative; the selected Root can lag behind a
        // terminal unselected signed commit. Never infer native sequence from Root.
        internal void RequireNativeTip(CatalogObjectReference? anchor)=>_=ReadNativeTip(anchor);

        internal (CatalogObjectReference? Parent,long Sequence) ReadNativeTip(CatalogObjectReference? anchor)
        {
            if(Publisher.Sequence==0) {RequirePath(null,0,anchor);return(null,0);}
            if(!_intents.TryGetValue(Publisher.Sequence,out var intent) || intent.Kind!="catalog-commit" || intent.EnvelopeSha256 is null)
                throw new SigningHistoryRecoveryRequiredException();
            var work=new WorkspacePublicationRepository(_owner._database,_owner._budget).Read(intent.Id,_transaction)
                ?? throw new WorkspacePublicationConflictException();
            if(work.FileId is null) throw new WorkspacePublicationConflictException();
            var tip=new CatalogObjectReference(work.Scope.AccountId,work.FileId,work.Scope.FolderId,work.Payload.Length,work.Sha256);
            _=Read(Publisher.Sequence,tip);RequirePath(tip,Publisher.Sequence,anchor);return(tip,Publisher.Sequence);
        }

        internal void RequirePath(CatalogObjectReference? parent,long sequence,CatalogObjectReference? anchor)
        {
            if(sequence<0 || sequence>Publisher.Sequence || (parent is null)!=(sequence==0)) throw new CatalogGenerationCaptureConflictException();
            var anchorSequence=AnchorSequence(anchor);var visited=new HashSet<long>();
            while(parent!=anchor)
            {
                _token.ThrowIfCancellationRequested();
                if(parent is null || sequence<=anchorSequence || !visited.Add(sequence) || visited.Count>ProtocolSigningJournal.MaximumInventoryIntents)
                    throw new InvalidDataException("Owned lineage is missing, cyclic or exceeds its existing native bound.");
                var inputs=Read(sequence,parent);var capture=inputs.Publication.Capture;
                if(capture.Phase!="retired") throw new CatalogGenerationCaptureConflictException();
                var disposition=_owner.ReadOriginalOwnedDisposition(capture,_transaction,_token);
                if(disposition.SelectedAnchor!=anchor || inputs.Publication.Basis.ParentSequence!=sequence-1)
                    throw new InvalidDataException("Owned lineage changed its actual selected anchor or contiguous native parent.");
                parent=inputs.Publication.Basis.Parent;sequence=inputs.Publication.Basis.ParentSequence;
            }
            if(sequence!=anchorSequence) throw new InvalidDataException("Owned lineage substituted its selected anchor sequence.");
        }

        private long AnchorSequence(CatalogObjectReference? anchor)
        {
            if(anchor is null) return 0;
            foreach(var intent in _intents.Values.OrderBy(v=>v.Sequence))
            {
                _token.ThrowIfCancellationRequested();if(intent.Kind!="catalog-commit") continue;
                var work=new WorkspacePublicationRepository(_owner._database,_owner._budget).Read(intent.Id,_transaction);
                if(work is null || work.FileId!=anchor.FileId || work.Sha256!=anchor.Sha256 || work.Payload.Length!=anchor.Bytes) continue;
                var inputs=Read(intent.Sequence,anchor);var capture=inputs.Publication.Capture;
                if(capture.Phase is not ("complete" or "snapshot-retiring" or "retired") ||
                    _owner.ReadCurrentGenerationVerification(capture,_transaction) is null && _owner.ReadVerification(capture,_transaction) is null)
                    throw new InvalidDataException("Owned lineage lost the actual acknowledged selected anchor.");
                return intent.Sequence;
            }
            throw new InvalidDataException("Owned lineage cannot substitute an unacknowledged or foreign anchor.");
        }

        private CurrentOwnedCatalogSelection Read(long sequence,CatalogObjectReference reference)
        {
            if(_loaded.TryGetValue(sequence,out var old))
            {
                if(Reference(old)!=reference) throw new InvalidDataException("Owned lineage substituted a native reference.");
                return old;
            }
            if(!_intents.TryGetValue(sequence,out var intent) || intent.Kind!="catalog-commit" || intent.EnvelopeSha256 is null)
                throw new SigningHistoryRecoveryRequiredException();
            var work=new WorkspacePublicationRepository(_owner._database,_owner._budget).Read(intent.Id,_transaction)
                ?? throw new WorkspacePublicationConflictException();
            var capture=_owner.ReadOriginalCaptureFields(work.Scope.GenerationId,_transaction)
                ?? throw new InvalidDataException("Owned lineage lost its original capture.");
            _owner.ValidatePublicationState(capture,_transaction);var inputs=_owner.SelectionInputsIn(capture,_transaction);
            if(capture.Scope.LibraryId!=_scope.LibraryId || capture.Scope.AccountId!=_scope.AccountId || capture.Scope.FolderId!=_scope.FolderId ||
                capture.Scope.ActorId!=_scope.ActorId || capture.Scope.ActorEpoch!=_scope.ActorEpoch || inputs.Intent.Id!=intent.Id ||
                inputs.Intent.Sequence!=sequence || Reference(inputs)!=reference)
                throw new InvalidDataException("Owned lineage changed its original native key, workspace or commit.");
            _loaded.Add(sequence,inputs);return inputs;
        }
        private static CatalogObjectReference Reference(CurrentOwnedCatalogSelection value)=>new(value.Work.Scope.AccountId,value.Work.FileId!,
            value.Work.Scope.FolderId,value.Work.Payload.Length,value.Work.Sha256);
    }
}
