using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>One exact original-header receipt under a genuinely new P1 scan.
    /// This consumes that scan, returns only local progress and grants no activation
    /// or further network action. Ordinary obsolete-source publication stays closed.</summary>
    public WorkspacePublicationRecord ConfirmCurrentCatalogHeaderReceipt(CurrentCatalogHeaderCloudInspection expected,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,
        string account,string folder,string file,string mime,long version,ReadOnlyMemory<byte> bytes,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(expected);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();
        var generation=expected.Identity.OriginalScope.GenerationId;
        var actual=ReadCurrentHeaderCloudInputsIn(generation,expected.Inventory,signing,fenced,publications,transaction,token);
        var header=actual.Header;
        if(actual.Identity.ClaimSha256!=expected.Identity.ClaimSha256 || actual.Inventory!=expected.Inventory ||
            header.Id!=expected.Header.Id || header.Scope!=expected.Header.Scope || header.FileId!=expected.Header.FileId ||
            header.VerifiedVersion!=expected.Header.VerifiedVersion || header.Sha256!=expected.Header.Sha256 ||
            header.MimeType!=expected.Header.MimeType || header.Role!=expected.Header.Role ||
            !header.Payload.Span.SequenceEqual(expected.Header.Payload.Span)) throw new StaleLibraryConfigurationException();
        if(header.FileId is null || account!=header.Scope.AccountId || folder!=header.Scope.FolderId || file!=header.FileId ||
            mime!="application/json" || header.Role!="catalog-commit" || version<1 || bytes.Length!=header.Payload.Length ||
            header.VerifiedVersion is {} recorded && recorded!=version ||
            !header.Payload.Span.SequenceEqual(bytes.Span) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.Span),Convert.FromHexString(header.Sha256)))
            throw new WorkspacePublicationConflictException();
        token.ThrowIfCancellationRequested();
        if(header.VerifiedVersion is not null){transaction.Commit();return header;}
        using var storage=_budget.Reserve(65536);storage.RecheckBeforeWrite(storage.Bytes);
        using(var update=Command(transaction,"UPDATE workspace_publication SET verified_version=$version WHERE id=$id AND file_id=$file AND verified_version IS NULL",
            ("$version",version),("$id",header.Id.ToString("D")),("$file",file)))
            if(update.ExecuteNonQuery()!=1) throw new WorkspacePublicationConflictException();
        var journal=new WorkspacePublicationRepository(_database,_budget);
        var saved=journal.Read(header.Id,transaction) ?? throw new WorkspacePublicationConflictException();
        if(saved.Scope!=header.Scope || saved.Role!=header.Role || saved.MimeType!=header.MimeType || saved.FileId!=file ||
            saved.VerifiedVersion!=version || saved.Sha256!=header.Sha256 || !saved.Payload.Span.SequenceEqual(header.Payload.Span))
            throw new WorkspacePublicationConflictException();
        // A LOCAL predicted mutation comparison only. No refreshed network scan,
        // cursor, admission or receipt for a different object is manufactured.
        journal.AssertOnlyOriginalHeaderReceiptChangedIn(publications,header.Id,version,transaction);
        new WorkspaceObservationRepository(_database,_budget).AssertHeaderReceiptObservationHistoryUnchangedIn(
            expected.Inventory,signing,fenced,transaction,token);
        var after=ReadCurrentHeaderProviderInputsIn(generation,signing,fenced,transaction,token);
        var afterHeader=after.Header ?? throw new WorkspacePublicationConflictException();
        var plan=after.Publication.Plan ?? throw new CatalogGenerationCaptureConflictException();
        var normalized=afterHeader with{VerifiedVersion=header.VerifiedVersion};
        var contextHash=CurrentHeaderClaim(after.Capture,after.Intent,PlanHash(after.Capture,after.Publication.Basis,PublicationEncode(plan)),
            normalized,after.Inspection.SelectedAnchor,after.Inspection.HumanConfigurationRevision,
            after.Inspection.HumanConfigurationSha256,signing.Sha256,fenced);
        if(contextHash!=actual.Identity.ClaimSha256) throw new StaleLibraryConfigurationException();
        _=AssertCurrentHeaderPolicyInputsIn(signing,fenced,transaction,token);
        token.ThrowIfCancellationRequested();storage.RecheckBeforeWrite(storage.Bytes);transaction.Commit();return saved;
    }

    // Pure formatting shared with original inspection. Inputs are read/validated
    // from the owning transaction first; a caller hash never supplies authority.
    private static string CurrentHeaderClaim(CatalogGenerationCapture capture,ProtocolSigningIntent intent,string planSha,
        WorkspacePublicationRecord? header,CatalogObjectReference? selectedAnchor,long humanRevision,string humanSha,
        string signingSha,LocalCurrentControlAttempt fenced)=>
        Convert.ToHexStringLower(SHA256.HashData(new CanonicalJson(32768).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain="mpa-current-original-catalog-header-inspection-v1",capture.Scope,capture.SnapshotId,capture.CommitIntentId,capture.Phase,
            intent.Sequence,intent.BodySha256,intent.EnvelopeSha256,planSha,OriginalFileId=header?.FileId,VerifiedVersion=header?.VerifiedVersion,
            SelectedAnchor=selectedAnchor,HumanRevision=humanRevision,HumanSourcesSha256=humanSha,SigningSha256=signingSha,
            fenced.Id,fenced.Revision,fenced.AccountRevision,fenced.Version,fenced.ContentSha256
        },CurrentCompletionJson))));

    private LocalCurrentTransitionBasis AssertCurrentHeaderPolicyInputsIn(CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,SqliteTransaction transaction,CancellationToken token)
    {
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(fenced,transaction,token);
        if(signing.Roots.Any(root=>root.Disposition==CurrentRootSigningDisposition.Pending))
            throw new CurrentControlTransitionInProgressException();
        var authority=new LibraryAuthorityRepository(_database,_budget);
        var publisher=authority.CurrentPublisherPolicy(transaction);var sources=authority.CurrentSourceConfiguration(transaction);
        if(publisher is not null && (publisher.Sequence>basis.Head.Publishers.Sequence ||
                publisher.Sequence==basis.Head.Publishers.Sequence && publisher.EnvelopeSha256!=basis.Head.Publishers.Reference.Sha256) ||
            sources is not null && (sources.Sequence>basis.Head.Sources.Sequence ||
                sources.Sequence==basis.Head.Sources.Sequence && sources.EnvelopeSha256!=basis.Head.Sources.Reference.Sha256))
            throw new StaleLibraryConfigurationException();
        return basis;
    }
}

