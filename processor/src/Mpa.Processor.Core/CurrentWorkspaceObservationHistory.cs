using System.Collections.ObjectModel;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>A read-only classification of retained history, not an inventory scan,
/// recovered failure, completion or publication grant. The exact mutable current ID
/// is separated without deleting or overwriting any original observation/candidate.</summary>
public sealed class CurrentWorkspaceObservationHistory
{
    public WorkspaceObservationContext Context { get; }
    public CurrentControlSigningInventory Signing { get; }
    public WorkspaceObservationAttempt? RetainedAttempt { get; }
    public WorkspaceDocumentObservation? RootObservation { get; }
    public WorkspaceCandidateObservation? RootCandidate { get; }
    public ReadOnlyCollection<WorkspaceDocumentObservation> ImmutableObservations { get; }
    public ReadOnlyCollection<WorkspaceCandidateObservation> ImmutableCandidates { get; }
    public string CompleteObservationSha256 { get; }
    public string CompleteCandidateSha256 { get; }

    internal CurrentWorkspaceObservationHistory(WorkspaceObservationContext context, CurrentControlSigningInventory signing,
        WorkspaceObservationAttempt? attempt, WorkspaceDocumentObservation? root, WorkspaceCandidateObservation? candidate,
        ReadOnlyCollection<WorkspaceDocumentObservation> immutable, ReadOnlyCollection<WorkspaceCandidateObservation> immutableCandidates,
        string observationSha256, string candidateSha256)
    {
        Context=context;Signing=signing;RetainedAttempt=attempt;RootObservation=root;RootCandidate=candidate;
        ImmutableObservations=immutable;ImmutableCandidates=immutableCandidates;
        CompleteObservationSha256=observationSha256;CompleteCandidateSha256=candidateSha256;
    }
}

public sealed partial class WorkspaceObservationRepository
{
    /// <summary>Require the exact current native signing/Root witness inside this
    /// transaction. Only its independently pinned actual current ID is mutable.
    /// Previously read Root bytes must match an original retained native envelope;
    /// candidate/read versions cannot exceed the actual current observation.
    /// Every other known/unread identity and the complete failed scan/history remain.
    /// This method writes nothing and does not acknowledge or repair any failure.</summary>
    public CurrentWorkspaceObservationHistory CaptureCurrentHistory(CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt expected, CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(signing);ArgumentNullException.ThrowIfNull(expected);
        using var transaction=_database.Connection.BeginTransaction();
        var result=CaptureCurrentHistoryIn(signing,expected,transaction,token);
        token.ThrowIfCancellationRequested();transaction.Commit();return result;
    }

    internal CurrentWorkspaceObservationHistory CaptureCurrentHistoryIn(CurrentControlSigningInventory signing,
        LocalCurrentControlAttempt expected,Microsoft.Data.Sqlite.SqliteTransaction transaction,CancellationToken token)
    {
        if(!ReferenceEquals(transaction.Connection,_database.Connection)) throw new InvalidOperationException("History classification requires its owning live transaction.");
        var actual=new CurrentControlTransitionJournal(_database,_budget).RequireSigningInventoryIn(signing,expected,transaction,token);
        var pin=LibraryConnectionDescriptor.Decode(actual.ConnectionCode);
        var rootId=pin.CurrentFileId ?? throw new InvalidDataException("Current history classification requires the independently pinned mutable file.");
        var version=expected.Version ?? throw new CurrentControlObservationConflictException();
        var context=new WorkspaceObservationContext(pin.LibraryId,pin.Workspace.AccountId,pin.Workspace.FolderId,expected.AccountRevision,expected.SharedDriveId);
        AssertContext(context,transaction);
        var retained=ReadAttempt(transaction);var known=ReadAll(context,transaction);var candidates=ReadCandidates(context,transaction);
        var checkpoint=ReadCheckpoint(transaction);
        foreach(var previous in new[]{retained?.Context,checkpoint?.Context})
            if(previous is not null)
            {
                AssertSameWorkspace(previous,context);
                if(previous.AccountRevision>context.AccountRevision) throw new WorkspaceObservationConflictException();
            }
        if(known.Select(value=>value.FileId).Union(candidates.Keys,StringComparer.Ordinal).Count()>MaximumDocuments)
            throw new InvalidDataException("Complete retained workspace history exceeds its unchanged identity bound.");
        var root=known.SingleOrDefault(value=>value.FileId==rootId);candidates.TryGetValue(rootId,out var candidate);
        if(root is not null && (root.Version>version || root.MimeType is not (null or "application/json") ||
            !actual.Roots.Any(value=>value.Intent.EnvelopeSha256==root.Sha256 && value.Intent.EnvelopeBytes==root.Bytes)))
            throw new WorkspaceObservationConflictException();
        if(candidate is not null && (candidate.Version>version || candidate.MimeType is not (null or "application/json") ||
            candidate.Bytes>ProtocolDocumentReader.MaximumDocumentBytes)) throw new WorkspaceObservationConflictException();
        var immutable=known.Where(value=>value.FileId!=rootId).ToList().AsReadOnly();
        var immutableCandidates=candidates.Values.Where(value=>value.FileId!=rootId).OrderBy(value=>value.FileId,StringComparer.Ordinal).ToList().AsReadOnly();
        token.ThrowIfCancellationRequested();
        return new(context,actual,retained,root,candidate,immutable,immutableCandidates,InventoryHash(known),CandidateInventoryHash(candidates.Values));
    }
}
