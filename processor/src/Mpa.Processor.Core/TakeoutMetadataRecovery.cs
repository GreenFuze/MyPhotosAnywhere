using System.Text;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class TakeoutMetadataJobRepository
{
    internal int RecoverInterruptedMetadata()
    {
        var leases=new List<StageLease>();
        using(var query=_database.Connection.CreateCommand())
        {
            query.CommandText="""
                SELECT j.id,j.captured_context,j.context_hash,i.id,i.captured_context,i.input_fingerprint,
                       s.name,s.recipe_hash,s.lease_id,s.attempt,s.checkpoint,s.result_receipt
                FROM job j JOIN job_item i ON i.job_id=j.id JOIN item_stage s ON s.job_id=j.id AND s.item_id=i.id
                WHERE j.kind=$kind AND s.state IN('running','canceling') LIMIT 1025
                """;query.Parameters.AddWithValue("$kind",JobKind);using var row=query.ExecuteReader();
            while(row.Read())
            {
                if(leases.Count==1024||row.GetString(6)!=Stage||row.GetString(7)!=TakeoutMetadataDocument.RecipeSha256||row.IsDBNull(8)||row.GetInt32(9)<1||!row.IsDBNull(10)||!row.IsDBNull(11)||
                    Hash(Encoding.UTF8.GetBytes(row.GetString(1)))!=row.GetString(2)||Hash(Encoding.UTF8.GetBytes(row.GetString(4)))!=row.GetString(5))
                    throw new InvalidDataException("Unknown interrupted metadata contract; preserve its original inputs and receipts.");
                leases.Add(new(Guid.ParseExact(row.GetString(0),"D"),Guid.ParseExact(row.GetString(3),"D"),Stage,Guid.ParseExact(row.GetString(8),"D"),row.GetInt32(9),row.GetString(1),null,row.GetString(4)));
            }
        }
        foreach(var lease in leases)_=Decode(lease);
        using var transaction=_database.Connection.BeginTransaction();
        foreach(var lease in leases)
        {
            using var update=_database.Connection.CreateCommand();update.Transaction=transaction;update.CommandText="""
                UPDATE item_stage SET state=CASE WHEN(SELECT canceled FROM job WHERE id=$job)=1 THEN 'canceled' ELSE 'queued' END,lease_id=NULL
                WHERE job_id=$job AND item_id=$item AND name=$stage AND lease_id=$lease AND attempt=$attempt
                  AND state IN('running','canceling') AND result_receipt IS NULL AND checkpoint IS NULL
                """;
            update.Parameters.AddWithValue("$job",lease.JobId.ToString("D"));update.Parameters.AddWithValue("$item",lease.ItemId.ToString("D"));update.Parameters.AddWithValue("$stage",Stage);
            update.Parameters.AddWithValue("$lease",lease.LeaseId.ToString("D"));update.Parameters.AddWithValue("$attempt",lease.Attempt);
            if(update.ExecuteNonQuery()!=1)throw new StaleStageLeaseException();
        }
        transaction.Commit();return leases.Count;
    }
}
