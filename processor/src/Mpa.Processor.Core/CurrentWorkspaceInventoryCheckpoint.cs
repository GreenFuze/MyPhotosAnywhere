using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Historical completion of an actual immutable inventory under one final
/// Root fence. Distinct from the legacy current-head receipt. It grants no catalog
/// activation, PublishedHead, generation/snapshot completion or original action.</summary>
public sealed record CurrentWorkspaceInventoryCheckpoint(WorkspaceObservationContext Context,Guid AttemptId,long AttemptRevision,
    string Cursor,Guid RootAttemptId,Guid RootIntentId,string ConnectionSha256,string RootSha256,long RootSequence,long RootVersion,
    string SigningSha256,string PublicationSha256,int Documents,string InventorySha256,string PreservedObservationSha256,
    string PreservedCandidateSha256,string PreservedHistorySha256)
{
    public string Format {get;init;}="mpa-current-workspace-inventory";
    public int Major {get;init;}=1;
}

public sealed partial class WorkspaceObservationRepository
{
    private const string CurrentInventoryKey="current_workspace_inventory_v1";
    private const int MaximumCurrentInventoryBytes=32768;
    private static readonly JsonSerializerOptions CurrentInventoryJson=new()
        {NumberHandling=JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString};

    /// <summary>Keep the complete legacy attempt, observations, candidates and failed
    /// history unchanged. A separate typed record commits only after explicit replay,
    /// complete immutable reads, all captured native/outbox work and the SAME actual
    /// final Root fence are rechecked in one transaction. The owner supplies actual
    /// network results; this repository does not manufacture a current-head proof.</summary>
    public CurrentWorkspaceInventoryCheckpoint CompleteCurrentInventory(WorkspaceObservationAttempt attempt,string cursor,
        IReadOnlyCollection<WorkspaceDocumentObservation> completeInventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(attempt);ArgumentNullException.ThrowIfNull(completeInventory);
        ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        if(!ValidAttemptCursor(cursor) || completeInventory.Count>MaximumDocuments) throw new ArgumentException("A complete bounded actual inventory/cursor is required.");
        using var transaction=_database.Connection.BeginTransaction();
        var history=RequireCurrentInventoryInputsIn(attempt,completeInventory,signing,fenced,publications,transaction,null,token);
        var root=history.Signing.Roots.Single(value=>value.Intent.EnvelopeSha256==fenced.ContentSha256 && value.Observed).Intent;
        var result=new CurrentWorkspaceInventoryCheckpoint(attempt.Context,attempt.Id,attempt.Revision,cursor,fenced.Id,root.Id,
            fenced.ConnectionSha256,fenced.ContentSha256,root.Sequence,fenced.Version!.Value,history.Signing.Sha256,publications.Sha256,
            history.ImmutableObservations.Count,InventoryHash(history.ImmutableObservations),history.CompleteObservationSha256,
            history.CompleteCandidateSha256,HistoryDigest(ReadHistory(attempt.Context,transaction).Values));
        var old=ReadCurrentInventoryIn(transaction,token);
        if(old is not null)
        {
            AssertSameWorkspace(old.Context,result.Context);
            if(old.Context.AccountRevision>result.Context.AccountRevision || old.AttemptRevision>result.AttemptRevision) throw new WorkspaceObservationConflictException();
            if(old.AttemptRevision==result.AttemptRevision)
            {
                if(old!=result) throw new WorkspaceObservationConflictException();
                token.ThrowIfCancellationRequested();transaction.Commit();return old;
            }
        }
        var encoded=EncodeCurrentInventory(result);using var reservation=_budget.Reserve(checked(Encoding.UTF8.GetByteCount(encoded)*3L+65536));
        reservation.RecheckBeforeWrite(reservation.Bytes);
        foreach(var pair in new[]{(Key:CurrentInventoryKey,Value:encoded),(Key:CurrentInventoryKey+":sha256",Value:Hash(Encoding.UTF8.GetBytes(encoded)))})
        {
            using var write=Command(transaction,"INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$key",pair.Key),("$value",pair.Value));
            if(write.ExecuteNonQuery()!=1) throw new InvalidOperationException("Current inventory completion lost its exact checkpoint owner.");
        }
        token.ThrowIfCancellationRequested();reservation.RecheckBeforeWrite(reservation.Bytes);transaction.Commit();return result;
    }

    /// <summary>A stored record is historical after restart or any new scan/Root
    /// nonce. This read independently verifies its retained native Root binding;
    /// it never supplies renewed access or an activation/completion capability.</summary>
    public CurrentWorkspaceInventoryCheckpoint? ReadCurrentInventory(CancellationToken token=default)
    {
        using var transaction=_database.Connection.BeginTransaction();var result=ReadCurrentInventoryIn(transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    public void AssertCurrentInventoryUnchanged(CurrentWorkspaceInventoryCheckpoint checkpoint,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction();
        AssertCurrentInventoryUnchangedIn(checkpoint,signing,fenced,publications,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();
    }

    internal void AssertCurrentInventoryUnchangedIn(CurrentWorkspaceInventoryCheckpoint checkpoint,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,SqliteTransaction transaction,CancellationToken token)=>
        AssertCurrentInventoryIn(checkpoint,signing,fenced,publications,transaction,null,token);

    // A local post-write condition, not a replacement observation or IO permission.
    internal void AssertCurrentInventoryAfterPreparedObjectIn(CurrentWorkspaceInventoryCheckpoint checkpoint,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,WorkspacePublicationRecord prepared,SqliteTransaction transaction,CancellationToken token)=>
        AssertCurrentInventoryIn(checkpoint,signing,fenced,publications,transaction,prepared,token);

    private void AssertCurrentInventoryIn(CurrentWorkspaceInventoryCheckpoint checkpoint,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,SqliteTransaction transaction,WorkspacePublicationRecord? prepared,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(fenced);ArgumentNullException.ThrowIfNull(publications);ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Inventory admission requires its owning live transaction.");
        if(ReadCurrentInventoryIn(transaction,token)!=checkpoint) throw new WorkspaceObservationConflictException();
        var attempt=ReadAttempt(transaction) ?? throw new WorkspaceObservationConflictException();
        var history=RequireCurrentInventoryInputsIn(attempt,null,signing,fenced,publications,transaction,prepared,token);
        var currentRoot=history.Signing.Roots.Single(value=>value.Intent.EnvelopeSha256==fenced.ContentSha256 && value.Observed).Intent;
        if(attempt.Id!=checkpoint.AttemptId || attempt.Revision!=checkpoint.AttemptRevision || checkpoint.Context!=attempt.Context ||
            checkpoint.RootAttemptId!=fenced.Id || checkpoint.RootIntentId!=currentRoot.Id || checkpoint.RootSequence!=currentRoot.Sequence ||
            checkpoint.ConnectionSha256!=fenced.ConnectionSha256 || checkpoint.RootSha256!=fenced.ContentSha256 || checkpoint.RootVersion!=fenced.Version ||
            checkpoint.SigningSha256!=history.Signing.Sha256 || checkpoint.PublicationSha256!=publications.Sha256 ||
            checkpoint.Documents!=history.ImmutableObservations.Count || checkpoint.InventorySha256!=InventoryHash(history.ImmutableObservations) ||
            checkpoint.PreservedObservationSha256!=history.CompleteObservationSha256 || checkpoint.PreservedCandidateSha256!=history.CompleteCandidateSha256 ||
            checkpoint.PreservedHistorySha256!=HistoryDigest(ReadHistory(attempt.Context,transaction).Values)) throw new WorkspaceObservationConflictException();
        token.ThrowIfCancellationRequested();
    }

    private CurrentWorkspaceObservationHistory RequireCurrentInventoryInputsIn(WorkspaceObservationAttempt attempt,
        IReadOnlyCollection<WorkspaceDocumentObservation>? completeInventory,CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt fenced,WorkspacePublicationInventory publications,SqliteTransaction transaction,WorkspacePublicationRecord? prepared,CancellationToken token)
    {
        if(fenced.Phase!="fenced") throw new CurrentControlObservationConflictException();
        var history=CaptureCurrentHistoryIn(signing,fenced,transaction,token);
        if(history.Context!=attempt.Context || history.RetainedAttempt!=attempt || attempt.Completed is not null ||
            attempt.FirstUnresolvedCursor is null || attempt.InventoryStartCursor is null ||
            attempt.HistoryReplay is not {Active:true,AtEnd:true} || publications.Context!=attempt.Context)
            throw new WorkspaceObservationConflictException();
        var expected=(completeInventory ?? history.ImmutableObservations).OrderBy(value=>value.FileId,StringComparer.Ordinal).ToArray();
        foreach(var value in expected) {token.ThrowIfCancellationRequested();Validate(value);}
        if(expected.Select(value=>value.FileId).Distinct(StringComparer.Ordinal).Count()!=expected.Length || !history.ImmutableObservations.SequenceEqual(expected))
            throw new WorkspaceObservationConflictException();
        var read=expected.ToDictionary(value=>value.FileId,StringComparer.Ordinal);
        foreach(var candidate in history.ImmutableCandidates)
            if(!read.TryGetValue(candidate.FileId,out var actual) || actual.Bytes!=candidate.Bytes || actual.Version<candidate.Version || actual.MimeType!=candidate.MimeType)
                throw new WorkspaceObservationConflictException();
        var outbox=new WorkspacePublicationRepository(_database,_budget);
        if(prepared is null) outbox.AssertInventoryUnchanged(publications,transaction);
        else outbox.AssertInventoryAfterPreparedObjectIn(publications,prepared,transaction);
        foreach(var work in publications.Objects)
        {
            token.ThrowIfCancellationRequested();
            if(work.FileId is not null && read.TryGetValue(work.FileId,out var actual))
            {
                if(actual.Bytes!=work.Bytes || actual.Sha256!=work.Sha256 || (actual.MimeType ?? "application/json")!=work.MimeType ||
                    work.VerifiedVersion is { } version && actual.Version<version) throw new WorkspaceObservationConflictException();
            }
            else if(work.VerifiedVersion is not null) throw new WorkspaceObservationConflictException();
        }
        var roots=history.Signing.Roots.Select(value=>value.Intent.Id).ToHashSet();
        var readEnvelopes=expected.Where(value=>value.MimeType is null).Select(value=>(value.Sha256,value.Bytes)).ToHashSet();
        var pendingEnvelopes=publications.Objects.Where(value=>value.MimeType=="application/json" && value.VerifiedVersion is null)
            .Select(value=>(value.Sha256,value.Bytes,value.Role)).ToHashSet();
        foreach(var intent in history.Signing.Signing.Intents)
        {
            token.ThrowIfCancellationRequested();if(roots.Contains(intent.Id) || intent.EnvelopeSha256 is null) continue;
            var role=intent.Kind switch {"library-publisher-authorization"=>"publisher-authorization","library-source-configuration"=>"source-configuration",
                "catalog-commit"=>"catalog-commit",_=>throw new ProtocolSigningConflictException()};
            var bytes=intent.EnvelopeBytes ?? throw new ProtocolSigningConflictException();
            if(!readEnvelopes.Contains((intent.EnvelopeSha256,bytes)) && !pendingEnvelopes.Contains((intent.EnvelopeSha256,bytes,role)))
                throw new SigningHistoryRecoveryRequiredException();
        }
        AssertSigningInventory(attempt,transaction);AssertHistoryReplay(attempt,transaction);return history;
    }

    private CurrentWorkspaceInventoryCheckpoint? ReadCurrentInventoryIn(SqliteTransaction transaction,CancellationToken token)
    {
        var text=AttemptMeta(CurrentInventoryKey,MaximumCurrentInventoryBytes,transaction);var digest=AttemptMeta(CurrentInventoryKey+":sha256",64,transaction);
        if(text is null && digest is null) return null;
        if(text is null || digest is null || Hash(Encoding.UTF8.GetBytes(text))!=digest) throw new InvalidDataException("Current inventory checkpoint lost its complete integrity record.");
        CurrentWorkspaceInventoryCheckpoint value;
        try {value=JsonSerializer.Deserialize<CurrentWorkspaceInventoryCheckpoint>(text,CurrentInventoryJson) ?? throw new InvalidDataException("Missing current inventory checkpoint.");}
        catch(JsonException error) {throw new InvalidDataException("Current inventory checkpoint cannot be decoded.",error);}
        if(EncodeCurrentInventory(value)!=text) throw new InvalidDataException("Current inventory checkpoint changed its typed scope or encoding.");
        AssertHistoricalCurrentInventoryIn(value,transaction,token);return value;
    }

    internal void AssertHistoricalCurrentInventoryIn(CurrentWorkspaceInventoryCheckpoint value,SqliteTransaction transaction,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(value);ArgumentNullException.ThrowIfNull(transaction);
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("Historical inventory requires its owning live transaction.");
        if(value.Context is null || value.Format!="mpa-current-workspace-inventory" || value.Major!=1 || value.AttemptId==Guid.Empty ||
            value.AttemptRevision<1 || !ValidAttemptCursor(value.Cursor) || value.RootAttemptId==Guid.Empty || value.RootIntentId==Guid.Empty ||
            value.RootSequence<1 || value.RootVersion<1 || value.Documents is <0 or >MaximumDocuments ||
            new[]{value.ConnectionSha256,value.RootSha256,value.SigningSha256,value.PublicationSha256,value.InventorySha256,
                value.PreservedObservationSha256,value.PreservedCandidateSha256,value.PreservedHistorySha256}.Any(hash=>!Digest(hash)))
            throw new InvalidDataException("Current inventory checkpoint changed its typed scope or encoding.");
        try {ValidateContext(value.Context);}
        catch(ArgumentException error) {throw new InvalidDataException("Current inventory checkpoint changed its retained workspace context.",error);}
        var basis=new LocalCurrentControlObservations(_database,_budget).TransitionBasisIn(null,transaction,token);
        var intent=new ProtocolSigningJournal(_database,_budget).Read(value.RootIntentId,transaction) ?? throw new InvalidDataException("Current inventory lost its retained native Root.");
        if(!basis.ObservedRoots.Contains(value.RootIntentId) || intent.Kind!=LibraryCurrentControl.Kind || intent.Envelope is null ||
            intent.Sequence!=value.RootSequence || intent.EnvelopeSha256!=value.RootSha256 || basis.Pin.DocumentSha256!=value.ConnectionSha256 ||
            basis.Pin.LibraryId!=value.Context.LibraryId || basis.Pin.Workspace.AccountId!=value.Context.AccountId || basis.Pin.Workspace.FolderId!=value.Context.FolderId ||
            basis.SharedDriveId!=value.Context.SharedDriveId || value.RootVersion<basis.MinimumVersion || value.RootVersion>basis.MaximumObservedVersion ||
            value.Context.AccountRevision<basis.MinimumAccountRevision || value.Context.AccountRevision>basis.ObservedAccountRevision)
            throw new InvalidDataException("Current inventory lost its exact observed native Root/workspace binding.");
        token.ThrowIfCancellationRequested();
    }

    private static string EncodeCurrentInventory(CurrentWorkspaceInventoryCheckpoint value)=>Encoding.UTF8.GetString(
        new CanonicalJson(MaximumCurrentInventoryBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(value,CurrentInventoryJson)));
}
