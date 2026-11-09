using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class TakeoutMetadataStageStorage
{
    private static Task<bool> AssertReadyAsync(TakeoutVerifiedMetadata completed,ProcessorStateCoordinator state,CancellationToken token)
        =>state.ExecuteAsync(database=>{token.ThrowIfCancellationRequested();completed.AssertReadyForReplayRetirement(database);return true;},token);

    private async Task RetireReplayAsync(TakeoutVerifiedMetadata completed,string item,string attempt,ProcessorStateCoordinator state,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();CheckReplayArea(completed,item,attempt);
        var replay=Path.Combine(attempt,"input.bin");
        if(!File.Exists(replay))return; // Already retired; source/result were still verified.
        await using(var duplicate=OpenRead(replay))
            await new ArtifactVerifier(TakeoutMetadataDocument.MaximumSourceBytes).VerifyAsync(duplicate,completed.Source,token).ConfigureAwait(false);
        // SQLite is rechecked outside filesystem/account locks. The same native
        // item-use admission remains held through both IO and final unlink; the
        // enclosing call still holds actual verified source/result read handles.
        await AssertReadyAsync(completed,state,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock(AccountingGate)
        {
            CheckReplayArea(completed,item,attempt);token.ThrowIfCancellationRequested();Regular(replay,false);File.Delete(replay);
        }
    }
    private void CheckReplayArea(TakeoutVerifiedMetadata completed,string item,string attempt)
    {
        Regular(_root,true);AssertBytes(Path.Combine(_root,"owner.json"),OwnerBytes(_installation));
        Regular(item,true);AssertBytes(Path.Combine(item,"claim.json"),ItemBytes(completed.JobId,completed.JobId,completed.ItemContext,_installation));
        foreach(var entry in Entries(item,MaximumAttempts+3))
        {
            var name=Path.GetFileName(entry);
            if(name is "claim.json" or "source.bin"){Regular(entry,false);continue;}
            if(name.Length!=40||!name.StartsWith("attempt-",StringComparison.Ordinal)||!Id(name[8..],out _))
                throw new InvalidDataException("Metadata replay retirement found an unexplained item entry; preserve every file.");
            Regular(entry,true);
        }
        Regular(attempt,true);AssertBytes(Path.Combine(attempt,"claim.json"),AttemptBytes(completed.JobId,completed.JobId,completed.ItemContext,_installation,completed.LeaseId,completed.Attempt));
        foreach(var entry in Entries(attempt,4))
        {
            var name=Path.GetFileName(entry);Regular(entry,false);
            if(name is "claim.json" or "input.bin"||name==completed.Artifact.RelativePath)continue;
            if(name=="diagnostics.log"&&new FileInfo(entry).Length==0)continue;
            throw new InvalidDataException("Metadata replay retirement found unknown or unresolved worker evidence; preserve every file.");
        }
    }
}
