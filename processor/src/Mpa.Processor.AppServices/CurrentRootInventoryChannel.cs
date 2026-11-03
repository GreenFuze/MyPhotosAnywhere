using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

internal interface ICurrentRootInterruptedPageObserver
{
    Task InterruptedAsync(WorkspaceHistoryPageEvidence page,CancellationToken token);
}

/// <summary>Exact independently pinned mutable identity, borrowed only by a joined
/// current-reader operation. Raw page/identity limits are checked before separation.
/// The final actual conditional Root read still follows every immutable network read.</summary>
internal sealed class CurrentRootInventoryChannel(string fileId,string folderId,LocalCurrentControlAttempt capture,
    Func<long?,CancellationToken,Task> reject)
{
    internal string FileId {get;}=fileId;

    private async Task RejectAsync(long? version,Func<Task>? preserve,CancellationToken token)
    {
        await reject(version,token).ConfigureAwait(false);
        if(preserve is not null) await preserve().ConfigureAwait(false);
        throw new CurrentControlObservationConflictException();
    }

    internal IGoogleWorkspaceDiscovery Documents(IGoogleWorkspaceDiscovery source,IWorkspaceInventoryObserver? observer)=>new ImmutableDocuments(this,source,observer);
    internal IGoogleWorkspaceChanges Changes(IGoogleWorkspaceChanges source,IWorkspaceInventoryObserver? observer,IWorkspaceHistoryObserver? history)=>
        new ImmutableChanges(this,source,observer,history);

    private sealed class ImmutableDocuments(CurrentRootInventoryChannel owner,IGoogleWorkspaceDiscovery source,IWorkspaceInventoryObserver? observer) : IGoogleWorkspaceDiscovery
    {
        private readonly HashSet<string> _rawIds=new(StringComparer.Ordinal);
        public async Task<DriveWorkspaceDocumentPage> ReadWorkspaceDocumentsAsync(string folder,string? sharedDrive,string? pageToken,CancellationToken token)
        {
            var page=await source.ReadWorkspaceDocumentsAsync(folder,sharedDrive,pageToken,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if(page.Documents.Count>100) throw new InvalidDataException("The raw current-workspace inventory exceeded its page bound.");
            foreach(var candidate in page.Documents)
            {
                if(!_rawIds.Add(candidate.FileId) || _rawIds.Count>WorkspaceDocumentInventoryReader.MaximumDocuments) throw new WorkspaceInventoryIncompleteException();
                if(candidate.FileId==owner.FileId && (candidate.Version!=owner.Capture.Version || candidate.Bytes!=owner.Capture.DeclaredBytes || candidate.MimeType!="application/json"))
                    await owner.RejectAsync(candidate.Version>=1 ? candidate.Version : null,
                        observer is null ? null : ()=>observer.PageAsync(page.Documents.Where(value=>value.FileId!=owner.FileId).ToArray(),token),token).ConfigureAwait(false);
            }
            if(page.NextPageToken is null && !_rawIds.Contains(owner.FileId))
            {
                if(observer is not null) await observer.PageAsync(page.Documents,token).ConfigureAwait(false);
                throw new WorkspaceInventoryIncompleteException();
            }
            return new(page.Documents.Where(candidate=>candidate.FileId!=owner.FileId).ToList().AsReadOnly(),page.NextPageToken,page.IncompleteSearch);
        }
        public Task<DriveWorkspaceObject> ReadWorkspaceDocumentAsync(string folder,DriveWorkspaceDocumentCandidate candidate,CancellationToken token)
        {
            if(candidate.FileId==owner.FileId) throw new CurrentControlObservationConflictException();
            return source.ReadWorkspaceDocumentAsync(folder,candidate,token);
        }
    }

    private LocalCurrentControlAttempt Capture {get;}=capture;
    private string FolderId {get;}=folderId;
    private sealed class ImmutableChanges(CurrentRootInventoryChannel owner,IGoogleWorkspaceChanges source,IWorkspaceInventoryObserver? observer,
        IWorkspaceHistoryObserver? history) : IGoogleWorkspaceChanges
    {
        public Task<DriveChangeCursor> GetChangeCursorAsync(string? sharedDrive,CancellationToken token)=>source.GetChangeCursorAsync(sharedDrive,token);
        public async Task<DriveChangePage> ReadChangesAsync(DriveChangeCursor cursor,CancellationToken token)
        {
            var page=await source.ReadChangesAsync(cursor,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if(page.Changes.Count>100 || (page.Next is null)==(page.AtEnd is null)) throw new InvalidDataException("The raw current-workspace change page lost its bounded continuation.");
            foreach(var change in page.Changes)
            {
                WorkspaceHistoryReplayReader.ValidateChange(change);
                if(change.Kind!="file" || change.Id!=owner.FileId) continue;
                if(change.Removed || change.File is not {} file || file.Trashed || file.CanDownload==false ||
                    file.MimeType!="application/json" || !file.Parents.Contains(owner.FolderId,StringComparer.Ordinal) ||
                    file.Bytes is null or <1 || file.Bytes>GoogleAccountConnection.MaximumWorkspaceDocumentBytes || file.Version>owner.Capture.Version ||
                    file.Version==owner.Capture.Version && file.Bytes!=owner.Capture.DeclaredBytes)
                    await owner.RejectAsync(change.File?.Version,()=>PreserveOtherChangesAsync(page,cursor,token),token).ConfigureAwait(false);
            }
            return new(page.Changes.Where(change=>change.Kind!="file" || change.Id!=owner.FileId).ToList().AsReadOnly(),page.Next,page.AtEnd);
        }

        private async Task PreserveOtherChangesAsync(DriveChangePage raw,DriveChangeCursor cursor,CancellationToken token)
        {
            var other=new DriveChangePage(raw.Changes.Where(change=>change.Kind!="file" || change.Id!=owner.FileId).ToList().AsReadOnly(),raw.Next,raw.AtEnd);
            // An incomplete raw page is evidence only. No present-file claim or
            // inventory completion is inferred from this scope-only projection.
            var scope=new WorkspaceDocumentInventory(cursor.AccountId,owner.FolderId,cursor.SharedDriveId,new([]),new([]),cursor);
            var evidence=WorkspaceHistoryReplayReader.Evidence(scope,new(StringComparer.Ordinal),new(StringComparer.Ordinal),cursor,raw.Next ?? raw.AtEnd!,other,observeFolderChanges:true);
            if(evidence.Events.Count==0) return;
            if(observer is ICurrentRootInterruptedPageObserver interrupted) await interrupted.InterruptedAsync(evidence,token).ConfigureAwait(false);
            else if(history is ICurrentRootInterruptedPageObserver interruptedHistory) await interruptedHistory.InterruptedAsync(evidence,token).ConfigureAwait(false);
            else throw new InvalidOperationException("A rejected mutable Root page requires its joined durable history owner.");
        }
    }
}
