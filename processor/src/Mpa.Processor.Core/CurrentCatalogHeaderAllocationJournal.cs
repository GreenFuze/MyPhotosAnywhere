using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

internal interface IReturnedOriginalHeaderAllocationContext
{
    (CatalogGenerationCapture Capture,ProtocolSigningIntent Intent,WorkspacePublicationRecord Expected,string FileId)
        Resolve(ProcessorStateDatabase database,SqliteTransaction transaction);
}

internal sealed class CurrentHeaderAllocationWrite(WorkspacePublicationRecord value,StorageReservation? storage) : IDisposable
{
    private int _disposed;
    internal WorkspacePublicationRecord Value {get;}=value;
    internal void Recheck()
    {ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);storage?.RecheckBeforeWrite(storage.Bytes);}
    public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)==0) storage?.Dispose();}
}

public sealed partial class WorkspacePublicationRepository
{
    internal CurrentHeaderAllocationWrite RetainOriginalHeaderAllocationIn(IReturnedOriginalHeaderAllocationContext context,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Header allocation checkpoint requires its owning transaction.");
        var original=context.Resolve(_database,transaction);var expected=original.Expected;var intent=original.Intent;
        var scope=original.Capture.Scope;var fileId=original.FileId;
        CheckId(expected.Id);ValidateScope(scope);
        if(!Identity(fileId) || fileId is "root" || fileId==scope.FolderId ||
            expected.Id!=original.Capture.CommitIntentId || expected.Scope!=scope ||
            intent.Id!=expected.Id || intent.LibraryId!=scope.LibraryId || intent.ActorId!=scope.ActorId || intent.Epoch!=scope.ActorEpoch ||
            intent.Kind!="catalog-commit" || intent.Envelope is not {} envelope || intent.EnvelopeSha256 is null ||
            expected.Role!="catalog-commit" || expected.MimeType!="application/json" || expected.Sha256!=intent.EnvelopeSha256 ||
            !expected.Payload.Span.SequenceEqual(envelope.Span)) throw new WorkspacePublicationConflictException();
        var actual=Read(expected.Id,transaction) ?? throw new WorkspacePublicationConflictException();
        if(actual.Scope!=scope || actual.Role!=expected.Role || actual.MimeType!=expected.MimeType || actual.Sha256!=expected.Sha256 ||
            !actual.Payload.Span.SequenceEqual(envelope.Span) || actual.FileId is not null && actual.FileId!=fileId)
            throw new WorkspacePublicationConflictException();
        var slot=new LibraryCurrentControlSlotRepository(_database,_budget).ReadIn(transaction);
        if(slot?.FileId==fileId) throw new WorkspacePublicationConflictException();
        using(var alias=Command(transaction,"SELECT 1 FROM workspace_publication WHERE account_id=$account AND file_id=$file AND id<>$id LIMIT 1",
            ("$account",scope.AccountId),("$file",fileId),("$id",actual.Id.ToString("D"))))
            if(alias.ExecuteScalar() is not null) throw new WorkspacePublicationConflictException();
        if(actual.FileId is not null) return new(actual,null);
        if(actual.VerifiedVersion is not null) throw new WorkspacePublicationConflictException();
        StorageReservation? storage=_budget.Reserve(65536);
        try
        {
            storage.RecheckBeforeWrite(storage.Bytes);
            using(var update=Command(transaction,"UPDATE workspace_publication SET file_id=$file WHERE id=$id AND file_id IS NULL AND verified_version IS NULL",
                ("$file",fileId),("$id",actual.Id.ToString("D"))))
                if(update.ExecuteNonQuery()!=1) throw new InvalidOperationException("Known header allocation lost its original pending row.");
            var after=Read(actual.Id,transaction) ?? throw new WorkspacePublicationConflictException();
            if(after.Scope!=actual.Scope || after.Role!=actual.Role || after.MimeType!=actual.MimeType || after.Sha256!=actual.Sha256 ||
                after.FileId!=fileId || after.VerifiedVersion is not null || !after.Payload.Span.SequenceEqual(actual.Payload.Span))
                throw new WorkspacePublicationConflictException();
            // An injected write trigger may not retarget native/captured evidence
            // behind the returned ID. Failure rolls back without forgetting the ID.
            _=context.Resolve(_database,transaction);
            var result=new CurrentHeaderAllocationWrite(after,storage);storage=null;return result;
        }
        finally{storage?.Dispose();}
    }
}

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>LOCAL immutable checkpoint for an actual returned GenerateIds ID.
    /// Retain it despite retired access, without generating, creating or blessing a
    /// new inventory. It does not move a phase, head, counter or selected pointer.</summary>
    public WorkspacePublicationRecord RetainReturnedOriginalHeaderAllocation(WorkspacePublicationRecord expected,string fileId)
    {
        ArgumentNullException.ThrowIfNull(expected);ArgumentNullException.ThrowIfNull(expected.Scope);
        using var transaction=_database.Connection.BeginTransaction();
        var context=new ReturnedOriginalHeaderAllocationContext(this,expected,fileId);
        using var write=new WorkspacePublicationRepository(_database,_budget).RetainOriginalHeaderAllocationIn(context,transaction);
        write.Recheck();transaction.Commit();return write.Value;
    }

    private sealed class ReturnedOriginalHeaderAllocationContext(CatalogGenerationCaptureRepository owner,
        WorkspacePublicationRecord expected,string fileId) : IReturnedOriginalHeaderAllocationContext
    {
        public (CatalogGenerationCapture Capture,ProtocolSigningIntent Intent,WorkspacePublicationRecord Expected,string FileId)
            Resolve(ProcessorStateDatabase database,SqliteTransaction transaction)
        {
            if(!ReferenceEquals(database,owner._database)) throw new InvalidOperationException("Header allocation cannot cross immutable journal owners.");
            var capture=owner.RequiredCapture(expected.Scope.GenerationId,transaction);
            var publication=owner.ReadPublication(capture,transaction) ?? throw new CatalogGenerationCaptureConflictException();
            var plan=publication.Plan ?? throw new CatalogGenerationCaptureConflictException();
            var intent=new ProtocolSigningJournal(database,owner._budget).Read(capture.CommitIntentId,transaction)
                ?? throw new SigningHistoryRecoveryRequiredException();
            AssertIntent(capture,plan,intent);
            if(capture.Scope!=expected.Scope || capture.CommitIntentId!=expected.Id || intent.Envelope is null ||
                intent.Sequence!=checked(publication.Basis.ParentSequence+1)) throw new WorkspacePublicationConflictException();
            return(capture,intent,expected,fileId);
        }
    }
}
