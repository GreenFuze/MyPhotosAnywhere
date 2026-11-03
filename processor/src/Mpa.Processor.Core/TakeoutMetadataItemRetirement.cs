using System.Security.Cryptography;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class TakeoutMetadataStageStorage
{
    private const string RetiringName="takeout-metadata-retiring";
    private string RetiringPath=>Path.Combine(Path.GetDirectoryName(_root)!,RetiringName);

    /// <summary>Explicit local cleanup only. Revalidates the actual native ready
    /// result and its complete raw bytes in the durable upload journal. The Drive
    /// receipt is not treated as current availability or remote delete authority.
    /// The original, native job/outbox, raw journal and human metadata stay intact.</summary>
    public async Task<bool> RetireBackedUpItemAsync(TakeoutVerifiedMetadata completed,MetadataSourceBackupReceipt backup,ProcessorStateCoordinator state,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(completed);ArgumentNullException.ThrowIfNull(backup);ArgumentNullException.ThrowIfNull(state);
        if(!ReferenceEquals(completed.Owner,_database))throw new InvalidOperationException("Metadata cleanup belongs to another native state owner.");
        using var read=await AcquireRetainedReadAsync(token).ConfigureAwait(false);var name=ItemName(completed.JobId,completed.JobId);
        lock(_gate){ObjectDisposedException.ThrowIf(_closed,this);if(!_users.Add(name))throw new InvalidOperationException("Join the metadata capture and worker before cleanup.");}
        try
        {
            using var reservation=_budget.Reserve(2L*WorkspacePublicationRepository.MaximumPayloadBytes+65536);reservation.RecheckBeforeWrite(reservation.Bytes);
            var captured=await state.ExecuteAsync(db=>
            {
                if(!ReferenceEquals(db,_database))throw new InvalidOperationException("Metadata cleanup must borrow its exact native state owner.");
                return new TakeoutMetadataRetirementRepository(db,_budget).Capture(completed,new(backup.Scope.AccountId,backup.Scope.FolderId),backup.PublicationId);
            },token).ConfigureAwait(false);
            if(captured.Backup!=backup)throw new InvalidDataException("Metadata cleanup changed its actual backup receipt.");
            var previous=await state.ExecuteAsync(db=>new TakeoutMetadataRetirementRepository(db,_budget).Read(completed),token).ConfigureAwait(false);
            var item=Path.Combine(_root,name);
            if(previous is not null)
            {
                var pending=await state.ExecuteAsync(db=>new TakeoutMetadataRetirementRepository(db,_budget).Pending(),token).ConfigureAwait(false);
                if(pending is not null)
                {
                    if(pending.Body!=previous.Body)throw new InvalidOperationException("Recover the first pending metadata cleanup.");
                    await Task.Run(()=>ResumeItemRetirement(pending),CancellationToken.None).ConfigureAwait(false);
                    await state.ExecuteAsync(db=>{new TakeoutMetadataRetirementRepository(db,_budget).Complete(pending);return true;},CancellationToken.None).ConfigureAwait(false);
                }
                else if(Directory.Exists(item)||File.Exists(item)||Directory.Exists(RetiringPath)||File.Exists(RetiringPath))throw new InvalidDataException("Completed metadata cleanup has unexplained private entries.");
                return false;
            }
            token.ThrowIfCancellationRequested();
            ValidateRetirementAdmission(item,captured);
            // Once the exact durable intent commits, join the bounded unlink even
            // if the browser closes. A restart derives the same expected bytes.
            await state.ExecuteAsync(db=>{new TakeoutMetadataRetirementRepository(db,_budget).Prepare(captured);return true;},token).ConfigureAwait(false);
            await Task.Run(()=>ResumeItemRetirement(captured),CancellationToken.None).ConfigureAwait(false);
            await state.ExecuteAsync(db=>{new TakeoutMetadataRetirementRepository(db,_budget).Complete(captured);return true;},CancellationToken.None).ConfigureAwait(false);return true;
        }
        catch(Exception error)when(error is System.Text.Json.JsonException or FileNotFoundException or DirectoryNotFoundException)
        {throw new InvalidDataException("Metadata cleanup found missing or invalid private ownership evidence; no unexplained files may be removed.",error);}
        finally{Release(name);}
    }
    private void RecoverItemRetirement()
    {
        var journal=new TakeoutMetadataRetirementRepository(_database,_budget);var pending=journal.Last();
        if(pending is null)
        {if(Directory.Exists(RetiringPath)||File.Exists(RetiringPath))throw new InvalidDataException("Unmarked metadata cleanup evidence cannot be adopted.");return;}
        if(journal.Read(pending.Ready)?.Body!=pending.Body)throw new InvalidDataException("Metadata cleanup lost its first durable per-item intent; preserve all files.");
        ResumeItemRetirement(pending);journal.Complete(pending);
    }
    private void ResumeItemRetirement(TakeoutMetadataRetirement captured)
    {
        lock(AccountingGate)
        {
            RequireStageOwner();var item=Path.Combine(_root,ItemName(captured.Ready.JobId,captured.Ready.JobId));var retiring=RetiringPath;
            if(Path.GetDirectoryName(Path.GetFullPath(retiring))!=Path.GetDirectoryName(Path.GetFullPath(_root))||Path.GetDirectoryName(Path.GetFullPath(item))!=Path.GetFullPath(_root))
                throw new InvalidDataException("Metadata cleanup escaped its installed private area.");
            if(File.Exists(item)||File.Exists(retiring))throw new InvalidDataException("Metadata cleanup refuses replaced directories.");
            if(Directory.Exists(item))
            {
                if(Directory.Exists(retiring))throw new InvalidDataException("Metadata cleanup has two private candidates; preserve both.");
                ValidateRetirementFiles(item,captured,complete:true);Directory.Move(item,retiring);
            }
            if(!Directory.Exists(retiring))return; // Joined deletion finished before native completion.
            var files=ValidateRetirementFiles(retiring,captured,complete:false);
            foreach(var file in files){VerifyRetirementFile(Path.Combine(retiring,file.Key),file.Value);File.Delete(Path.Combine(retiring,file.Key));}
            var attempt=Path.Combine(retiring,"attempt-"+captured.Ready.LeaseId.ToString("N"));
            if(Directory.Exists(attempt)){Regular(attempt,true);Directory.Delete(attempt,recursive:false);}
            Regular(retiring,true);Directory.Delete(retiring,recursive:false);
        }
    }
    private void RequireStageOwner()
    {Regular(_root,true);AssertBytes(Path.Combine(_root,"owner.json"),OwnerBytes(_installation));}
    private void ValidateRetirementAdmission(string item,TakeoutMetadataRetirement captured)
    {
        // Keep this synchronous lock owner outside the async operation's
        // exception handling; every failed validation retires its monitor here.
        lock(AccountingGate)
        {RequireStageOwner();ValidateRetirementFiles(item,captured,complete:true);if(Directory.Exists(RetiringPath)||File.Exists(RetiringPath))throw new InvalidDataException("Recover the pending metadata cleanup before admitting another.");}
    }

    private Dictionary<string,(long Bytes,string Sha256)> ValidateRetirementFiles(string item,TakeoutMetadataRetirement captured,bool complete)
    {
        Regular(item,true);var ready=captured.Ready;var attemptName="attempt-"+ready.LeaseId.ToString("N");
        var expected=new Dictionary<string,(long Bytes,string Sha256)>(StringComparer.Ordinal);
        void Add(string name,byte[] bytes)=>expected.Add(name,(bytes.LongLength,Convert.ToHexStringLower(SHA256.HashData(bytes))));
        Add("claim.json",ItemBytes(ready.JobId,ready.JobId,ready.ItemContext,_installation));Add("source.bin",captured.Document.CopyRawSource());
        Add(Path.Combine(attemptName,"claim.json"),AttemptBytes(ready.JobId,ready.JobId,ready.ItemContext,_installation,ready.LeaseId,ready.Attempt));
        Add(Path.Combine(attemptName,ready.Artifact.RelativePath),captured.Document.EncodeArtifact());
        Add(Path.Combine(attemptName,"input.bin"),captured.Document.CopyRawSource());Add(Path.Combine(attemptName,"diagnostics.log"),[]);
        var found=new Dictionary<string,(long Bytes,string Sha256)>(StringComparer.Ordinal);
        foreach(var entry in Entries(item,3))
        {
            var name=Path.GetFileName(entry);
            if(name==attemptName)
            {
                Regular(entry,true);
                foreach(var child in Entries(entry,4))
                {var relative=Path.Combine(name,Path.GetFileName(child));if(!expected.TryGetValue(relative,out var fingerprint))throw new InvalidDataException("Keep unknown or failed metadata worker evidence.");VerifyRetirementFile(child,fingerprint);found.Add(relative,fingerprint);}
            }
            else
            {if(!expected.TryGetValue(name,out var fingerprint))throw new InvalidDataException("Keep unknown or unfinished metadata inputs and attempts.");VerifyRetirementFile(entry,fingerprint);found.Add(name,fingerprint);}
        }
        if(complete&&new[]{"claim.json","source.bin",Path.Combine(attemptName,"claim.json"),Path.Combine(attemptName,ready.Artifact.RelativePath)}.Any(name=>!found.ContainsKey(name)))
            throw new InvalidDataException("Metadata cleanup requires its complete owned raw source and result before retirement.");
        return found;
    }
    private static void VerifyRetirementFile(string path,(long Bytes,string Sha256) fingerprint)
    {
        using var file=OpenRead(path);
        if(file.Length!=fingerprint.Bytes||Convert.ToHexStringLower(SHA256.HashData(file))!=fingerprint.Sha256)throw new InvalidDataException("Metadata cleanup found changed private bytes; preserve every file.");
    }

    // Accounting only, without adopting or deleting anything. The fixed sibling
    // slot may have partially removed markers after a crash. Its exact contents
    // must still pass the native intent/outbox checks before startup can unlink.
    private static long MeasureRetiringBytes(string state)
    {
        var path=Path.Combine(state,RetiringName);if(!Directory.Exists(path)){if(File.Exists(path))throw new IOException("Metadata cleanup slot was replaced.");return 0;}
        Regular(path,true);long bytes=0;
        foreach(var entry in Entries(path,3))
        {
            var name=Path.GetFileName(entry);
            if(name is "claim.json" or "source.bin")
            {Add(entry,name=="claim.json"?16384:TakeoutMetadataDocument.MaximumSourceBytes);continue;}
            if(name.Length!=40||!name.StartsWith("attempt-",StringComparison.Ordinal)||!Id(name[8..],out var lease))throw new InvalidDataException("Unknown metadata cleanup evidence; preserve the area.");
            Regular(entry,true);
            foreach(var child in Entries(entry,4))
            {
                var childName=Path.GetFileName(child);var maximum=childName switch
                {"claim.json"=>16384,"input.bin"=>TakeoutMetadataDocument.MaximumSourceBytes,"diagnostics.log"=>0,_ when childName=="facts-"+lease.ToString("N")+".json"=>TakeoutMetadataDocument.MaximumArtifactBytes,_=>throw new InvalidDataException("Unknown metadata cleanup artifact; preserve all bytes.")};
                Add(child,maximum);
            }
        }
        return bytes;
        void Add(string file,long maximum){Regular(file,false);var length=new FileInfo(file).Length;if(length>maximum)throw new InvalidDataException("Metadata cleanup accounting exceeded its retained bound.");bytes=checked(bytes+length);}
    }
}
