using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Display progress only. Completion here means local sidecar processing,
/// not original backup, metadata publication, or complete import acceptance.</summary>
public sealed record TakeoutMetadataProgress(
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long Total,
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long Ready,
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long Issues,
    [property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long Waiting,bool Finished);

public sealed partial class TakeoutImportInventoryRepository
{
    private const string MetadataProgressPrefix="takeout-metadata-progress-v1:";
    public void RecordMetadataProgress(TakeoutCompletedInventory completed,TakeoutMetadataProgress progress)
    {
        ArgumentNullException.ThrowIfNull(completed);ArgumentNullException.ThrowIfNull(progress);
        var actual=ReadCompleted(completed.ImportId)??throw new InvalidOperationException("Metadata requires its complete native input inventory.");
        if(actual.StateDirectory!=completed.StateDirectory||actual.InstallationId!=completed.InstallationId||actual.CapturedContext!=completed.CapturedContext||actual.Result!=completed.Result)
            throw new InvalidDataException("Metadata progress changed its captured import selection.");
        ValidateProgress(actual,progress);var old=ReadMetadataProgress(actual);
        if(old is not null&&old.Total!=progress.Total)throw new InvalidDataException("Metadata progress changed the captured sidecar count.");
        using var update=_database.Connection.CreateCommand();update.CommandText="INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        update.Parameters.AddWithValue("$key",MetadataProgressPrefix+completed.ImportId.ToString("D"));update.Parameters.AddWithValue("$value",Encoding.UTF8.GetString(ProgressBytes(actual,progress)));update.ExecuteNonQuery();
    }
    internal TakeoutMetadataProgress? ReadMetadataProgress(TakeoutCompletedInventory completed)
    {
        var raw=ReadMeta(MetadataProgressPrefix+completed.ImportId.ToString("D"));if(raw is null)return null;
        using var parsed=JsonDocument.Parse(raw);var progress=parsed.RootElement.GetProperty("progress").Deserialize<TakeoutMetadataProgress>()??throw new InvalidDataException("Metadata progress is missing.");
        ValidateProgress(completed,progress);
        if(!ProgressBytes(completed,progress).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(raw)))throw new InvalidDataException("Metadata progress changed its captured inventory or closed schema.");
        return progress;
    }
    private static byte[] ProgressBytes(TakeoutCompletedInventory completed,TakeoutMetadataProgress progress)=>new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(new
    {format="mpa-local-takeout-metadata-progress-1",importId=completed.ImportId.ToString("D"),indexSha256=completed.Result.Sha256,selectionSha256=Hash(Encoding.UTF8.GetBytes(completed.CapturedContext)),progress}));
    private static void ValidateProgress(TakeoutCompletedInventory completed,TakeoutMetadataProgress progress)
    {
        if(progress.Total<0||progress.Total>completed.Result.Entries-completed.Result.Media||progress.Ready<0||progress.Issues<0||progress.Waiting<0||
            progress.Ready>progress.Total||progress.Issues>progress.Total-progress.Ready||progress.Waiting>progress.Total-progress.Ready-progress.Issues||
            progress.Finished&&(progress.Waiting!=0||progress.Ready+progress.Issues!=progress.Total))
            throw new InvalidDataException("Metadata display progress exceeds its captured inventory or claims unfinished work complete.");
    }
}
