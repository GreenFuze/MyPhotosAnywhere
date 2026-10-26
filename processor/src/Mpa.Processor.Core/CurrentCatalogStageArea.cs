using System.Text.Json;

namespace Mpa.Processor.Core;

/// <summary>Leases one marked generated-only directory. Durable claims/files survive
/// Exit and failed writes; unknown/corrupt entries are never automatically removed.
/// Native indexes must physically join before this borrowed area retires.</summary>
public sealed class CurrentCatalogStageArea : IDisposable
{
    private const string Name="current-catalog-stages";
    private static readonly object AccountingGate=new();
    private readonly string _root;
    private readonly object _gate=new();
    private readonly HashSet<Guid> _users=[];
    private readonly HashSet<Guid> _retiring=[];
    private readonly Dictionary<Guid,byte[]> _claims;
    private readonly Guid _installation;
    private FileStream? _lease;
    private bool _closed;

    public CurrentCatalogStageArea(string stateDirectory,Guid installation,IReadOnlyList<CurrentCatalogStageClaim> claims,StorageBudgetManager budget)
    {
        ArgumentNullException.ThrowIfNull(claims);ArgumentNullException.ThrowIfNull(budget);
        var state=Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        if(!Path.IsPathFullyQualified(stateDirectory) || state==Path.GetPathRoot(state) || installation==Guid.Empty || claims.Count>CurrentCatalogStageJournal.MaximumStages || claims.Select(c=>c.Id).Distinct().Count()!=claims.Count)
            throw new ArgumentException("A dedicated leased installation and bounded complete claims are required.");
        CheckState(state,installation);_root=Path.Combine(state,Name);_installation=installation;_claims=claims.ToDictionary(claim=>claim.Id,CurrentCatalogStageJournal.Identity);
        foreach(var claim in claims.Where(claim=>claim.Phase=="retiring")) _retiring.Add(claim.Id);
        using var reservation=budget.Reserve(65536);reservation.RecheckBeforeWrite(reservation.Bytes);
        lock(AccountingGate)
        {
            var existed=Directory.Exists(_root);if(!existed) Directory.CreateDirectory(_root);Regular(_root,true);
            var marker=Path.Combine(_root,"owner.json");if(existed && !File.Exists(marker)) throw new InvalidDataException("Refusing to claim an unmarked current-stage directory.");
            FileStream? lease=null;
            try
            {
                var path=Path.Combine(_root,"owner.lock");if(File.Exists(path)) Regular(path,false);lease=new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                if(!existed)
                {
                    using var output=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    JsonSerializer.Serialize(output,new {format="mpa-current-catalog-stages",major=1,installationId=installation.ToString("D")});output.Flush(true);
                }
                CheckOwner(_root,installation);var inventory=Inventory(_root);
                foreach(var file in inventory)
                {
                    var id=FileIdentity(file);if(id is null) continue;
                    if(!claims.Any(c=>c.Id==id)) throw new InvalidDataException("Current-stage recovery found an unclaimed generated file; nothing was removed.");
                }
                foreach(var claim in claims)
                {
                    var file=IndexPath(claim.Id);
                    if(claim.Descriptor is not null && claim.Phase!="retiring" && (!File.Exists(file) || new FileInfo(file).Length!=claim.Descriptor.Bytes || File.Exists(file+"-journal")))
                        throw new InvalidDataException("A sealed current stage is missing, changed or has an unconfirmed journal.");
                }
                _lease=lease;lease=null;
            }
            finally {lease?.Dispose();}
        }
    }
    public CurrentCatalogStageIndex Open(CurrentCatalogStageClaim claim,StorageBudgetManager budget,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(claim);ArgumentNullException.ThrowIfNull(budget);
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);if(!_claims.TryGetValue(claim.Id,out var identity) || !identity.AsSpan().SequenceEqual(CurrentCatalogStageJournal.Identity(claim))) throw new InvalidOperationException("A durable captured claim must precede native file creation.");
            if(claim.Phase=="retiring" || _retiring.Contains(claim.Id) || !_users.Add(claim.Id)) throw new InvalidOperationException("A current stage needs one live native owner.");
        }
        try {return CurrentCatalogStageIndex.Open(IndexPath(claim.Id),claim,budget,()=>Release(claim.Id),token);}
        catch {Release(claim.Id);throw;}
    }
    private void Release(Guid id)
    {lock(_gate) if(!_users.Remove(id)) throw new InvalidOperationException("Current-stage native ownership was released twice.");}
    /// <summary>Recover a previously claimed preparing file before persisting its
    /// retirement intent. Missing uncreated files need no allocation to be retired.</summary>
    public void RecoverPreparing(CurrentCatalogStageClaim claim,StorageBudgetManager budget,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);
            if(claim.Phase!="preparing" || !_claims.TryGetValue(claim.Id,out var identity) || !identity.AsSpan().SequenceEqual(CurrentCatalogStageJournal.Identity(claim)))
                throw new InvalidOperationException("Recover only the exact known preparing index.");
            token.ThrowIfCancellationRequested();var path=IndexPath(claim.Id);
            if(!File.Exists(path))
            {
                if(Directory.Exists(path) || File.Exists(path+"-journal")) throw new InvalidDataException("Uncreated index has unexplained recovery entries.");
                return;
            }
            using var recovered=Open(claim,budget,token);
        }
    }
    /// <summary>Only after the journal durably marks this inactive claim retiring.
    /// The caller must confirm actual absence before removing the paired claim.</summary>
    public CurrentCatalogStageRetirement RemoveRetiring(CurrentCatalogStageClaim claim,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(claim);if(claim.Phase!="retiring") throw new InvalidOperationException("Mark private stage retirement before unlinking it.");
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);
            if(!_claims.TryGetValue(claim.Id,out var identity) || !identity.AsSpan().SequenceEqual(CurrentCatalogStageJournal.Identity(claim))) throw new InvalidOperationException("Retirement requires the exact durable captured claim.");
            if(_users.Contains(claim.Id)) throw new InvalidOperationException("Join the current-stage index before retiring its file.");
            _retiring.Add(claim.Id); // A stale preparing/sealed record cannot reopen it.
            token.ThrowIfCancellationRequested();var path=IndexPath(claim.Id);
            if(File.Exists(path)) CurrentCatalogStageIndex.VerifyForRemoval(path,claim,token);
            else if(Directory.Exists(path) || File.Exists(path+"-journal")) throw new InvalidDataException("Missing current stage has an unexplained filesystem replacement or journal.");
            lock(AccountingGate)
            {
                Regular(_root,true);if(File.Exists(path+"-journal")) throw new InvalidDataException("Recover a current-stage transaction before retiring it.");
                if(File.Exists(path)) {Regular(path,false);File.Delete(path);}
                if(File.Exists(path) || Directory.Exists(path)) throw new IOException("Current-stage file retirement is unconfirmed.");
            }
            return new(this,claim);
        }
    }
    /// <summary>Apply one bounded COMPLETE durable claim inventory. New claims must
    /// precede files; removed claims require joined users and actual file absence.
    /// Existing captured headers cannot be replaced by a newer nonce or Root.</summary>
    public void SyncClaims(IReadOnlyList<CurrentCatalogStageClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if(claims.Count>CurrentCatalogStageJournal.MaximumStages || claims.Any(claim=>claim.Id==Guid.Empty) || claims.Select(claim=>claim.Id).Distinct().Count()!=claims.Count)
            throw new ArgumentException("A complete bounded durable claim inventory is required.",nameof(claims));
        var next=claims.ToDictionary(claim=>claim.Id,CurrentCatalogStageJournal.Identity);
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);
            if(_users.Any(id=>!next.ContainsKey(id))) throw new InvalidOperationException("A live native stage cannot lose its durable claim.");
            foreach(var pair in next)
                if(_claims.TryGetValue(pair.Key,out var old) && !old.AsSpan().SequenceEqual(pair.Value)) throw new InvalidDataException("A live area cannot retarget a captured stage header.");
            lock(AccountingGate)
            {
                CheckOwner(_root,_installation);
                foreach(var file in Inventory(_root))
                    if(FileIdentity(file) is { } id && !next.ContainsKey(id)) throw new InvalidDataException("A claim cannot be forgotten while its file or journal remains.");
                foreach(var claim in claims)
                    if(claim.Descriptor is not null && claim.Phase!="retiring" && (!File.Exists(IndexPath(claim.Id)) || new FileInfo(IndexPath(claim.Id)).Length!=claim.Descriptor.Bytes || File.Exists(IndexPath(claim.Id)+"-journal")))
                        throw new InvalidDataException("A retained sealed stage lost its complete file.");
            }
            _claims.Clear();foreach(var pair in next) _claims.Add(pair.Key,pair.Value);
            _retiring.RemoveWhere(id=>!next.ContainsKey(id));foreach(var claim in claims.Where(claim=>claim.Phase=="retiring")) _retiring.Add(claim.Id);
        }
    }
    internal void AssertLiveRetirement(string stateDirectory,Guid installation,CurrentCatalogStageClaim expected)
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);
            var comparison=OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if(installation!=_installation || !string.Equals(_root,Path.Combine(Path.GetFullPath(stateDirectory),Name),comparison) || expected.Phase!="retiring" ||
                !_retiring.Contains(expected.Id) || _users.Contains(expected.Id) || !_claims.TryGetValue(expected.Id,out var bytes) || !bytes.AsSpan().SequenceEqual(CurrentCatalogStageJournal.Identity(expected)))
                throw new InvalidOperationException("Retirement receipt lost its exact live area and captured claim.");
            lock(AccountingGate)
            {
                CheckOwner(_root,installation);var path=IndexPath(expected.Id);
                if(File.Exists(path) || Directory.Exists(path) || File.Exists(path+"-journal") || Directory.Exists(path+"-journal")) throw new IOException("Retired current-stage bytes reappeared; retain the claim.");
            }
        }
    }
    public static long MeasureOwnedBytes(string stateDirectory)
    {
        var root=Path.Combine(Path.GetFullPath(stateDirectory),Name);
        lock(AccountingGate)
        {
            if(!Directory.Exists(root)) {if(File.Exists(root)) throw new IOException("Current-stage accounting refuses a replaced directory.");return 0;}
            using var state=Marker(Path.Combine(stateDirectory,"mpa-processor-state.json"));
            if(!Guid.TryParseExact(state.RootElement.GetProperty("installationId").GetString(),"D",out var id) || id==Guid.Empty) throw new InvalidDataException("Current-stage accounting lost its installation.");
            CheckOwner(root,id);long bytes=0;
            foreach(var file in Inventory(root))
            {
                try {bytes=checked(bytes+new FileInfo(file).Length);}
                catch(FileNotFoundException) when(file.EndsWith("-journal",StringComparison.Ordinal)) { /* Joined SQLite finished this temporary journal. */ }
            }
            return bytes;
        }
    }
    internal static void AssertRetired(string stateDirectory,Guid installation,Guid id)
    {
        if(id==Guid.Empty) throw new ArgumentException("Retirement requires its retained identity.",nameof(id));
        CheckState(stateDirectory,installation);var root=Path.Combine(stateDirectory,Name);
        lock(AccountingGate)
        {
            if(!Directory.Exists(root))
            {if(File.Exists(root)) throw new IOException("Current-stage retirement refuses a replaced directory.");return;}
            CheckOwner(root,installation);var lockPath=Path.Combine(root,"owner.lock");Regular(lockPath,false);
            // The area owner must have joined ALL native file leases and released
            // its exclusive lease before the main journal can forget this claim.
            using var lease=new FileStream(lockPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            _=Inventory(root);var path=Path.Combine(root,"stage-"+id.ToString("N")+".sqlite");
            if(File.Exists(path) || Directory.Exists(path) || File.Exists(path+"-journal") || Directory.Exists(path+"-journal"))
                throw new IOException("Current-stage file retirement has not been confirmed; retain its claim.");
        }
    }
    internal static void Regular(string path,bool directory)
    {var value=File.GetAttributes(path);if((value&FileAttributes.ReparsePoint)!=0 || ((value&FileAttributes.Directory)!=0)!=directory) throw new IOException("Current-stage ownership refuses linked or unexpected filesystem entries.");}
    private string IndexPath(Guid id)
    {if(id==Guid.Empty) throw new ArgumentException("A retained stage identity is required.",nameof(id));return Path.Combine(_root,"stage-"+id.ToString("N")+".sqlite");}
    private static Guid? FileIdentity(string path)
    {
        var name=Path.GetFileName(path);if(name is "owner.json" or "owner.lock") return null;
        if(name.EndsWith("-journal",StringComparison.Ordinal)) name=name[..^8];
        if(name.Length!=45 || !name.StartsWith("stage-",StringComparison.Ordinal) || !name.EndsWith(".sqlite",StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[6..38],"N",out var id) || id==Guid.Empty || id.ToString("N")!=name[6..38]) throw new InvalidDataException("Current-stage inventory contains an unknown entry; preserve all files.");
        return id;
    }
    private static string[] Inventory(string root)
    {
        Regular(root,true);var files=Directory.EnumerateFileSystemEntries(root).Take(7).ToArray();if(files.Length>6) throw new InvalidDataException("Current-stage inventory exceeds its bounded claims.");
        foreach(var file in files)
        {
            _=FileIdentity(file);
            try {Regular(file,false);}
            catch(FileNotFoundException) when(file.EndsWith("-journal",StringComparison.Ordinal)) { }
        }
        return files;
    }
    private static void CheckState(string path,Guid installation)
    {Regular(path,true);using var marker=Marker(Path.Combine(path,"mpa-processor-state.json"));if(marker.RootElement.GetProperty("installationId").GetString()!=installation.ToString("D")) throw new InvalidDataException("Current stage belongs to another installation.");}
    private static void CheckOwner(string root,Guid installation)
    {
        Regular(root,true);using var marker=Marker(Path.Combine(root,"owner.json"));var value=marker.RootElement;
        if(value.GetProperty("format").GetString()!="mpa-current-catalog-stages" || value.GetProperty("major").GetInt32()!=1 || value.GetProperty("installationId").GetString()!=installation.ToString("D")) throw new InvalidDataException("Current-stage marker does not match its installation.");
    }
    private static JsonDocument Marker(string path)
    {Regular(path,false);using var file=File.OpenRead(path);if(file.Length is <1 or >4096) throw new InvalidDataException("Current-stage marker exceeds its bound.");return JsonDocument.Parse(file);}
    public void Dispose()
    {
        lock(_gate)
        {
            if(_closed) return;if(_users.Count!=0) throw new InvalidOperationException("Join all current-stage native indexes before releasing their area.");
            _lease?.Dispose();_lease=null;_closed=true; // Claims, indexes and markers deliberately survive Exit.
        }
    }
}

/// <summary>Process-local proof of actual per-index retirement while the SAME area
/// remains exclusively leased. It cannot refresh Root authority or survive Exit.</summary>
public sealed class CurrentCatalogStageRetirement
{
    private readonly CurrentCatalogStageArea _owner;
    private readonly CurrentCatalogStageClaim _claim;
    internal CurrentCatalogStageRetirement(CurrentCatalogStageArea owner,CurrentCatalogStageClaim claim) {_owner=owner;_claim=claim;}
    internal void AssertFor(string stateDirectory,Guid installation,CurrentCatalogStageClaim expected)
    {
        if(expected!=_claim) throw new InvalidOperationException("A retirement receipt cannot replace its captured claim.");
        _owner.AssertLiveRetirement(stateDirectory,installation,expected);
    }
}
