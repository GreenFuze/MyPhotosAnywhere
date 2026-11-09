using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

/// <summary>Bounded hashes of completed actual reads belonging to one live current
/// reader. No body, persisted checkpoint, source grant or caller capability. The
/// internal session scan still lists, checks access/history/changes and fences Root.</summary>
internal sealed class CurrentWorkspaceReadCache
{
    private readonly WorkspaceObservationContext _context;
    private readonly Dictionary<string,WorkspaceDocumentObservation> _documents;
    internal CurrentWorkspaceReadCache(WorkspaceObservationContext context,IReadOnlyCollection<WorkspaceDocumentObservation> completed)
    {
        ArgumentNullException.ThrowIfNull(context);ArgumentNullException.ThrowIfNull(completed);
        if(completed.Count>WorkspaceObservationRepository.MaximumDocuments) throw new InvalidDataException("Completed current read cache exceeded its observation bound.");
        _context=context;_documents=new(StringComparer.Ordinal);
        foreach(var document in completed)
            if(!_documents.TryAdd(document.FileId,document)) throw new InvalidDataException("Completed current read cache duplicated an observation.");
    }
    internal WorkspaceDocumentObservation? Find(WorkspaceObservationContext context,DriveWorkspaceDocumentCandidate candidate)
    {
        if(context!=_context || !_documents.TryGetValue(candidate.FileId,out var observation) || observation.Version!=candidate.Version ||
            observation.Bytes!=candidate.Bytes || (observation.MimeType??"application/json")!=candidate.MimeType) return null;
        return observation;
    }
}
