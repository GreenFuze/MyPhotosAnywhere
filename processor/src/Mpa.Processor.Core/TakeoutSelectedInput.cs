using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mpa.Processor.Core;

public enum TakeoutEntryKind { Media, JsonSidecar, Other }
public sealed record TakeoutInputBinding(Guid PartId,string Kind,string Location,[property:JsonNumberHandling(JsonNumberHandling.WriteAsString|JsonNumberHandling.AllowReadingFromString)]long? Bytes,string? Sha256);
public sealed record TakeoutCapturedEntry(Guid PartId,string Key,string RelativePath,long Bytes,TakeoutEntryKind Kind,int Ordinal,long SourceStamp,Guid MediaId);

/// <summary>A logical entry under one explicitly selected part. Names are never
/// used as an extraction destination. Distinct entries never merge by name/hash.</summary>
public sealed class TakeoutInputEntry
{
    internal TakeoutSelectedInput Owner { get; }
    internal object Native { get; }
    internal long SourceStamp { get; }
    internal int Ordinal { get; }
    public Guid PartId { get; }
    public string Key { get; }
    public string RelativePath { get; }
    public long Bytes { get; }
    public TakeoutEntryKind Kind { get; }
    internal TakeoutInputEntry(TakeoutSelectedInput owner,object native,string relative,long bytes,string discriminator,long sourceStamp=0,int ordinal=0)
    {
        Owner=owner;Native=native;PartId=owner.PartId;RelativePath=relative;Bytes=bytes;SourceStamp=sourceStamp;Ordinal=ordinal;
        Key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PartId.ToString("D")+"\n"+discriminator+"\n"+relative)));
        var extension=Path.GetExtension(relative).ToLowerInvariant();
        Kind=extension==".json"?TakeoutEntryKind.JsonSidecar:TakeoutMediaNames.Contains(extension)?TakeoutEntryKind.Media:TakeoutEntryKind.Other;
    }
    private static readonly HashSet<string> TakeoutMediaNames=new(StringComparer.Ordinal)
    {".jpg",".jpeg",".png",".gif",".webp",".heic",".heif",".avif",".tif",".tiff",".bmp",".dng",".raw",".cr2",".cr3",".nef",".arw",".raf",".rw2",".orf",".pef",".srw",".mp4",".mov",".m4v",".mkv",".webm",".avi",".3gp",".3g2",".mpg",".mpeg",".mts",".m2ts",".wmv"};
}

