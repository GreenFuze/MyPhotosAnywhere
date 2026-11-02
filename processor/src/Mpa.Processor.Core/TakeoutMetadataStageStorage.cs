using System.Text.Json;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

/// <summary>Private, retained sidecar snapshots and generated worker attempts. This
/// owner never accepts an original path and never deletes source or result bytes.
/// A canceled capture resumes only after comparing its entire retained prefix.</summary>
public sealed class TakeoutMetadataStageStorage : IDisposable
{
    private const string Name="takeout-metadata-stages";
    private const int MaximumItems=1024;
    private const int MaximumAttempts=32;
    private static readonly object AccountingGate=new();
    private readonly object _gate=new();
    private readonly HashSet<string> _users=new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly ProcessorStateDatabase _database;
    private readonly Guid _installation;
    private readonly StorageBudgetManager _budget;
    private FileStream? _lease;
    private bool _closed;

    public TakeoutMetadataStageStorage(ProcessorStateDatabase database,StorageBudgetManager budget)
    {
        ArgumentNullException.ThrowIfNull(database);ArgumentNullException.ThrowIfNull(budget);
        _database=database;var stateDirectory=database.StateDirectory;var installation=database.InstallationId;
        if(!Path.IsPathFullyQualified(stateDirectory) || installation==Guid.Empty)throw new ArgumentException("A dedicated installed state owner is required.");
        var state=Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        if(state==Path.GetPathRoot(state))throw new ArgumentException("A dedicated state directory is required.");
        Regular(state,true);AssertInstallation(state,installation);
        _root=Path.Combine(state,Name);_installation=installation;_budget=budget;
        using var reserved=budget.Reserve(65536);reserved.RecheckBeforeWrite(reserved.Bytes);
        lock(AccountingGate)
        {
            var existed=Directory.Exists(_root);
            if(!existed)Directory.CreateDirectory(_root);
            Regular(_root,true);
            if(existed && !File.Exists(Path.Combine(_root,"owner.json")))throw new InvalidDataException("Preserve the unmarked metadata directory; it cannot be adopted.");
            FileStream? lease=null;
            try
            {
                var path=Path.Combine(_root,"owner.lock");if(File.Exists(path))Regular(path,false);
                lease=new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                if(!existed)WriteNew(Path.Combine(_root,"owner.json"),OwnerBytes(installation));
                AssertBytes(Path.Combine(_root,"owner.json"),OwnerBytes(installation));
                _=MeasureInventory(_root,installation);
                _lease=lease;lease=null;
            }
            finally {lease?.Dispose();}
        }
    }

