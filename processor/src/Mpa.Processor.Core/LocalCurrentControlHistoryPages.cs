using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Private observation-history encoding. Root bodies remain in the signing
/// journal. Each page respects the unchanged canonical codec collection limit; the
/// independent total-history bound is never a reason to enlarge a wire-format cap.</summary>
internal sealed class LocalCurrentControlHistoryPages
{
    internal const int MaximumHeads=4096;
    private readonly Guid[] _heads;
    internal Guid[] Heads=>_heads.ToArray();
    internal Guid[][] Pages=>_heads.Chunk(CanonicalJson.MaximumCollectionItems).ToArray();

    internal LocalCurrentControlHistoryPages(Guid[][] pages)
    {
        if(pages is null || pages.Length is <1 || pages.Length>(MaximumHeads+CanonicalJson.MaximumCollectionItems-1)/CanonicalJson.MaximumCollectionItems)
            throw Invalid();
        var count=0;
        for(var index=0;index<pages.Length;index++)
        {
            var page=pages[index];
            if(page is null || page.Length is <1 || page.Length>CanonicalJson.MaximumCollectionItems ||
                index<pages.Length-1 && page.Length!=CanonicalJson.MaximumCollectionItems) throw Invalid();
            count=checked(count+page.Length);
        }
        if(count>MaximumHeads) throw Invalid();
        _heads=new Guid[count];var offset=0;var seen=new HashSet<Guid>();
        foreach(var page in pages)
        foreach(var id in page)
        {
            if(id==Guid.Empty || !seen.Add(id)) throw Invalid();
            _heads[offset++]=id;
        }
    }

    internal static LocalCurrentControlHistoryPages FromHeads(Guid[] heads)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if(heads.Length is <1 or >MaximumHeads) throw Invalid();
        return new(heads.Chunk(CanonicalJson.MaximumCollectionItems).ToArray());
    }
    private static InvalidDataException Invalid()=>new("Observed current history lost its bounded distinct canonical pages.");
}