public sealed partial class WorkspacePublicationRepository
{
    // Internal post-write comparison, never an evolving network capability.
    internal void AssertOnlyOriginalHeaderReceiptChangedIn(WorkspacePublicationInventory before,Guid id,long version,SqliteTransaction transaction)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Header receipt belongs to another SQLite transaction.");
        if(InventoryDigest(before.Context,before.Maximum,before.Objects)!=before.Sha256) throw new WorkspacePublicationConflictException();
        var expected=before.Objects.Select(value=>value.Id==id?value with{VerifiedVersion=version}:value).ToArray();
        if(before.Objects.Count(value=>value.Id==id && value.Role=="catalog-commit" && value.MimeType=="application/json" &&
            value.FileId is not null && value.VerifiedVersion is null)!=1) throw new WorkspacePublicationConflictException();
        if(Inventory(before.Context,before.Maximum,transaction).Sha256!=InventoryDigest(before.Context,before.Maximum,expected))
            throw new WorkspacePublicationInventoryChangedException();
    }
}

public sealed partial class WorkspaceObservationRepository
{
    // Preserve every original observation/candidate/replay/native binding after
    // the one intentional local receipt mutation. No checkpoint is rewritten.
    internal void AssertHeaderReceiptObservationHistoryUnchangedIn(CurrentWorkspaceInventoryCheckpoint checkpoint,
        CurrentControlSigningInventory signing,LocalCurrentControlAttempt fenced,SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Header history belongs to another SQLite transaction.");
        if(ReadCurrentInventoryIn(transaction,token)!=checkpoint) throw new WorkspaceObservationConflictException();
        var attempt=ReadAttempt(transaction) ?? throw new WorkspaceObservationConflictException();
        var history=CaptureCurrentHistoryIn(signing,fenced,transaction,token);
        var root=history.Signing.Roots.Single(value=>value.Intent.EnvelopeSha256==fenced.ContentSha256 && value.Observed).Intent;
        if(history.Context!=attempt.Context || history.RetainedAttempt!=attempt || attempt.Completed is not null ||
            attempt.FirstUnresolvedCursor is null || attempt.InventoryStartCursor is null || attempt.HistoryReplay is not{Active:true,AtEnd:true} ||
            attempt.Id!=checkpoint.AttemptId || attempt.Revision!=checkpoint.AttemptRevision || checkpoint.Context!=attempt.Context ||
            checkpoint.RootAttemptId!=fenced.Id || checkpoint.RootIntentId!=root.Id || checkpoint.RootSequence!=root.Sequence ||
            checkpoint.ConnectionSha256!=fenced.ConnectionSha256 || checkpoint.RootSha256!=fenced.ContentSha256 || checkpoint.RootVersion!=fenced.Version ||
            checkpoint.SigningSha256!=history.Signing.Sha256 || checkpoint.Documents!=history.ImmutableObservations.Count ||
            checkpoint.InventorySha256!=InventoryHash(history.ImmutableObservations) || checkpoint.PreservedObservationSha256!=history.CompleteObservationSha256 ||
            checkpoint.PreservedCandidateSha256!=history.CompleteCandidateSha256 ||
            checkpoint.PreservedHistorySha256!=HistoryDigest(ReadHistory(attempt.Context,transaction).Values))
            throw new WorkspaceObservationConflictException();
        AssertSigningInventory(attempt,transaction);AssertHistoryReplay(attempt,transaction);token.ThrowIfCancellationRequested();
    }
}
