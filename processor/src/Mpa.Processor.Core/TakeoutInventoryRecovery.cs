using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Core;

public sealed partial class TakeoutImportInventoryRepository
{
    /// <summary>Composition-root startup only: the database already owns the
    /// actual exclusive process/state lease, before admitting any inventory work.
    /// This fixed stage performs all IO in its own process and starts no children.
    /// No metadata-worker/original-upload stage may borrow this recovery rule.</summary>
    public int RecoverInterruptedAfterExclusiveRestart()
    {
        using var area=TakeoutAssociationCatalog.AcquireInterruptedRecoveryLease(_database);
        using var transaction=_database.Connection.BeginTransaction();
        using(var query=_database.Connection.CreateCommand())
        {
            query.Transaction=transaction;query.CommandText="""
                SELECT s.name,s.recipe_hash,s.checkpoint,s.result_receipt,j.captured_context,j.context_hash,i.input_fingerprint,i.captured_context,s.item_id,j.id
                FROM item_stage s JOIN job j ON j.id=s.job_id JOIN job_item i ON i.job_id=j.id AND i.id=s.item_id
                WHERE j.kind=$kind AND s.state IN('running','canceling') LIMIT 65
                """;query.Parameters.AddWithValue("$kind",Kind);using var rows=query.ExecuteReader();var count=0;
            while(rows.Read())
            {
                if(++count>64||rows.GetString(0)!=Stage||rows.GetString(1)!=Recipe||!rows.IsDBNull(2)||!rows.IsDBNull(3)||rows.GetString(7)!=Item||rows.GetString(8)!=rows.GetString(9))throw new InvalidDataException("Unknown interrupted inventory contract; retain all pending work.");
                var context=rows.GetString(4);var body=Encoding.UTF8.GetBytes(context);var hash=Hash(body);
                if(hash!=rows.GetString(5)||hash!=rows.GetString(6))throw new InvalidDataException("Interrupted inventory lost its original context digest.");
                using var document=JsonDocument.Parse(context);var root=document.RootElement;
                if(root.GetProperty("format").GetString()!="mpa-takeout-import-inventory-1")throw new InvalidDataException("Unknown interrupted inventory format.");
                var target=root.GetProperty("target").Deserialize<TakeoutImportTarget>()??throw new InvalidDataException("Missing original destination.");
                var parts=root.GetProperty("parts").Deserialize<TakeoutInputBinding[]>()??throw new InvalidDataException("Missing original selected parts.");
                Validate(target,parts);if(!Encode(target,parts).AsSpan().SequenceEqual(body))throw new InvalidDataException("Interrupted inventory changed its exact context shape.");
            }
        }
        using var update=_database.Connection.CreateCommand();update.Transaction=transaction;update.CommandText="""
            UPDATE item_stage SET state=CASE WHEN (SELECT canceled FROM job WHERE id=job_id)=1 THEN 'canceled' ELSE 'queued' END,lease_id=NULL
            WHERE state IN('running','canceling') AND job_id IN(SELECT id FROM job WHERE kind=$kind)
            """;update.Parameters.AddWithValue("$kind",Kind);var changed=update.ExecuteNonQuery();transaction.Commit();return changed;
    }
}
