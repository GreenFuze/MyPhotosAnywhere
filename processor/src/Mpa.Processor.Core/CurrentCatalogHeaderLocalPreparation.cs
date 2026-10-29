using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

// Only the private current header context resolves original work under the SAME
// borrowed transaction. No boolean old-scope bypass or generic public payload grant.
internal interface ICurrentOriginalHeaderWriteContext
{
    (CatalogGenerationCapture Capture,ProtocolSigningIntent Intent) Resolve(ProcessorStateDatabase database,SqliteTransaction transaction);
}

internal sealed class CurrentHeaderPendingWrite(WorkspacePublicationRecord value,StorageReservation? storage) : IDisposable
{
    private int _disposed;
    internal WorkspacePublicationRecord Value {get;}=value;
    internal void Recheck()
    {ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);storage?.RecheckBeforeWrite(storage.Bytes);}
    public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)==0) storage?.Dispose();}
}

public sealed partial class WorkspacePublicationRepository
{
    // LOCAL bookkeeping only. Caller owns/rechecks this returned storage claim
    // through commit. Ordinary Prepare/AssertCurrent/ConfirmVerified stay intact.
    internal CurrentHeaderPendingWrite PrepareCurrentOriginalHeaderIn(ICurrentOriginalHeaderWriteContext context,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Original header preparation needs its owning transaction.");
        var original=context.Resolve(_database,transaction);var capture=original.Capture;var intent=original.Intent;var scope=capture.Scope;
        if(intent.Envelope is not {} envelope || intent.Id!=capture.CommitIntentId || intent.LibraryId!=scope.LibraryId ||
            intent.ActorId!=scope.ActorId || intent.Epoch!=scope.ActorEpoch || intent.Kind!="catalog-commit" || intent.EnvelopeSha256 is null)
            throw new ProtocolSigningConflictException();
        var id=capture.CommitIntentId;CheckId(id);ValidateScope(scope);ValidateContent("catalog-commit","application/json");
        if(envelope.Length is <1 or >MaximumPayloadBytes) throw new InvalidDataException("Original header exceeds the existing metadata payload bound.");
        using(var reference=Command(transaction,"SELECT 1 FROM processor_meta WHERE key=$key",("$key","catalog_dependency_reference_v1:"+id.ToString("D"))))
            if(reference.ExecuteScalar() is not null) throw new WorkspacePublicationConflictException();
        var existing=Read(id,transaction);
        if(existing is not null)
        {
            if(existing.Scope!=scope || existing.Role!="catalog-commit" || existing.MimeType!="application/json" ||
                existing.Sha256!=intent.EnvelopeSha256 || !existing.Payload.Span.SequenceEqual(envelope.Span)) throw new WorkspacePublicationConflictException();
            return new(existing,null);
        }
        var owned=envelope.ToArray();var sha=Hash(owned);var encoded=EncodeScope(scope);
        if(sha!=intent.EnvelopeSha256) throw new ProtocolSigningConflictException();
        StorageReservation? storage=_budget.Reserve(checked(owned.LongLength*3+65536));
        try
        {
            storage.RecheckBeforeWrite(storage.Bytes);
            using(var insert=Command(transaction,"""
                INSERT INTO workspace_publication(id,account_id,scope,context_hash,role,mime_type,payload,payload_hash)
                VALUES($id,$account,$scope,$scopeHash,'catalog-commit','application/json',$payload,$hash)
                """,("$id",id.ToString("D")),("$account",scope.AccountId),("$scope",encoded),
                ("$scopeHash",ContextHash(id,encoded,"catalog-commit","application/json",sha)),("$payload",owned),("$hash",sha)))
                if(insert.ExecuteNonQuery()!=1) throw new InvalidOperationException("Original header pending preparation lost its fixed identity.");
            var result=new CurrentHeaderPendingWrite(new(id,scope,"catalog-commit","application/json",sha,null,null,owned),storage);
            storage=null;return result;
        }
        finally{storage?.Dispose();}
    }
}

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Reconstruct only the exact LOCAL pending row from an already sealed
    /// original native header. It grants no actual cloud operation or completion.
    /// No complete inventory is fabricated to bridge the sealed/no-outbox crash gap.</summary>
    public WorkspacePublicationRecord PrepareCurrentOriginalHeader(Guid generationId,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,CancellationToken token=default)
    {
        Id(generationId);ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(fenced);
        using var transaction=_database.Connection.BeginTransaction();
        // Resolve and recheck actual Root/native/original-plan inputs in the exact
        // transaction that will insert. The private context cannot be made by App.
        var context=new CurrentOriginalHeaderWriteContext(this,generationId,signing,fenced,token);
        using var write=new WorkspacePublicationRepository(_database,_budget).PrepareCurrentOriginalHeaderIn(context,transaction);
        var capture=RequiredCapture(generationId,transaction);
        using var phaseStorage=capture.Phase=="dependencies-ready" ? _budget.Reserve(65536) : null;
        if(capture.Phase=="dependencies-ready")
        {
            phaseStorage!.RecheckBeforeWrite(phaseStorage.Bytes);ChangePhase(capture,"commit-sealed",transaction);
        }
        else if(capture.Phase!="commit-sealed") throw new CatalogGenerationCaptureConflictException();
        token.ThrowIfCancellationRequested();write.Recheck();phaseStorage?.RecheckBeforeWrite(phaseStorage.Bytes);
        transaction.Commit();return write.Value;
    }

    private sealed class CurrentOriginalHeaderWriteContext(CatalogGenerationCaptureRepository owner,Guid generationId,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,CancellationToken token) : ICurrentOriginalHeaderWriteContext
    {
        public (CatalogGenerationCapture Capture,ProtocolSigningIntent Intent) Resolve(ProcessorStateDatabase database,SqliteTransaction transaction)
        {
            if(!ReferenceEquals(database,owner._database)) throw new InvalidOperationException("Original header write cannot cross state owners.");
            var actual=owner.ReadCurrentHeaderInputsIn(generationId,signing,fenced,transaction,token);
            if(actual.Intent.Envelope is null) throw new SigningHistoryRecoveryRequiredException();
            return(actual.Capture,actual.Intent);
        }
    }
}
