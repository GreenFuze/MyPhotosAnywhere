using System.Globalization;
using Mpa.Processor.AppServices;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Web;

public sealed record DesktopLibraryCatalogPage(bool Available,Guid? StageId,long? RootSequence,IReadOnlyList<DesktopCatalogPhoto> Photos,string? OlderCursor,string? NewerCursor);
public interface IDesktopLibraryCatalogApplication
{
    Task<DesktopLibraryCatalogPage> LibraryCatalogAsync(string? accountId,string? position,bool newer,long? dateUtc,CancellationToken cancellationToken);
}
public sealed class DesktopLibraryCatalogUnavailableException() : IOException("Shared library photos are not initialized on this host.");

/// <summary>Borrow the actual engine's historical query owner. Every continuation
/// binds its canonical stage and account. No discovery revision, provider request,
/// refresh, publication permission or ownership transfer is invented here.</summary>
public sealed class DesktopLibraryCatalogReader(ICurrentCanonicalCatalogQueries catalog)
{
    private readonly ICurrentCanonicalCatalogQueries _catalog=catalog ?? throw new ArgumentNullException(nameof(catalog));
    public async Task<DesktopLibraryCatalogPage> ReadAsync(string? accountId,string? position,bool newer,long? dateUtc,CancellationToken caller)
    {
        if(position is not null && dateUtc is not null || newer && position is null || dateUtc is < -62135596800000L or >253402300799999L)
            throw new InvalidDesktopCatalogQueryException();
        var account=Account(accountId);var cursor=position is null?null:Decode(position,accountId);caller.ThrowIfCancellationRequested();
        CurrentCanonicalReadPage? page;
        if(dateUtc is {} date) page=account is null?await _catalog.ReadAtOrBeforeAsync(null,date,60,caller).ConfigureAwait(false):
            await _catalog.ReadAccountAtOrBeforeAsync(null,account,date,60,caller).ConfigureAwait(false);
        else if(newer && cursor is not null) page=account is null?await _catalog.ReadPreviousPageAsync(cursor.Stage,cursor.Key,60,caller).ConfigureAwait(false):
            await _catalog.ReadAccountPreviousPageAsync(cursor.Stage,account,cursor.Key,60,caller).ConfigureAwait(false);
        else page=await PageAsync(cursor?.Stage,account,cursor?.Key,60,caller).ConfigureAwait(false);
        caller.ThrowIfCancellationRequested();
        if(page is null)
        {
            if(cursor is not null) throw new CurrentCanonicalPositionChangedException();
            return new(false,null,null,[],null,null);
        }
        Validate(page,account,60,cursor?.Stage);var selected=page.Selection;
        if(page.Page.Assets.Count==0) return new(true,selected.StageId,selected.RootSequence,[],null,null);
        var first=page.Page.Assets[0];var last=page.Page.Assets[^1];
        var older=await PageAsync(selected.StageId,account,Key(last),1,caller).ConfigureAwait(false);
        ValidateProbe(older,selected,account);
        var earlier=account is null?await _catalog.ReadPreviousPageAsync(selected.StageId,Key(first),1,caller).ConfigureAwait(false):
            await _catalog.ReadAccountPreviousPageAsync(selected.StageId,account,Key(first),1,caller).ConfigureAwait(false);
        ValidateProbe(earlier,selected,account);caller.ThrowIfCancellationRequested();
        return new(true,selected.StageId,selected.RootSequence,page.Page.Assets.Select(Photo).ToArray(),
            older is {Page.Assets.Count:>0}?Encode(selected.StageId,accountId,Key(last)):null,
            earlier is {Page.Assets.Count:>0}?Encode(selected.StageId,accountId,Key(first)):null);
    }

