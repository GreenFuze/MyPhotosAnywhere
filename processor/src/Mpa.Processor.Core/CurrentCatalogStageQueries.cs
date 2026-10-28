using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CurrentCatalogStageIndex
{
    /// <summary>Nearest entries strictly before the cursor, returned in normal
    /// gallery order. HasMore describes the earlier/newer direction. The next
    /// previous-page cursor is the FIRST returned entry, even for unknown dates.</summary>
    public CurrentCatalogStagePage ReadPreviousPage(CatalogCursor before,int limit=60,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(before);
        return ReadOrderedPage(new(StageReadKind.Before,Cursor:before),limit,token);
    }

    /// <summary>Seek directly through the date index to the first known timestamp
    /// at or before inclusive UTC milliseconds, followed by older/unknown entries.
    /// Device-local day boundaries belong to the caller, not the stored index.</summary>
    public CurrentCatalogStagePage ReadAtOrBefore(long utcMilliseconds,int limit=60,CancellationToken token=default)=>
        ReadOrderedPage(new(StageReadKind.Date,Date:utcMilliseconds),limit,token);

    /// <summary>Resolve only this identity. Missing content returns null; no nearby
    /// photo is substituted and no whole-library enumeration is performed.</summary>
    public DriveCatalogAsset? FindAsset(Guid assetId,CancellationToken token=default)
    {
        var page=ReadOrderedPage(new(StageReadKind.Identity,Id:assetId),1,token);
        if(page.HasMore) throw new InvalidDataException("A canonical identity resolved to more than one row.");
        return page.Assets.SingleOrDefault();
    }

    private enum StageReadKind { First,After,Before,Date,Identity }
    private sealed record StageReadPosition(StageReadKind Kind,CatalogCursor? Cursor=null,long? Date=null,Guid? Id=null);

    private CurrentCatalogStagePage ReadOrderedPage(StageReadPosition position,int limit,CancellationToken token)
    {
        RequireOpen();if(_sealed is null) throw new InvalidOperationException("Seal and verify private staging before reading its gallery projection.");
        if(limit is <1 or >90 || position.Cursor is {} cursor && (cursor.Id==Guid.Empty || !ValidStageDate(cursor.EffectiveUtc)) ||
            position.Kind is StageReadKind.After or StageReadKind.Before && position.Cursor is null ||
            position.Kind==StageReadKind.Date && (position.Date is null || !ValidStageDate(position.Date)) ||
            position.Kind==StageReadKind.Identity && (position.Id is null || position.Id==Guid.Empty))
            throw new ArgumentException("A bounded valid catalog position is required.");
        token.ThrowIfCancellationRequested();using var query=_connection!.CreateCommand();
        var predicate=position.Kind switch
        {
            StageReadKind.First=>"",
            StageReadKind.After=>" WHERE (date_unknown,date_sort,id)>($unknown,$sort,$id)",
            StageReadKind.Before=>" WHERE (date_unknown,date_sort,id)<($unknown,$sort,$id)",
            StageReadKind.Date=>" WHERE (date_unknown,date_sort)>=(0,$sort)",
            StageReadKind.Identity=>" WHERE id=$id",
            _=>throw new InvalidOperationException("Unknown internal canonical query kind.")
        };
        var reverse=position.Kind==StageReadKind.Before;
        query.CommandText="SELECT id,prefix,length(payload),payload,sha256,date_unknown,date_sort FROM fact"+predicate+
            (reverse?" ORDER BY date_unknown DESC,date_sort DESC,id DESC":" ORDER BY date_unknown,date_sort,id")+" LIMIT $limit";
        query.Parameters.AddWithValue("$limit",limit+1);
        if(position.Cursor is {} key)
        {
            query.Parameters.AddWithValue("$unknown",key.EffectiveUtc is null);
            query.Parameters.AddWithValue("$sort",key.EffectiveUtc is null?0L:-key.EffectiveUtc.Value);
            query.Parameters.AddWithValue("$id",key.Id.ToString("D"));
        }
        if(position.Kind==StageReadKind.Date) query.Parameters.AddWithValue("$sort",-position.Date!.Value);
        if(position.Kind==StageReadKind.Identity) query.Parameters.AddWithValue("$id",position.Id!.Value.ToString("D"));
        var assets=new List<DriveCatalogAsset>();var more=false;using var rows=query.ExecuteReader();
        while(rows.Read())
        {
            token.ThrowIfCancellationRequested();if(assets.Count==limit){more=true;break;}
            assets.Add(ReadGalleryRow(rows));
        }
        if(reverse) assets.Reverse();
        return new(assets.AsReadOnly(),more);
    }

    private DriveCatalogAsset ReadGalleryRow(SqliteDataReader row)
    {
        if(row.GetInt64(2) is <1 or >CatalogChunkDecoder.MaximumExpandedBytes) throw new InvalidDataException("A staged row exceeds its byte bound.");
        var bytes=row.GetFieldValue<byte[]>(3);var fact=_codec.Read(bytes,row.GetString(1));
        if(Hash(bytes)!=row.GetString(4) || fact.Assets.Count!=1 || fact.Assets[0].Id.ToString("D")!=row.GetString(0) ||
            row.GetBoolean(5)!=(fact.Assets[0].EffectiveUtc is null) ||
            row.GetInt64(6)!=(fact.Assets[0].EffectiveUtc is null?0L:-fact.Assets[0].EffectiveUtc!.Value))
            throw new InvalidDataException("Staged gallery row failed identity, digest or date validation.");
        return fact.Assets[0];
    }
    private static bool ValidStageDate(long? value)=>value is null or >= -62135596800000L and <=253402300799999L;
}
