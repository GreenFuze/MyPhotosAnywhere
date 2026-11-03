using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Atomically retain the first exact native envelope, its original
    /// pending header and commit-sealed phase under an actual current-scope scan.
    /// Return durable progress only; a new real scan is required for provider IO.</summary>
    public WorkspacePublicationRecord SealCurrentFreshCatalogHeader(
        CurrentCatalogHeaderInspection expected,DocumentSignature? signature,
        CurrentWorkspaceInventoryCheckpoint inventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        token.ThrowIfCancellationRequested();using var transaction=_database.Connection.BeginTransaction();
        var observations=new WorkspaceObservationRepository(_database,_budget);
        observations.AssertCurrentInventoryUnchangedIn(inventory,signing,fenced,publications,transaction,token);
        var generation=expected.OriginalScope.GenerationId;
        var before=ReadCurrentFreshHeaderInputsIn(generation,signing,fenced,transaction,token);
        if(before.Inspection.ClaimSha256!=expected.ClaimSha256) throw new CurrentControlObservationConflictException();
        if((before.Intent.Envelope is null)!=(signature is not null)) throw new ProtocolSigningConflictException();
        var journal=new ProtocolSigningJournal(_database,_budget);
        using var sealedWrite=signature is null?null:journal.SealIn(before.Intent.Id,before.Intent.BodySha256,signature,transaction);
        var roots=new CurrentControlTransitionJournal(_database,_budget);
        var afterSigning=sealedWrite is null?roots.RequireSigningInventoryIn(signing,fenced,transaction,token):
            roots.AssertOnlyCurrentHeaderEnvelopeChangedIn(signing,fenced,sealedWrite.Value,transaction,token);
        var context=new CurrentFreshHeaderPendingContext(this,generation,afterSigning,fenced,token);
        using var pending=new WorkspacePublicationRepository(_database,_budget).PrepareCurrentOriginalHeaderIn(context,transaction);
        using var phaseStorage=before.Capture.Phase=="dependencies-ready"?_budget.Reserve(65536):null;
        if(phaseStorage is not null)
        {
            phaseStorage.RecheckBeforeWrite(phaseStorage.Bytes);ChangePhase(before.Capture,"commit-sealed",transaction);
        }
        else if(before.Capture.Phase!="commit-sealed") throw new CatalogGenerationCaptureConflictException();
        var after=ReadCurrentFreshHeaderInputsIn(generation,afterSigning,fenced,transaction,token);
        if(after.Capture!=(before.Capture with{Phase="commit-sealed"}) || after.Publication.Basis!=before.Publication.Basis ||
            after.Publication.Plan!=before.Publication.Plan || after.Intent.Id!=before.Intent.Id ||
            after.Intent.BodySha256!=before.Intent.BodySha256 || after.Intent.Sequence!=before.Intent.Sequence ||
            !after.Intent.Body.Span.SequenceEqual(before.Intent.Body.Span) || after.Intent.Envelope is not {} envelope ||
            after.Header is not {} header || header.Id!=pending.Value.Id || header.Scope!=pending.Value.Scope ||
            header.Role!=pending.Value.Role || header.MimeType!=pending.Value.MimeType || header.Sha256!=pending.Value.Sha256 ||
            header.FileId!=pending.Value.FileId || header.VerifiedVersion!=pending.Value.VerifiedVersion ||
            after.Inspection.HumanConfigurationRevision!=before.Inspection.HumanConfigurationRevision ||
            after.Inspection.HumanConfigurationSha256!=before.Inspection.HumanConfigurationSha256 ||
            !header.Payload.Span.SequenceEqual(pending.Value.Payload.Span) || !header.Payload.Span.SequenceEqual(envelope.Span))
            throw new CatalogGenerationCaptureConflictException();
        if(sealedWrite is null && (after.Intent.EnvelopeSha256!=before.Intent.EnvelopeSha256 ||
            !envelope.Span.SequenceEqual(before.Intent.Envelope!.Value.Span)))
            throw new ProtocolSigningConflictException();
        observations.AssertCurrentHeaderObservationAfterNativeMutationIn(inventory,signing,afterSigning,
            fenced,publications,transaction,pending.Value,token);
        token.ThrowIfCancellationRequested();sealedWrite?.Recheck();pending.Recheck();phaseStorage?.RecheckBeforeWrite(phaseStorage.Bytes);
        transaction.Commit();return header;
    }

    private sealed class CurrentFreshHeaderPendingContext(CatalogGenerationCaptureRepository owner,Guid generation,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,CancellationToken token) : ICurrentOriginalHeaderWriteContext
    {
        public (CatalogGenerationCapture Capture,ProtocolSigningIntent Intent) Resolve(ProcessorStateDatabase database,SqliteTransaction transaction)
        {
            if(!ReferenceEquals(database,owner._database)) throw new InvalidOperationException("Current header bookkeeping cannot cross state owners.");
            var actual=owner.ReadCurrentFreshHeaderInputsIn(generation,signing,fenced,transaction,token);
            if(actual.Intent.Envelope is null) throw new SigningHistoryRecoveryRequiredException();
            return(actual.Capture,actual.Intent);
        }
    }
}
