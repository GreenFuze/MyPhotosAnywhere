using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

// Historical authored configuration only; never a fresh publication scope.
public sealed partial class CatalogGenerationCaptureRepository
{
    private string ReadArchivedPredecessorHumanSources(long revision,SqliteTransaction transaction)
    {
        if(revision<1 || !ReferenceEquals(transaction.Connection,_database.Connection))
            throw new InvalidDataException("Historical human configuration requires its exact owning revision/transaction.");
        string text;
        using(var query=Command(transaction,"SELECT length(CAST(snapshot AS BLOB)),snapshot FROM configuration_outbox WHERE revision=$revision",("$revision",revision)))
        using(var row=query.ExecuteReader())
        {
            if(!row.Read() || row.GetInt64(0) is <1 or >1024*1024)
                throw new InvalidDataException("Historical human source configuration is missing or exceeds its original bound.");
            text=row.GetString(1);
        }
        try
        {
            var bytes=Encoding.UTF8.GetBytes(text);
            if(!new CanonicalJson(1024*1024).Encode(bytes).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Historical human source configuration changed its canonical encoding.");
            using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
            RequireArchivedSourceProperties(root,["format","major","revision","sources"]);
            if(ArchivedSourceString(root,"format")!="mpa-source-configuration" ||
                root.GetProperty("major").ValueKind!=JsonValueKind.Number || !root.GetProperty("major").TryGetInt32(out var major) || major!=1 ||
                ArchivedSourceCounter(root,"revision")!=revision || root.GetProperty("sources").ValueKind!=JsonValueKind.Array ||
                root.GetProperty("sources").GetArrayLength()>256)
                throw new InvalidDataException("Historical human source configuration changed its supported original schema.");
            var sources=new List<LibrarySourceRecord>();var ids=new HashSet<Guid>();var folders=new HashSet<(string Account,string Folder)>();
            foreach(var value in root.GetProperty("sources").EnumerateArray())
            {
                RequireArchivedSourceProperties(value,["id","accountId","folderId","displayName","descendants","enabled","revision"]);
                var idText=ArchivedSourceString(value,"id");var account=ArchivedSourceString(value,"accountId");
                var folder=ArchivedSourceString(value,"folderId");var display=ArchivedSourceString(value,"displayName");
                if(!Guid.TryParseExact(idText,"D",out var id) || id==Guid.Empty || id.ToString("D")!=idText || !ids.Add(id) ||
                    !ArchivedSourceIdentity(account) || !ArchivedSourceIdentity(folder) || !folders.Add((account,folder)) ||
                    string.IsNullOrWhiteSpace(display) || display.Length>1024 || display.Any(char.IsControl) ||
                    value.GetProperty("descendants").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    value.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Historical authored source bindings changed their original identities or bounds.");
                sources.Add(new(id,account,folder,display,value.GetProperty("descendants").GetBoolean(),value.GetProperty("enabled").GetBoolean(),
                    ArchivedSourceCounter(value,"revision"),false)); // Connectivity is not an authored binding/fingerprint field.
            }
            return HashPredecessorHumanSources(new(revision,sources.AsReadOnly()));
        }
        catch(Exception error) when(error is JsonException or ProtocolFormatException or OverflowException)
        {throw new InvalidDataException("Historical human source configuration cannot be decoded safely.",error);}
    }

    private static void RequireArchivedSourceProperties(JsonElement value,IReadOnlyCollection<string> expected)
    {
        if(value.ValueKind!=JsonValueKind.Object || !value.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("Historical human source configuration has unknown or missing fields.");
    }
    private static string ArchivedSourceString(JsonElement value,string name)=>value.GetProperty(name).ValueKind==JsonValueKind.String ?
        value.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing historical source text.") :
        throw new InvalidDataException("Historical source text changed its type.");
    private static long ArchivedSourceCounter(JsonElement value,string name)
    {
        var text=ArchivedSourceString(value,name);
        if(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var counter) || counter<1 || counter.ToString(CultureInfo.InvariantCulture)!=text)
            throw new InvalidDataException("Historical source counter changed its exact positive decimal representation.");
        return counter;
    }
    private static bool ArchivedSourceIdentity(string value)=>value is {Length:>0 and <=256} && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
