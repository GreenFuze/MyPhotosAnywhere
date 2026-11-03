namespace Mpa.Processor.Core;

public sealed record TakeoutOriginalChoice(string Key,string Name,string FileId);
public sealed record TakeoutOriginalChoices(IReadOnlyList<TakeoutOriginalChoice> Originals,string? Next);

public sealed partial class TakeoutOriginalJobRepository
{
    /// <summary>Bounded pages of actual ready copy receipts. Browser cursors are
    /// local job IDs only; a cursor cannot select another import's originals.</summary>
    public TakeoutOriginalChoices ReadChoices(Guid importId,string? after=null,int limit=60)
    {
        if(importId==Guid.Empty||limit is <1 or >60||after is not null&&(!Guid.TryParseExact(after,"D",out var cursor)||cursor==Guid.Empty||cursor.ToString("D")!=after))
            throw new ArgumentException("Choose a captured import and bounded original page.");
        _=new TakeoutImportInventoryRepository(_database).ReadCompleted(importId)??throw new InvalidOperationException("Wait for the captured input review.");
        var captures=new List<(string Id,string Context,string Checkpoint)>();
        using(var query=_database.Connection.CreateCommand())
        {
            query.CommandText="""
                SELECT j.id,j.captured_context,s.checkpoint FROM job j
                JOIN item_stage s ON s.job_id=j.id AND s.item_id=j.id AND s.name=$stage
                WHERE j.kind=$kind AND j.id>$after AND s.state='ready'
                  AND json_extract(j.captured_context,'$.importId')=$import
                ORDER BY j.id LIMIT $limit
                """;
            query.Parameters.AddWithValue("$stage",Stage);query.Parameters.AddWithValue("$kind",Kind);query.Parameters.AddWithValue("$after",after??"");
            query.Parameters.AddWithValue("$import",importId.ToString("D"));query.Parameters.AddWithValue("$limit",limit+1);
            using var rows=query.ExecuteReader();while(rows.Read())captures.Add((rows.GetString(0),rows.GetString(1),rows.GetString(2)));
        }
        var more=captures.Count>limit;if(more)captures.RemoveAt(captures.Count-1);
        var choices=new List<TakeoutOriginalChoice>(captures.Count);
        foreach(var item in captures)
        {
            var captured=DecodeCapture(item.Context,item.Checkpoint);
            var original=ReadVerified(importId,captured.Capture.Entry.Key)??throw new InvalidDataException("Original selection lost a ready copy receipt.");
            if(original.JobId.ToString("D")!=item.Id)throw new InvalidDataException("Original page changed its captured job identity.");
            choices.Add(new(original.Capture.Entry.Key,original.Capture.Entry.RelativePath,original.FileId));
        }
        return new(choices,more?captures[^1].Id:null);
    }
}