    public async Task CaptureAsync(TakeoutMetadataWork work,Stream borrowedSource,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);ArgumentNullException.ThrowIfNull(borrowedSource);
        if(!borrowedSource.CanRead)throw new ArgumentException("A readable selected sidecar stream is required.",nameof(borrowedSource));
        var name=Acquire(work);
        try
        {
            token.ThrowIfCancellationRequested();
            using var reserved=_budget.Reserve(checked(work.Sidecar.Bytes+16384));reserved.RecheckBeforeWrite(reserved.Bytes);
            var item=EnsureItem(work);var ready=Path.Combine(item,"source.bin");
            if(File.Exists(ready))
            {
                await using var old=OpenRead(ready);
                await new ArtifactVerifier(TakeoutMetadataDocument.MaximumSourceBytes).VerifyAsync(old,work.Sidecar,token).ConfigureAwait(false);
                return;
            }
            var partial=Path.Combine(item,"source.pending");
            if(File.Exists(partial))Regular(partial,false);
            await using(var destination=new FileStream(partial,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read,8192,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {
                if(destination.Length>work.Sidecar.Bytes)throw new InvalidDataException("Retained sidecar prefix exceeds its captured input; nothing was replaced.");
                var buffer=new byte[8192];var comparison=new byte[8192];long prefix=destination.Length;
                while(prefix>0)
                {
                    var count=(int)Math.Min(prefix,buffer.Length);
                    await destination.ReadExactlyAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);
                    await borrowedSource.ReadExactlyAsync(comparison.AsMemory(0,count),token).ConfigureAwait(false);
                    if(!buffer.AsSpan(0,count).SequenceEqual(comparison.AsSpan(0,count)))throw new InvalidDataException("Selected sidecar changed its retained prefix; preserve the original capture.");
                    prefix-=count;
                }
                while(destination.Position<work.Sidecar.Bytes)
                {
                    token.ThrowIfCancellationRequested();var remaining=work.Sidecar.Bytes-destination.Position;
                    var count=await borrowedSource.ReadAsync(buffer.AsMemory(0,(int)Math.Min(remaining,buffer.Length)),token).ConfigureAwait(false);
                    if(count==0)throw new EndOfStreamException("Selected sidecar ended before its captured length; partial bytes were retained.");
                    reserved.RecheckBeforeWrite(remaining);
                    await destination.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);
                }
                if(await borrowedSource.ReadAsync(buffer.AsMemory(0,1),token).ConfigureAwait(false)!=0)throw new InvalidDataException("Selected sidecar exceeds its captured input.");
                await destination.FlushAsync(token).ConfigureAwait(false);destination.Flush(true);destination.Position=0;
                await new ArtifactVerifier(TakeoutMetadataDocument.MaximumSourceBytes).VerifyAsync(destination,work.Sidecar,token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            lock(AccountingGate)
            {
                CheckItem(work,item);Regular(partial,false);File.Move(partial,ready,overwrite:false);
            }
        }
        finally {Release(name);}
    }

    internal async Task<TakeoutMetadataAttemptStage> OpenAttemptAsync(TakeoutMetadataWork work,CancellationToken token)
    {
        var name=Acquire(work);StorageReservation? reserved=null;FileStream? input=null;FileStream? source=null;
        try
        {
            token.ThrowIfCancellationRequested();
            // Includes the supervisor's actual 8MiB diagnostics bound plus its
            // final read buffer, the generated artifact, snapshot and markers.
            reserved=_budget.Reserve(checked(work.Sidecar.Bytes+TakeoutMetadataDocument.MaximumArtifactBytes+8L*1024*1024+65536));
            reserved.RecheckBeforeWrite(reserved.Bytes);
            var item=Path.Combine(_root,name);CheckItem(work,item);
            if(File.Exists(Path.Combine(item,"source.pending")))throw new InvalidDataException("Complete the original retained capture before starting metadata analysis.");
            source=OpenRead(Path.Combine(item,"source.bin"));
            await new ArtifactVerifier(TakeoutMetadataDocument.MaximumSourceBytes).VerifyAsync(source,work.Sidecar,token).ConfigureAwait(false);source.Position=0;
            var attempt=Path.Combine(item,"attempt-"+work.Lease.LeaseId.ToString("N"));
            lock(AccountingGate)
            {
                CheckItem(work,item);
                if(Directory.EnumerateDirectories(item).Take(MaximumAttempts).Count()>=MaximumAttempts)throw new IOException("Retained metadata attempts reached their bounded capacity; preserve evidence before explicit cleanup.");
                if(File.Exists(attempt)||Directory.Exists(attempt))throw new IOException("This metadata attempt already has retained bytes; do not reuse its worker directory.");
                Directory.CreateDirectory(attempt);WriteNew(Path.Combine(attempt,"claim.json"),AttemptBytes(work,_installation));
            }
            var inputPath=Path.Combine(attempt,"input.bin");
            await using(var output=new FileStream(inputPath,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {
                var buffer=new byte[8192];long copied=0;int count;
                while((count=await source.ReadAsync(buffer,token).ConfigureAwait(false))!=0)
                {
                    reserved.RecheckBeforeWrite(reserved.Bytes-copied);
                    await output.WriteAsync(buffer.AsMemory(0,count),token).ConfigureAwait(false);copied+=count;
                }
                await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);
            }
            input=OpenRead(inputPath);
            await new ArtifactVerifier(TakeoutMetadataDocument.MaximumSourceBytes).VerifyAsync(input,work.Sidecar,token).ConfigureAwait(false);
            var stage=new TakeoutMetadataAttemptStage(this,name,attempt,work,input,source,reserved);input=null;source=null;reserved=null;return stage;
        }
        catch {input?.Dispose();source?.Dispose();reserved?.Dispose();Release(name);throw;}
    }

    private string Acquire(TakeoutMetadataWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if(!ReferenceEquals(work.Owner,_database))throw new InvalidOperationException("Metadata staging belongs to a different native state owner.");
        if(work.Sidecar.Bytes is <1 or >TakeoutMetadataDocument.MaximumSourceBytes || work.Sidecar.RelativePath!="input.bin")throw new InvalidDataException("Invalid captured metadata input.");
        var name=ItemName(work.Lease.JobId,work.Lease.ItemId);
        lock(_gate){ObjectDisposedException.ThrowIf(_closed,this);if(!_users.Add(name))throw new InvalidOperationException("Join the current sidecar capture/worker before reusing its item.");}
        return name;
    }
    internal void Release(string name)
    {lock(_gate)if(!_users.Remove(name))throw new InvalidOperationException("Metadata staging ownership was released twice.");}
    private string EnsureItem(TakeoutMetadataWork work)
    {
        var path=Path.Combine(_root,ItemName(work.Lease.JobId,work.Lease.ItemId));
        lock(AccountingGate)
        {
            Regular(_root,true);AssertBytes(Path.Combine(_root,"owner.json"),OwnerBytes(_installation));
            if(!Directory.Exists(path))
            {
                if(Directory.EnumerateDirectories(_root).Take(MaximumItems).Count()>=MaximumItems)throw new IOException("Retained metadata inputs reached capacity; keep originals and pending evidence.");
                Directory.CreateDirectory(path);WriteNew(Path.Combine(path,"claim.json"),ItemBytes(work,_installation));
            }
            CheckItem(work,path);return path;
        }
    }
    private void CheckItem(TakeoutMetadataWork work,string path)
    {Regular(_root,true);AssertBytes(Path.Combine(_root,"owner.json"),OwnerBytes(_installation));Regular(path,true);AssertBytes(Path.Combine(path,"claim.json"),ItemBytes(work,_installation));}

    public static long MeasureOwnedBytes(string stateDirectory)
    {
        lock(AccountingGate)
        {
            var root=Path.Combine(Path.GetFullPath(stateDirectory),Name);
            if(!Directory.Exists(root)){if(File.Exists(root))throw new IOException("Metadata accounting refuses a replaced directory.");return 0;}
            Regular(stateDirectory,true);var state=ReadMarker(Path.Combine(stateDirectory,"mpa-processor-state.json"));
            using var json=JsonDocument.Parse(state);
            if(!Guid.TryParseExact(json.RootElement.GetProperty("installationId").GetString(),"D",out var installation)||installation==Guid.Empty)throw new InvalidDataException("Metadata accounting lost its installation identity.");
            AssertBytes(Path.Combine(root,"owner.json"),OwnerBytes(installation));return MeasureInventory(root,installation);
        }
    }
    private static long MeasureInventory(string root,Guid installation)
    {
        Regular(root,true);long bytes=0;
        foreach(var entry in Entries(root,MaximumItems+2))
        {
            var name=Path.GetFileName(entry);
            if(name is "owner.json" or "owner.lock"){Regular(entry,false);bytes=checked(bytes+new FileInfo(entry).Length);continue;}
            if(name.Length!=70 || !name.StartsWith("item-",StringComparison.Ordinal)||name[37]!='-' || !Id(name[5..37],out var job)||!Id(name[38..],out var item))throw new InvalidDataException("Unknown metadata entry; no files were removed.");
            Regular(entry,true);var claim=Path.Combine(entry,"claim.json");using var document=JsonDocument.Parse(ReadMarker(claim));
            var context=document.RootElement.GetProperty("capturedItemContext").GetString()??throw new InvalidDataException("Missing retained metadata binding.");
            AssertBytes(claim,ItemBytes(job,item,context,installation));
            foreach(var file in Entries(entry,MaximumAttempts+3))
            {
                var child=Path.GetFileName(file);
                if(child is "claim.json" or "source.bin" or "source.pending")
                {Regular(file,false);var length=new FileInfo(file).Length;if(child!="claim.json"&&length>TakeoutMetadataDocument.MaximumSourceBytes)throw new InvalidDataException("Retained sidecar exceeds its metadata bound.");bytes=checked(bytes+length);continue;}
                if(child.Length!=40 || !child.StartsWith("attempt-",StringComparison.Ordinal)||!Id(child[8..],out var lease))throw new InvalidDataException("Unknown metadata attempt entry; preserve all files.");
                Regular(file,true);var attemptClaim=Path.Combine(file,"claim.json");using var attemptDocument=JsonDocument.Parse(ReadMarker(attemptClaim));
                var number=attemptDocument.RootElement.GetProperty("attempt").GetInt32();if(number<1)throw new InvalidDataException("Invalid retained attempt number.");
                AssertBytes(attemptClaim,AttemptBytes(job,item,context,installation,lease,number));
                foreach(var artifact in Entries(file,4))
                {
                    var artifactName=Path.GetFileName(artifact);long maximum=artifactName switch
                    {
                        "claim.json"=>16384,"input.bin"=>TakeoutMetadataDocument.MaximumSourceBytes,"diagnostics.log"=>8L*1024*1024+4096,
                        _ when artifactName=="facts-"+lease.ToString("N")+".json"=>TakeoutMetadataDocument.MaximumArtifactBytes,
                        _=>throw new InvalidDataException("Unknown metadata worker artifact; preserve all bytes.")
                    };
                    Regular(artifact,false);var length=new FileInfo(artifact).Length;if(length>maximum)throw new InvalidDataException("Retained metadata artifact exceeds its bound.");bytes=checked(bytes+length);
                }
            }
        }
        return bytes;
    }
    private static string[] Entries(string path,int maximum)
    {var entries=Directory.EnumerateFileSystemEntries(path).Take(maximum+1).ToArray();if(entries.Length>maximum)throw new InvalidDataException("Metadata inventory exceeds its bounded capacity.");return entries;}
    private static bool Id(string text,out Guid value)=>Guid.TryParseExact(text,"N",out value)&&value!=Guid.Empty&&value.ToString("N")==text;
    private static string ItemName(Guid job,Guid item)
    {if(job==Guid.Empty||item==Guid.Empty)throw new ArgumentException("Captured metadata job and item identities are required.");return "item-"+job.ToString("N")+"-"+item.ToString("N");}
    private static byte[] OwnerBytes(Guid installation)=>JsonSerializer.SerializeToUtf8Bytes(new {format="mpa-takeout-metadata-stages-1",installationId=installation.ToString("D")});
    private static byte[] ItemBytes(TakeoutMetadataWork work,Guid installation)=>ItemBytes(work.Lease.JobId,work.Lease.ItemId,work.Lease.CapturedItemContext,installation);
    private static byte[] ItemBytes(Guid job,Guid item,string context,Guid installation)=>JsonSerializer.SerializeToUtf8Bytes(new {format="mpa-takeout-staged-item-1",installationId=installation.ToString("D"),jobId=job.ToString("D"),itemId=item.ToString("D"),capturedItemContext=context});
    private static byte[] AttemptBytes(TakeoutMetadataWork work,Guid installation)=>AttemptBytes(work.Lease.JobId,work.Lease.ItemId,work.Lease.CapturedItemContext,installation,work.Lease.LeaseId,work.Lease.Attempt);
    private static byte[] AttemptBytes(Guid job,Guid item,string context,Guid installation,Guid lease,int attempt)=>JsonSerializer.SerializeToUtf8Bytes(new {format="mpa-takeout-staged-attempt-1",installationId=installation.ToString("D"),jobId=job.ToString("D"),itemId=item.ToString("D"),capturedItemContext=context,leaseId=lease.ToString("D"),attempt});
    private static void AssertInstallation(string state,Guid installation)
    {using var json=JsonDocument.Parse(ReadMarker(Path.Combine(state,"mpa-processor-state.json")));if(json.RootElement.GetProperty("installationId").GetString()!=installation.ToString("D"))throw new InvalidDataException("Metadata stage belongs to another installation.");}
    internal static FileStream OpenRead(string path)
    {Regular(path,false);return new(path,FileMode.Open,FileAccess.Read,FileShare.Read,8192,FileOptions.Asynchronous|FileOptions.SequentialScan);}
    internal static void Regular(string path,bool directory)
    {var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0||((attributes&FileAttributes.Directory)!=0)!=directory)throw new IOException("Metadata ownership refuses linked or unexpected entries.");}
    private static byte[] ReadMarker(string path)
    {using var file=OpenRead(path);if(file.Length is <1 or >16384)throw new InvalidDataException("Metadata marker exceeds its bound.");var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);return bytes;}
    private static void AssertBytes(string path,byte[] expected)
    {if(!ReadMarker(path).AsSpan().SequenceEqual(expected))throw new InvalidDataException("Retained metadata ownership or input binding changed.");}
    private static void WriteNew(string path,byte[] bytes)
    {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(bytes);file.Flush(true);}
    public void Dispose()
    {lock(_gate){if(_closed)return;if(_users.Count!=0)throw new InvalidOperationException("Join metadata capture and actual worker owners before closing staging.");_lease?.Dispose();_lease=null;_closed=true;}}
}

/// <summary>Transferred to one supervised worker. Input, output reservation and
/// area admission remain owned until its actual process and I/O retire.</summary>
internal sealed class TakeoutMetadataAttemptStage(TakeoutMetadataStageStorage owner,string name,string path,TakeoutMetadataWork work,FileStream input,FileStream source,StorageReservation reservation) : IDisposable
{
    private bool _closed;
    internal string Path { get; }=path;
    internal void Recheck()=>reservation.RecheckBeforeWrite(reservation.Bytes-work.Sidecar.Bytes);
    internal FileStream OpenResult(ArtifactDescriptor result)
    {
        ObjectDisposedException.ThrowIf(_closed,this);
        if(result.RelativePath!="facts-"+work.Lease.LeaseId.ToString("N")+".json"||result.Bytes>TakeoutMetadataDocument.MaximumArtifactBytes)throw new ProtocolFormatException("Metadata result changed its assigned artifact identity or bound.");
        TakeoutMetadataStageStorage.Regular(Path,true);return TakeoutMetadataStageStorage.OpenRead(System.IO.Path.Combine(Path,result.RelativePath));
    }
    public void Dispose()
    {if(_closed)return;input.Dispose();source.Dispose();reservation.Dispose();owner.Release(name);_closed=true;}
}
