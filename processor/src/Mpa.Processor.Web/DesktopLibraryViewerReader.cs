using Mpa.Processor.AppServices;
using Mpa.Processor.Core;

namespace Mpa.Processor.Web;

/// <summary>Resolve only the frozen positions and identities in their captured
/// canonical stage/account. Never resolve a canonical ID through discovery or
/// replace a missing entry with a nearby photo.</summary>
public sealed class DesktopLibraryViewerReader(ICurrentCanonicalCatalogQueries catalog)
{
    private readonly DesktopLibraryCatalogReader _catalog=new(catalog);
    public async Task<DesktopViewerWindow> ReadAsync(CatalogViewerWindow window,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(window);
        if(window.StageId is not {} stage || stage==Guid.Empty || window.Id==Guid.Empty || window.Count<1 || window.Position<0 || window.Position>=window.Count ||
            window.Entries.Count is <1 or >90 || !window.Entries.Any(e=>e.Position==window.Position))
            throw new InvalidDataException("The canonical viewer lost its captured context or position.");
        long previous=-1;var identities=new HashSet<Guid>();
        foreach(var entry in window.Entries)
        {
            if(entry.Position<0 || entry.Position>=window.Count || previous!=-1 && entry.Position!=previous+1 || entry.AssetId==Guid.Empty || !identities.Add(entry.AssetId))
                throw new InvalidDataException("The canonical viewer returned a conflicting frozen identity sequence.");
            previous=entry.Position;
        }
        caller.ThrowIfCancellationRequested();var entries=new List<DesktopViewerEntry>(window.Entries.Count);CurrentCatalogActivation? selection=null;
        foreach(var entry in window.Entries)
        {
            var exact=await _catalog.ResolveAsync(stage,entry.AssetId,window.AccountId,caller).ConfigureAwait(false);
            if(selection is not null && selection!=exact.Selection) throw new CurrentCanonicalPositionChangedException();
            selection=exact.Selection;
            entries.Add(new(entry.Position,entry.AssetId,exact.Asset is {} asset?DesktopLibraryCatalogReader.Photo(asset):null));
        }
        caller.ThrowIfCancellationRequested();return new(window.Id,window.Count,window.Position,entries.ToArray(),stage,window.AccountId);
    }
}
