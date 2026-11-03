using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

/// <summary>One borrowed, sealed source held by its caller's historical read lane.
/// Stream and validate canonical rows directly in timeline order. No discovery
/// snapshot, provider authority or full-library list is manufactured.</summary>
public sealed partial class CurrentCatalogStageIndex
{
    public CatalogViewerSessionIndex CaptureViewerIndex(string ownedIndexPath,Guid selectedId,CurrentCatalogAccountFilter? account=null,CancellationToken token=default)=>
        CatalogViewerSessionIndex.CaptureCanonical(this,ownedIndexPath,selectedId,account?.AccountId,_budget,token);
    internal long CountViewerIdentities(CurrentCatalogAccountFilter? account,CancellationToken token)
    {
        ValidateRead(new(StageReadKind.First,Account:account),1,token);
        using var query=_connection!.CreateCommand();
        query.CommandText="SELECT count(*) FROM fact"+(account is null?"":" INDEXED BY current_stage_account_date WHERE account_id=$account");
        if(account is not null) query.Parameters.AddWithValue("$account",account.AccountId);
        var count=Convert.ToInt64(query.ExecuteScalar(),CultureInfo.InvariantCulture);token.ThrowIfCancellationRequested();
        if(count<0 || count>_sealed!.Assets || account is null && count!=_sealed.Assets) throw new InvalidDataException("The canonical viewer count disagrees with its sealed descriptor.");
        return count;
    }
    internal IEnumerable<Guid> StreamViewerIdentities(CurrentCatalogAccountFilter? account,CancellationToken token)
    {
        ValidateRead(new(StageReadKind.First,Account:account),1,token);
        using var query=_connection!.CreateCommand();
        query.CommandText="SELECT id,prefix,length(payload),payload,sha256,date_unknown,date_sort"+(_formatVersion==2?",account_id":"")+
            " FROM fact"+(account is null?"":" INDEXED BY current_stage_account_date WHERE account_id=$account")+" ORDER BY date_unknown,date_sort,id";
        if(account is not null) query.Parameters.AddWithValue("$account",account.AccountId);
        using var rows=query.ExecuteReader();
        while(rows.Read())
        {
            token.ThrowIfCancellationRequested();var asset=ReadGalleryRow(rows);
            if(account is not null && asset.AccountId!=account.AccountId) throw new InvalidDataException("The canonical viewer stream returned a foreign account.");
            yield return asset.Id;
        }
    }
}

public sealed partial class CatalogViewerSessionIndex
{
    internal static CatalogViewerSessionIndex CaptureCanonical(CurrentCatalogStageIndex source, string ownedIndexPath, Guid selected, string? accountId,
        StorageBudgetManager storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedIndexPath);
        if (selected == Guid.Empty) throw new ArgumentException("A selected canonical media identity is required.", nameof(selected));
        if (accountId is not null && (accountId.Length is < 1 or > 256 || !accountId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Invalid captured account filter.", nameof(accountId));
        var owner = new CatalogViewerSessionIndex(ownedIndexPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(owner._path) ?? throw new ArgumentException("A private session directory is required.", nameof(ownedIndexPath));
            if (!Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The owned viewer session directory is missing or linked.");
            if (File.Exists(owner._path + "-journal") || File.Exists(owner._path + "-wal") || File.Exists(owner._path + "-shm"))
                throw new IOException("Viewer index creation refuses pre-existing sidecars.");
            var account=accountId is null?null:new CurrentCatalogAccountFilter(accountId);
            var total=source.CountViewerIdentities(account,cancellationToken);
            // Conservative SQLite page/index/journal overhead; retain the reservation
            // until the actual generated file has closed and been removed.
            owner._storage = storage.Reserve(checked(4L * 1024 * 1024 + total * 512));
            owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
            using (var file = new FileStream(owner._path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owner._ownsFile = true; }
            owner._connection = new(new SqliteConnectionStringBuilder { DataSource = owner._path, Mode = SqliteOpenMode.ReadWrite,
                Pooling = false, DefaultTimeout = 5 }.ToString());
            owner._connection.Open();
            using (var setup = owner._connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=-2048; PRAGMA temp_store=FILE;" +
                    "PRAGMA application_id=" + ApplicationId.ToString(CultureInfo.InvariantCulture) + "; CREATE TABLE entry(position INTEGER PRIMARY KEY,asset_id TEXT NOT NULL UNIQUE) STRICT;";
                setup.ExecuteNonQuery();
            }
            using var destination = owner._connection.BeginTransaction();
            using var insert = owner._connection.CreateCommand(); insert.Transaction = destination;
            insert.CommandText = "INSERT INTO entry(position,asset_id) SELECT $start+CAST(key AS INTEGER),value FROM json_each($batch)";
            var start = insert.Parameters.Add("$start", SqliteType.Integer); var encoded = insert.Parameters.Add("$batch", SqliteType.Text); insert.Prepare();
            var batch = new List<string>(256);
            void FlushBatch()
            {
                if (batch.Count == 0) return;
                cancellationToken.ThrowIfCancellationRequested(); owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
                start.Value = owner.Count - batch.Count; encoded.Value = JsonSerializer.Serialize(batch);
                if (insert.ExecuteNonQuery() != batch.Count) throw new InvalidOperationException("Viewer index batch did not retain every captured identity.");
                batch.Clear();
            }
            foreach(var id in source.StreamViewerIdentities(account,cancellationToken))
            {
                if (owner.Count % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
                if(id==Guid.Empty) throw new InvalidDataException("The canonical index returned an empty identity.");
                if(id==selected) owner.SelectedPosition=owner.Count;
                batch.Add(id.ToString("D"));owner.Count=checked(owner.Count+1);
                if(owner.Count>total) throw new InvalidDataException("The canonical stream exceeded its exact sealed count.");
                if(batch.Count==256) FlushBatch();
            }
            FlushBatch();
            if (owner.Count != total) throw new InvalidOperationException("Viewer snapshot count changed inside its transaction.");
            if (owner.SelectedPosition < 0) throw new CatalogSessionSelectionUnavailableException();
            cancellationToken.ThrowIfCancellationRequested(); owner._storage.RecheckBeforeWrite(owner._storage.Bytes);
            destination.Commit();
            if (new FileInfo(owner._path).Length > owner._storage.Bytes) throw new InvalidOperationException("Viewer index exceeded its reserved storage bound.");
            return owner;
        }
        catch (Exception error)
        {
            try { owner.Dispose(); }
            catch (Exception cleanup) { throw new CatalogSessionCaptureException(new AggregateException(error, cleanup), owner); }
            throw;
        }
    }
}
