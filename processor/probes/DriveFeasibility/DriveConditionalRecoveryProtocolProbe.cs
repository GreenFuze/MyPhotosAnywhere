using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mpa.Processor.DriveProbes;

internal static class DriveConditionalRecoveryProtocolProbe
{
    internal static async Task RunAsync(string root)
    {
        var run=Guid.NewGuid().ToString("N");var name="conditional-media-"+run+".journal.jsonl";var path=Path.Combine(root,name);
        var first="MPA synthetic current metadata A; no user content.\n"u8.ToArray();
        var pending="MPA synthetic current metadata C; no user content.\n"u8.ToArray();
        var records=new object[] {
            new { operation="before-folder-create",id="new-synthetic-parent",parent="disposable-root",name="MPA conditional media synthetic probe "+run },
            new { operation="before-synthetic-media-create",id="new-synthetic-id",parent="new-synthetic-parent",bytes=first.Length,sha256=Hash(first) },
            new { operation="before-synthetic-media-update",id="new-synthetic-id",bytes=pending.Length,sha256=Hash(pending),condition="\"actual-old-tag\"" },
            new { operation="synthetic-media-completion-discarded",id="new-synthetic-id",status=200,bodyConsumed=false }
        };
        JsonArray Fresh()=>JsonSerializer.SerializeToNode(records)!.AsArray();
        async Task WriteAsync(JsonArray values)=>await File.WriteAllTextAsync(path,string.Join('\n',values.Select(x=>x!.ToJsonString()))+"\n",new UTF8Encoding(false),CancellationToken.None).ConfigureAwait(false);
        try
        {
            var before=new ConditionalObservation(200,"\"actual-tag\"",false,8);
            foreach(var version in new[]{8L,9L,11L}) DriveConditionalMediaProbe.AssertRejectedContent(before,before with { Version=version });
            foreach(var invalid in new[]{before with { Version=7 },before with { Etag="\"different-tag\"" },before with { Trashed=true },before with { Status=412 }})
                await FailsAsync<InvalidDataException>(()=> { DriveConditionalMediaProbe.AssertRejectedContent(before,invalid);return Task.CompletedTask; }).ConfigureAwait(false);
            foreach(var invalid in new[]{before with { Etag=null },before with { Etag="W/\"weak\"" },before with { Etag="*" },before with { Version=0 }})
                await FailsAsync<InvalidDataException>(()=> { DriveConditionalMediaProbe.AssertRejectedContent(invalid,invalid);return Task.CompletedTask; }).ConfigureAwait(false);
            await WriteAsync(Fresh()).ConfigureAwait(false);
            var accepted=await DriveConditionalMediaProbe.ReadRetainedAsync(name,root,CancellationToken.None).ConfigureAwait(false);
            if (accepted!=new RetainedSyntheticMedia("new-synthetic-id","new-synthetic-parent","\"actual-old-tag\""))
                throw new InvalidOperationException("Retained synthetic identity/precondition changed.");
            var changes=new Action<JsonArray>[] {
                rows=>rows[3]!["operation"]="missing-discard",
                rows=>rows[2]!["id"]="replacement-id",
                rows=>rows[1]!["sha256"]=Hash(pending),
                rows=>rows[1]!["parent"]="different-parent",
                rows=>rows[0]!["name"]="different-run",
                rows=>rows[2]!["condition"]="W/\"weak\"",
                rows=>rows[2]!["condition"]="*",
                rows=>rows[3]!["status"]=201,
                rows=>rows[3]!["bodyConsumed"]=true,
                rows=>rows.Add(rows[3]!.DeepClone()),
                rows=>rows.Add(JsonSerializer.SerializeToNode(new { operation="before-synthetic-media-update",id="new-synthetic-id",bytes=pending.Length,sha256=Hash(pending),condition="\"different-new-tag\"" }))
            };
            foreach(var change in changes)
            {
                var rows=Fresh();change(rows);await WriteAsync(rows).ConfigureAwait(false);
                await FailsAsync<InvalidDataException>(()=>DriveConditionalMediaProbe.ReadRetainedAsync(name,root,CancellationToken.None)).ConfigureAwait(false);
            }
            var allowed=Fresh();allowed.Add(JsonSerializer.SerializeToNode(new { operation="before-synthetic-media-update",id="new-synthetic-id",bytes=first.Length,sha256=Hash(first),condition="\"actual-old-tag\"" }));
            await WriteAsync(allowed).ConfigureAwait(false);
            if (await DriveConditionalMediaProbe.ReadRetainedAsync(name,root,CancellationToken.None).ConfigureAwait(false)!=accepted)
                throw new InvalidOperationException("Retained stale retry changed its original discarded-completion identity.");
            await File.WriteAllBytesAsync(path,new byte[65537],CancellationToken.None).ConfigureAwait(false);
            await FailsAsync<InvalidDataException>(()=>DriveConditionalMediaProbe.ReadRetainedAsync(name,root,CancellationToken.None)).ConfigureAwait(false);
            await File.WriteAllTextAsync(path,"{}",CancellationToken.None).ConfigureAwait(false);
            await FailsAsync<InvalidDataException>(()=>DriveConditionalMediaProbe.ReadRetainedAsync(name,root,CancellationToken.None)).ConfigureAwait(false);
            await FailsAsync<ArgumentException>(()=>DriveConditionalMediaProbe.ReadRetainedAsync("../other.jsonl",root,CancellationToken.None)).ConfigureAwait(false);
        }
        finally { File.Delete(path); }
        Console.WriteLine("Retained synthetic media recovery identity/receipt/condition/size/path checks passed; no Google request occurred.");
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task FailsAsync<T>(Func<Task> action) where T:Exception
    { try { await action().ConfigureAwait(false); } catch(T) { return; } throw new InvalidOperationException("Retained synthetic recovery did not reject "+typeof(T).Name); }
}