    public async Task<CurrentCanonicalReadAsset> ResolveAsync(Guid stage,Guid id,string? accountId,CancellationToken caller)
    {
        if(stage==Guid.Empty || id==Guid.Empty) throw new InvalidDesktopCatalogQueryException();
        var account=Account(accountId);caller.ThrowIfCancellationRequested();
        var result=account is null?await _catalog.FindAssetAsync(stage,id,caller).ConfigureAwait(false):
            await _catalog.FindAccountAssetAsync(stage,account,id,caller).ConfigureAwait(false);
        caller.ThrowIfCancellationRequested();
        if(result is null || result.Selection.StageId!=stage) throw new CurrentCanonicalPositionChangedException();
        if(result.Asset is {} asset && (asset.Id!=id || account is not null && asset.AccountId!=account.AccountId))
            throw new InvalidDataException("The canonical query returned another photo or account.");
        return result;
    }
    private Task<CurrentCanonicalReadPage?> PageAsync(Guid? stage,CurrentCatalogAccountFilter? account,CatalogCursor? after,int limit,CancellationToken caller)=>
        account is null?_catalog.ReadPageAsync(stage,after,limit,caller):_catalog.ReadAccountPageAsync(stage,account,after,limit,caller);
    private static void ValidateProbe(CurrentCanonicalReadPage? page,CurrentCatalogActivation selected,CurrentCatalogAccountFilter? account)
    {
        if(page is null || page.Selection!=selected) throw new CurrentCanonicalPositionChangedException();
        Validate(page,account,1,selected.StageId);
    }
    private static void Validate(CurrentCanonicalReadPage page,CurrentCatalogAccountFilter? account,int limit,Guid? expected)
    {
        if(expected is not null && page.Selection.StageId!=expected) throw new CurrentCanonicalPositionChangedException();
        if(page.Selection.StageId==Guid.Empty || page.Selection.RootSequence<1 || page.Page.Assets.Count>limit || page.Page.Assets.Count==0 && page.Page.HasMore ||
            page.Page.Assets.Any(a=>a.Id==Guid.Empty || account is not null && a.AccountId!=account.AccountId) ||
            page.Page.Assets.Select(a=>a.Id).Distinct().Count()!=page.Page.Assets.Count)
            throw new InvalidDataException("The canonical query returned an invalid historical page.");
    }
    private static CurrentCatalogAccountFilter? Account(string? value)
    {
        if(value is null) return null;
        try {return new(value);}catch(ArgumentException) {throw new InvalidDesktopCatalogQueryException();}
    }
    private sealed record Position(Guid Stage,CatalogCursor Key);
    private static CatalogCursor Key(DriveCatalogAsset asset)=>new(asset.EffectiveUtc,asset.Id);
    private static DesktopCatalogPhoto Photo(DriveCatalogAsset asset)=>new(asset.Id,asset.AccountId,asset.Name,asset.MimeType,asset.Width,asset.Height,asset.EffectiveUtc,
        (asset.CapturedUtc is not null?CatalogDateProvenance.DriveCapture:asset.ModifiedUtc is not null?CatalogDateProvenance.DriveModification:CatalogDateProvenance.Unknown).ToString(),asset.RawCapture);
    private static string Encode(Guid stage,string? account,CatalogCursor key)=>string.Join('|',"c1",stage.ToString("D"),account ?? "*",
        key.EffectiveUtc?.ToString(CultureInfo.InvariantCulture) ?? "unknown",key.Id.ToString("D"));
    private static Position Decode(string value,string? account)
    {
        if(value.Length>400) throw new InvalidDesktopCatalogQueryException();var fields=value.Split('|');
        if(fields.Length!=5 || fields[0]!="c1" || fields[2]!=(account ?? "*") ||
            !Guid.TryParseExact(fields[1],"D",out var stage) || stage==Guid.Empty || stage.ToString("D")!=fields[1] ||
            !Guid.TryParseExact(fields[4],"D",out var id) || id==Guid.Empty || id.ToString("D")!=fields[4]) throw new InvalidDesktopCatalogQueryException();
        long? date=null;
        if(fields[3]!="unknown")
        {
            if(!long.TryParse(fields[3],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var parsed) || parsed is < -62135596800000L or >253402300799999L || parsed.ToString(CultureInfo.InvariantCulture)!=fields[3])
                throw new InvalidDesktopCatalogQueryException();
            date=parsed;
        }
        return new(stage,new(date,id));
    }
}