/// <summary>One read-only selected folder or physically held local ZIP snapshot.
/// Drive ZIPs enter only after a separate verified download stages a local file.
/// This owner has no provider credentials, source writes or deletion capability.</summary>
public abstract class TakeoutSelectedInput : IDisposable
{
    internal const long MaximumEntryBytes=256L*1024*1024*1024;
    private readonly object _gate=new();
    private bool _reading;
    private bool _enumerating;
    private bool _closed;
    public Guid PartId { get; }
    public abstract TakeoutInputBinding Binding { get; }
    protected TakeoutSelectedInput(Guid partId)
    {if(partId==Guid.Empty)throw new ArgumentException("A persisted selected-part identity is required.",nameof(partId));PartId=partId;}
    public IEnumerable<TakeoutInputEntry> Enumerate(CancellationToken token=default)
    {
        lock(_gate){ObjectDisposedException.ThrowIf(_closed,this);if(_enumerating||_reading)throw new InvalidOperationException("Join the selected input's current native enumeration/read first.");_enumerating=true;}
        try
        {
            var count=0;
            foreach(var entry in EnumerateOwned(token))
            {
                token.ThrowIfCancellationRequested();if(++count>TakeoutZipDirectoryBounds.MaximumEntries)throw new InvalidDataException("Selected Takeout part exceeds its bounded inventory; preserve it for explicit split/import recovery.");
                yield return entry;
            }
        }
        finally {lock(_gate)_enumerating=false;}
    }
    protected abstract IEnumerable<TakeoutInputEntry> EnumerateOwned(CancellationToken token);
    public Stream OpenRead(TakeoutInputEntry entry,long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);
            if(!ReferenceEquals(entry.Owner,this))throw new InvalidOperationException("Selected entry belongs to another original input owner.");
            if(maximumBytes<1||maximumBytes>MaximumEntryBytes||entry.Bytes>maximumBytes)throw new InvalidDataException("Takeout entry exceeds this stage's explicit byte budget.");
            if(_reading)throw new InvalidOperationException("Join the selected input's previous native read before opening another.");
            _reading=true;
        }
        try
        {
            var (stream,crc)=OpenOwned(entry);
            return new TakeoutInputReadStream(stream,entry.Bytes,crc,()=>{lock(_gate)_reading=false;});
        }
        catch {lock(_gate)_reading=false;throw;}
    }
    protected abstract (Stream Stream,uint? Crc) OpenOwned(TakeoutInputEntry entry);
    protected abstract object RestoreNative(TakeoutCapturedEntry captured);
    internal TakeoutInputEntry Restore(TakeoutCapturedEntry captured)
    {
        ArgumentNullException.ThrowIfNull(captured);lock(_gate)ObjectDisposedException.ThrowIf(_closed,this);
        if(captured.PartId!=PartId||captured.Bytes is <0 or >MaximumEntryBytes||captured.Ordinal<0||captured.SourceStamp<0)throw new InvalidDataException("Captured Takeout input changed its selected part or bounds.");
        var path=LogicalPath(captured.RelativePath);var entry=new TakeoutInputEntry(this,RestoreNative(captured),path,captured.Bytes,captured.Ordinal==0?"folder":captured.Ordinal.ToString(CultureInfo.InvariantCulture),captured.SourceStamp,captured.Ordinal);
        if(entry.Key!=captured.Key||entry.Kind!=captured.Kind)throw new InvalidDataException("Captured Takeout entry changed its immutable identity or kind.");return entry;
    }
    protected abstract void DisposeOwned();
    public void Dispose()
    {lock(_gate){if(_closed)return;if(_reading||_enumerating)throw new InvalidOperationException("Join all selected Takeout entry readers/enumerators before releasing the source.");DisposeOwned();_closed=true;}GC.SuppressFinalize(this);}

    public static TakeoutSelectedInput OpenFolder(Guid partId,string selectedAbsoluteDirectory)=>new FolderInput(partId,selectedAbsoluteDirectory);
    /// <summary>Capture an explicitly chosen local archive, then open the same exact
    /// read-only snapshot through the normal bounded ZIP owner. No extraction.</summary>
    public static async Task<TakeoutSelectedInput> OpenZipAsync(Guid partId,string selectedAbsolutePath,CancellationToken token)
    {
        if(partId==Guid.Empty||!Path.IsPathFullyQualified(selectedAbsolutePath))throw new ArgumentException("Choose a local ZIP with a selected-part identity.");
        token.ThrowIfCancellationRequested();Regular(selectedAbsolutePath,false);
        await using var input=new FileStream(selectedAbsolutePath,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        var bytes=input.Length;
        if(bytes is <22 or >TakeoutZipDirectoryBounds.MaximumArchiveBytes)throw new InvalidDataException("Selected ZIP exceeds the supported archive bounds.");
        var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token).ConfigureAwait(false));
        return await OpenZipAsync(partId,selectedAbsolutePath,bytes,hash,token).ConfigureAwait(false);
    }
    public static async Task<TakeoutSelectedInput> OpenZipAsync(Guid partId,string stagedAbsolutePath,long expectedBytes,string expectedSha256,CancellationToken token)
    {
        if(partId==Guid.Empty||!Path.IsPathFullyQualified(stagedAbsolutePath)||expectedBytes is <22 or >TakeoutZipDirectoryBounds.MaximumArchiveBytes||expectedSha256 is not {Length:64}||expectedSha256.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f')))
            throw new ArgumentException("A captured local ZIP snapshot and exact SHA/length are required.");
        Regular(stagedAbsolutePath,false);var input=new FileStream(stagedAbsolutePath,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.RandomAccess);
        try
        {
            if(input.Length!=expectedBytes||Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token).ConfigureAwait(false))!=expectedSha256)throw new InvalidDataException("Selected ZIP snapshot changed its captured bytes.");
            var count=TakeoutZipDirectoryBounds.Verify(input,token);return new ZipInput(partId,input,count,expectedSha256);
        }
        catch {await input.DisposeAsync().ConfigureAwait(false);throw;}
    }
    internal static string LogicalPath(string path,bool directory=false)
    {
        if(path.Length is <1 or >2048||path.Contains('\\')||path[0]=='/'||path.Any(char.IsControl))throw new InvalidDataException("Takeout contains an unsafe logical path.");
        if(directory&&path.EndsWith('/'))path=path[..^1];
        var parts=path.Split('/');if(parts.Length>256||parts.Any(p=>p.Length is <1 or >255||p is "." or ".."||p.Contains(':')||p.EndsWith('.')||p.EndsWith(' ')||Reserved(p)))throw new InvalidDataException("Takeout contains an unsafe or nonportable path component.");
        try {_=new UTF8Encoding(false,true).GetByteCount(path);}
        catch(EncoderFallbackException error){throw new InvalidDataException("Takeout path contains an invalid Unicode scalar sequence.",error);}
        return path;
    }
    private static bool Reserved(string name)
    {
        var stem=name.Split('.')[0].ToUpperInvariant();return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"||stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&stem[3] is >='1' and <='9';
    }
    private static void Regular(string path,bool directory)
    {var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0||((attributes&FileAttributes.Directory)!=0)!=directory)throw new IOException("Takeout original input crosses a link or unexpected entry.");}

    private sealed class FolderInput : TakeoutSelectedInput
    {
        private readonly string _root;
        private int _nodes;
        public override TakeoutInputBinding Binding=>new(PartId,"folder",_root,null,null);
        internal FolderInput(Guid id,string selected):base(id)
        {
            if(!Path.IsPathFullyQualified(selected))throw new ArgumentException("An explicitly selected absolute Takeout directory is required.",nameof(selected));
            _root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));if(_root==Path.GetPathRoot(_root))throw new ArgumentException("Select a dedicated Takeout directory.",nameof(selected));Regular(_root,true);
        }
        protected override IEnumerable<TakeoutInputEntry> EnumerateOwned(CancellationToken token)
        {_nodes=0;return Walk(_root,0,token);}
        private IEnumerable<TakeoutInputEntry> Walk(string directory,int depth,CancellationToken token)
        {
            if(depth>256)throw new InvalidDataException("Takeout folder exceeds its traversal depth bound.");Regular(directory,true);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();if(++_nodes>TakeoutZipDirectoryBounds.MaximumEntries)throw new InvalidDataException("Takeout folder exceeds its bounded file/directory inventory.");var attributes=File.GetAttributes(path);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Takeout enumeration refuses links; no original was opened or removed.");
                var isDirectory=(attributes&FileAttributes.Directory)!=0;var relative=LogicalPath(Path.GetRelativePath(_root,path).Replace(Path.DirectorySeparatorChar,'/'));
                if(isDirectory){foreach(var entry in Walk(path,depth+1,token))yield return entry;}
                else
                {
                    var file=new FileInfo(path);if(file.Length>MaximumEntryBytes)throw new InvalidDataException("Takeout original exceeds its per-entry bound.");
                    yield return new(this,path,relative,file.Length,"folder",file.LastWriteTimeUtc.Ticks);
                }
            }
        }
        protected override (Stream Stream,uint? Crc) OpenOwned(TakeoutInputEntry entry)
        {
            var path=(string)entry.Native;Regular(_root,true);var relative=LogicalPath(entry.RelativePath);var parts=relative.Split('/');var current=_root;
            for(var i=0;i<parts.Length;i++){current=Path.Combine(current,parts[i]);Regular(current,i<parts.Length-1);}
            if(current!=path)throw new InvalidDataException("Takeout folder entry lost its exact selected path.");
            var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
            try
            {
                if(stream.Length!=entry.Bytes||File.GetLastWriteTimeUtc(path).Ticks!=entry.SourceStamp)throw new InvalidDataException("Takeout original changed since its inventory; keep its previous captured work.");
                return(stream,null);
            }
            catch {stream.Dispose();throw;}
        }
        protected override object RestoreNative(TakeoutCapturedEntry captured)
        {if(captured.Ordinal!=0)throw new InvalidDataException("A captured folder entry cannot acquire a ZIP ordinal.");return Path.Combine(_root,captured.RelativePath.Replace('/',Path.DirectorySeparatorChar));}
        protected override void DisposeOwned(){ }
    }
    private sealed class ZipInput : TakeoutSelectedInput
    {
        private readonly FileStream _input;
        private readonly ZipArchive _archive;
        private readonly string _sha256;
        public override TakeoutInputBinding Binding=>new(PartId,"zip",_input.Name,_input.Length,_sha256);
        internal ZipInput(Guid id,FileStream input,int count,string sha256):base(id)
        {
            _input=input;_sha256=sha256;ZipArchive? archive=null;
            try
            {
                archive=new(input,ZipArchiveMode.Read,leaveOpen:true);
                if(archive.Entries.Count!=count)throw new InvalidDataException("Takeout ZIP physical and managed inventories disagree.");
                _archive=archive;
            }
            catch {archive?.Dispose();throw;}
        }
        protected override IEnumerable<TakeoutInputEntry> EnumerateOwned(CancellationToken token)
        {
            var number=0;long total=0;
            foreach(var entry in _archive.Entries)
            {
                token.ThrowIfCancellationRequested();number++;var directory=entry.FullName.EndsWith('/');var path=LogicalPath(entry.FullName,directory);
                var mode=(entry.ExternalAttributes>>16)&0xf000;
                if(mode is not(0 or 0x8000 or 0x4000)||(entry.ExternalAttributes&(int)FileAttributes.ReparsePoint)!=0||mode==0x4000&&!directory||directory&&entry.Length!=0)
                    throw new InvalidDataException("Takeout ZIP contains a link or unsupported native entry type.");
                if(entry.Length>MaximumEntryBytes||entry.CompressedLength<0||entry.CompressedLength>_input.Length||entry.Length>Math.Max(1,entry.CompressedLength)*10000)
                    throw new InvalidDataException("Takeout ZIP entry exceeds its explicit size/expansion bounds.");
                total=checked(total+entry.Length);if(total>TakeoutZipDirectoryBounds.MaximumArchiveBytes)throw new InvalidDataException("Takeout ZIP total declared expansion exceeds its per-part bound.");
                if(!directory)yield return new(this,entry,path,entry.Length,number.ToString(CultureInfo.InvariantCulture),ordinal:number);
            }
        }
        protected override (Stream Stream,uint? Crc) OpenOwned(TakeoutInputEntry entry)
        {var native=(ZipArchiveEntry)entry.Native;if(native.Length!=entry.Bytes)throw new InvalidDataException("Takeout ZIP entry changed its captured length.");return(native.Open(),native.Crc32);}
        protected override object RestoreNative(TakeoutCapturedEntry captured)
        {
            if(captured.Ordinal<1||captured.Ordinal>_archive.Entries.Count)throw new InvalidDataException("Captured ZIP ordinal is unavailable.");var entry=_archive.Entries[captured.Ordinal-1];
            if(entry.FullName!=captured.RelativePath||entry.Length!=captured.Bytes)throw new InvalidDataException("Captured ZIP entry changed its exact path or bytes.");return entry;
        }
        protected override void DisposeOwned(){_archive.Dispose();_input.Dispose();}
    }
}

