using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class WorkspaceObservationRepository
{
    // Local postcondition ONLY after the enclosing Core transaction has checked
    // the actual complete checkpoint, performed one exact native header mutation
    // and independently verified that complete native delta. No old attempt or
    // checkpoint is rewritten, and no current cloud admission is returned.
    internal void AssertCurrentHeaderObservationAfterNativeMutationIn(
        CurrentWorkspaceInventoryCheckpoint checkpoint,CurrentControlSigningInventory before,
        CurrentControlSigningInventory after,LocalCurrentControlAttempt fenced,
        WorkspacePublicationInventory publications,SqliteTransaction transaction,
        WorkspacePublicationRecord? prepared,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection))
            throw new InvalidOperationException("Header observation postconditions require their owning transaction.");
        if(ReadCurrentInventoryIn(transaction,token)!=checkpoint || checkpoint.SigningSha256!=before.Sha256)
            throw new WorkspaceObservationConflictException();
        var attempt=ReadAttempt(transaction)??throw new WorkspaceObservationConflictException();
        var history=CaptureCurrentHistoryIn(after,fenced,transaction,token);
        var root=history.Signing.Roots.Single(value=>value.Intent.EnvelopeSha256==fenced.ContentSha256 && value.Observed).Intent;
        if(history.Context!=attempt.Context || history.RetainedAttempt!=attempt || attempt.Completed is not null ||
            attempt.FirstUnresolvedCursor is null || attempt.InventoryStartCursor is null ||
            attempt.HistoryReplay is not{Active:true,AtEnd:true} ||
            attempt.Id!=checkpoint.AttemptId || attempt.Revision!=checkpoint.AttemptRevision || checkpoint.Context!=attempt.Context ||
            checkpoint.RootAttemptId!=fenced.Id || checkpoint.RootIntentId!=root.Id || checkpoint.RootSequence!=root.Sequence ||
            checkpoint.ConnectionSha256!=fenced.ConnectionSha256 || checkpoint.RootSha256!=fenced.ContentSha256 ||
            checkpoint.RootVersion!=fenced.Version || checkpoint.Documents!=history.ImmutableObservations.Count ||
            checkpoint.InventorySha256!=InventoryHash(history.ImmutableObservations) ||
            checkpoint.PreservedObservationSha256!=history.CompleteObservationSha256 ||
            checkpoint.PreservedCandidateSha256!=history.CompleteCandidateSha256 ||
            checkpoint.PreservedHistorySha256!=HistoryDigest(ReadHistory(attempt.Context,transaction).Values) ||
            attempt.SigningInventorySha256!=before.Signing.Sha256 || publications.Context!=attempt.Context ||
            checkpoint.PublicationSha256!=publications.Sha256)
            throw new WorkspaceObservationConflictException();
        var outbox=new WorkspacePublicationRepository(_database,_budget);
        if(prepared is null) outbox.AssertInventoryUnchanged(publications,transaction);
        else outbox.AssertInventoryAfterPreparedObjectIn(publications,prepared,transaction);
        // The pre-mutation native hash remains in the original scan. Its deliberate
        // single delta is verified against actual after state by the native owner;
        // accepting that delta here never updates this older scan's permission.
        AssertHistoryReplay(attempt,transaction);token.ThrowIfCancellationRequested();
    }
}
