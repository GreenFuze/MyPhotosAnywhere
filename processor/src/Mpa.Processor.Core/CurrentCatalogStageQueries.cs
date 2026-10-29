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
    public DriveCatalogAsset? FindAsset(Guid assetId,CancellationToken token=default)=>
        FindOrderedAsset(new(StageReadKind.Identity,Id:assetId),token);

    public CurrentCatalogStagePage ReadAccountPage(CurrentCatalogAccountFilter account,CatalogCursor? after=null,int limit=60,CancellationToken token=default)=>
        ReadOrderedPage(new(after is null?StageReadKind.First:StageReadKind.After,Cursor:after,Account:RequireAccount(account)),limit,token);
    public CurrentCatalogStagePage ReadAccountPreviousPage(CurrentCatalogAccountFilter account,CatalogCursor before,int limit=60,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(before);
        return ReadOrderedPage(new(StageReadKind.Before,Cursor:before,Account:RequireAccount(account)),limit,token);
    }
    public CurrentCatalogStagePage ReadAccountAtOrBefore(CurrentCatalogAccountFilter account,long utcMilliseconds,int limit=60,CancellationToken token=default)=>
        ReadOrderedPage(new(StageReadKind.Date,Date:utcMilliseconds,Account:RequireAccount(account)),limit,token);
    /// <summary>One primary-key lookup followed by exact canonical account
    /// comparison. This also works on genuine format1 without an account index.</summary>
    public DriveCatalogAsset? FindAccountAsset(CurrentCatalogAccountFilter account,Guid assetId,CancellationToken token=default)=>
        FindOrderedAsset(new(StageReadKind.Identity,Id:assetId,Account:RequireAccount(account)),token);

    private static CurrentCatalogAccountFilter RequireAccount(CurrentCatalogAccountFilter account)=>account ?? throw new ArgumentNullException(nameof(account));
    private DriveCatalogAsset? FindOrderedAsset(StageReadPosition position,CancellationToken token)
    {
        var page=ReadOrderedPage(position,1,token);
        if(page.HasMore) throw new InvalidDataException("A canonical identity resolved to more than one row.");
        return page.Assets.SingleOrDefault();
    }
    private enum StageReadKind { First,After,Before,Date,Identity }
    private sealed record StageReadPosition(StageReadKind Kind,CatalogCursor? Cursor=null,long? Date=null,Guid? Id=null,CurrentCatalogAccountFilter? Account=null);

    private void ValidateRead(StageReadPosition position,int limit,CancellationToken token)
    {
        RequireOpen();if(_sealed is null) throw new InvalidOperationException("Seal and verify private staging before reading its gallery projection.");
        if(limit is <1 or >90 || position.Cursor is {} cursor && (cursor.Id==Guid.Empty || !ValidStageDate(cursor.EffectiveUtc)) ||
            position.Kind is StageReadKind.After or StageReadKind.Before && position.Cursor is null ||
            position.Kind==StageReadKind.Date && (position.Date is null || !ValidStageDate(position.Date)) ||
            position.Kind==StageReadKind.Identity && (position.Id is null || position.Id==Guid.Empty))
            throw new ArgumentException("A bounded valid catalog position is required.");
        token.ThrowIfCancellationRequested();
        if(position.Account is not null && position.Kind!=StageReadKind.Identity && !SupportsAccountPaging)
            throw new CurrentCatalogAccountPagingUnavailableException();
    }

    private CurrentCatalogStagePage ReadOrderedPage(StageReadPosition position,int limit,CancellationToken token)
    {
        ValidateRead(position,limit,token);
        using var query=BuildReadQuery(position,limit);
        var assets=new List<DriveCatalogAsset>();var more=false;using var rows=query.ExecuteReader();
        while(rows.Read())
        {
            token.ThrowIfCancellationRequested();if(assets.Count==limit){more=true;break;}
            var asset=ReadGalleryRow(rows);
            if(position.Account is {} account && asset.AccountId!=account.AccountId)
            {
                if(position.Kind==StageReadKind.Identity) continue; // The one exact ID belongs to another account.
                throw new InvalidDataException("An indexed account page returned another canonical account.");
            }
            assets.Add(asset);
        }
        if(position.Kind==StageReadKind.Before) assets.Reverse();
        return new(assets.AsReadOnly(),more);
    }

    private SqliteCommand BuildReadQuery(StageReadPosition position,int limit)
    {
        var query=_connection!.CreateCommand();
        try
        {
            var predicate=position.Kind switch
            {
                StageReadKind.First=>"",
                StageReadKind.After=>"(date_unknown,date_sort,id)>($unknown,$sort,$id)",
                StageReadKind.Before=>"(date_unknown,date_sort,id)<($unknown,$sort,$id)",
                StageReadKind.Date=>"(date_unknown,date_sort)>=(0,$sort)",
                StageReadKind.Identity=>"id=$id",
                _=>throw new InvalidOperationException("Unknown internal canonical query kind.")
            };
            var indexedAccount=position.Account is not null && position.Kind!=StageReadKind.Identity;
            if(indexedAccount) predicate="account_id=$account"+(predicate.Length==0?"":" AND "+predicate);
            var reverse=position.Kind==StageReadKind.Before;
            query.CommandText="SELECT id,prefix,length(payload),payload,sha256,date_unknown,date_sort"+(_formatVersion==2?",account_id":"")+
                " FROM fact"+(indexedAccount?" INDEXED BY current_stage_account_date":"")+(predicate.Length==0?"":" WHERE "+predicate)+
                (reverse?" ORDER BY date_unknown DESC,date_sort DESC,id DESC":" ORDER BY date_unknown,date_sort,id")+" LIMIT $limit";
            query.Parameters.AddWithValue("$limit",limit+1);
            if(indexedAccount && position.Account is {} account) query.Parameters.AddWithValue("$account",account.AccountId);
            if(position.Cursor is {} key)
            {
                query.Parameters.AddWithValue("$unknown",key.EffectiveUtc is null);
                query.Parameters.AddWithValue("$sort",key.EffectiveUtc is null?0L:-key.EffectiveUtc.Value);
                query.Parameters.AddWithValue("$id",key.Id.ToString("D"));
            }
            if(position.Kind==StageReadKind.Date) query.Parameters.AddWithValue("$sort",-position.Date!.Value);
            if(position.Kind==StageReadKind.Identity) query.Parameters.AddWithValue("$id",position.Id!.Value.ToString("D"));
            return query;
        }
        catch {query.Dispose();throw;}
    }

    // Native query-plan evidence for the same command used by account paging.
    // No arbitrary SQL, handles, whole-catalog output or authority is exposed.
    internal string ExplainAccountPage(CurrentCatalogAccountFilter account,CatalogCursor? after=null)
    {
        var position=new StageReadPosition(after is null?StageReadKind.First:StageReadKind.After,Cursor:after,Account:RequireAccount(account));
        ValidateRead(position,1,CancellationToken.None);using var command=BuildReadQuery(position,1);
        command.CommandText="EXPLAIN QUERY PLAN "+command.CommandText;using var rows=command.ExecuteReader();var plan=new List<string>();
        while(rows.Read()) {if(plan.Count==16) throw new InvalidDataException("The bounded canonical query plan exceeded its shape.");plan.Add(rows.GetString(3));}
        return string.Join(';',plan);
    }

    private DriveCatalogAsset ReadGalleryRow(SqliteDataReader row)
    {
        if(row.GetInt64(2) is <1 or >CatalogChunkDecoder.MaximumExpandedBytes) throw new InvalidDataException("A staged row exceeds its byte bound.");
        var bytes=row.GetFieldValue<byte[]>(3);var fact=_codec.Read(bytes,row.GetString(1));
        if(Hash(bytes)!=row.GetString(4) || fact.Assets.Count!=1 || fact.Assets[0].Id.ToString("D")!=row.GetString(0) ||
            row.GetBoolean(5)!=(fact.Assets[0].EffectiveUtc is null) ||
            row.GetInt64(6)!=(fact.Assets[0].EffectiveUtc is null?0L:-fact.Assets[0].EffectiveUtc!.Value) ||
            _formatVersion==2 && row.GetString(7)!=fact.Assets[0].AccountId)
            throw new InvalidDataException("Staged gallery row failed identity, digest, account or date validation.");
        return fact.Assets[0];
    }
    private static bool ValidStageDate(long? value)=>value is null or >= -62135596800000L and <=253402300799999L;
}