/// <summary>One physical input reader, never a writer. Async disposal joins an
/// outstanding read before returning the source lease; partial reads are not a
/// verified snapshot. CRC is checked at EOF for ZIP, length for every input.</summary>
internal sealed class TakeoutInputReadStream(Stream input,long expected,uint? expectedCrc,Action release) : Stream
{
    private readonly object _gate=new();
    private readonly SemaphoreSlim _read=new(1,1);
    private Task? _disposal;
    private bool _closing;
    private bool _closed;
    private bool _failed;
    private bool _verifiedEof;
    private long _position;
    private uint _crc=uint.MaxValue;
    private static readonly uint[] CrcTable=BuildCrcTable();
    public override bool CanRead=>!_closing;
    public override bool CanSeek=>false;
    public override bool CanWrite=>false;
    public override long Length=>expected;
    public override long Position {get=>_position;set=>throw new NotSupportedException();}
    public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
    public override int Read(Span<byte> buffer)
    {
        lock(_gate){ObjectDisposedException.ThrowIf(_closing,this);if(!_read.Wait(0))throw new InvalidOperationException("Join the pending Takeout read before another synchronous read.");}
        try {return Observe(buffer,input.Read(buffer[..Bound(buffer.Length)]));}
        catch {_failed=true;throw;}
        finally {_read.Release();}
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
    {
        lock(_gate)ObjectDisposedException.ThrowIf(_closing,this);
        await _read.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock(_gate)ObjectDisposedException.ThrowIf(_closing,this);
            cancellationToken.ThrowIfCancellationRequested();var count=await input.ReadAsync(buffer[..Bound(buffer.Length)],cancellationToken).ConfigureAwait(false);
            return Observe(buffer.Span,count);
        }
        catch {_failed=true;throw;}
        finally {_read.Release();}
    }
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken)=>ReadAsync(buffer.AsMemory(offset,count),cancellationToken).AsTask();
    private int Bound(int count)
    {if(_failed)throw new InvalidDataException("Takeout read failed; close it before retrying the exact input.");return(int)Math.Min(count,Math.Max(1,expected-_position+1));}
    private int Observe(ReadOnlySpan<byte> bytes,int count)
    {
        if(count==0&&bytes.Length==0)return 0;
        _position=checked(_position+count);if(_position>expected)throw new InvalidDataException("Takeout input expanded beyond its captured length.");
        if(expectedCrc is not null)foreach(var value in bytes[..count])_crc=CrcTable[(_crc^value)&255]^(_crc>>8);
        if(count==0&&(_position!=expected||expectedCrc is not null&&~_crc!=expectedCrc.Value))throw new InvalidDataException("Takeout input is truncated or its ZIP CRC changed.");
        if(count==0)_verifiedEof=true;
        return count;
    }
    // Only the native original owner may replay a fully verified physical file.
    // Public seek stays unavailable; a ZIP entry must reopen under its held archive.
    internal bool TryRewindVerified()
    {
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closing,this);
            if(!_read.Wait(0))throw new InvalidOperationException("Join the original read before replaying it.");
            try
            {
                if(_failed||!_verifiedEof)throw new InvalidOperationException("Only a complete verified input may replay.");
                if(!input.CanSeek)return false;
                if(input.Seek(0,SeekOrigin.Begin)!=0)throw new IOException("The held original could not rewind.");
                _position=0;_crc=uint.MaxValue;_verifiedEof=false;return true;
            }
            catch {_failed=true;throw;}
            finally {_read.Release();}
        }
    }
    protected override void Dispose(bool disposing)
    {
        if(!disposing)return;
        lock(_gate)
        {
            if(_closed)return;if(_disposal is not null||!_read.Wait(0))throw new InvalidOperationException("Await the actual Takeout read/disposal before releasing its source.");
            try {_closing=true;input.Dispose();release();_closed=true;}finally {_read.Release();}
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        Task pending;
        lock(_gate){_closing=true;pending=_closed?Task.CompletedTask:_disposal??=DisposeJoinedAsync();}
        await pending.ConfigureAwait(false);await base.DisposeAsync().ConfigureAwait(false);GC.SuppressFinalize(this);
    }
    private async Task DisposeJoinedAsync()
    {
        await _read.WaitAsync().ConfigureAwait(false);
        try {await input.DisposeAsync().ConfigureAwait(false);release();lock(_gate)_closed=true;}
        finally {_read.Release();}
    }
    private static uint[] BuildCrcTable()
    {var table=new uint[256];for(uint i=0;i<256;i++){var value=i;for(var bit=0;bit<8;bit++)value=(value&1)!=0?0xedb88320^(value>>1):value>>1;table[i]=value;}return table;}
    public override void Flush()=>throw new NotSupportedException();
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
