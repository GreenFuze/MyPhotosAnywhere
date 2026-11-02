using System.Security.Cryptography;

namespace Mpa.Processor.Core;

/// <summary>Actual complete-source hash under a retained read-only native owner.
/// This is input evidence, never a provider permission or a backup receipt.</summary>
public sealed class TakeoutOriginalCapture
{
    public TakeoutCompletedInventory Inventory {get;}
    public TakeoutCapturedEntry Entry {get;}
    public string Sha256 {get;}
    public string Name {get;}
    public string MimeType {get;}
    internal TakeoutOriginalCapture(TakeoutCompletedInventory inventory,TakeoutCapturedEntry entry,string sha256)
    {
        Inventory=inventory;Entry=entry;Sha256=sha256;Name=entry.RelativePath[(entry.RelativePath.LastIndexOf('/')+1)..];
        MimeType=Path.GetExtension(Name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg"=>"image/jpeg",".png"=>"image/png",".gif"=>"image/gif",".webp"=>"image/webp",
            ".heic"=>"image/heic",".heif"=>"image/heif",".avif"=>"image/avif",".tif" or ".tiff"=>"image/tiff",".bmp"=>"image/bmp",
            ".mp4" or ".m4v"=>"video/mp4",".mov"=>"video/quicktime",".webm"=>"video/webm",".mkv"=>"video/x-matroska",
            ".avi"=>"video/x-msvideo",".mpeg" or ".mpg"=>"video/mpeg",".3gp"=>"video/3gpp",".3g2"=>"video/3gpp2",
            _=>"application/octet-stream"
        };
    }
}

/// <summary>Owns the original from hashing through transfer. Bounded sequential
/// replay hashes every byte again, including bytes skipped to a server offset.
/// Disposal joins physical reads before releasing the folder file or ZIP.</summary>
public sealed class TakeoutOriginalReader : IAsyncDisposable
{
    public const int MaximumReadBytes=1024*1024;
    private readonly object _gate=new();
    private readonly SemaphoreSlim _reading=new(1,1);
    private readonly TakeoutSelectedInput _source;
    private readonly Stream _input;
    private readonly IncrementalHash _hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _closing;
    private bool _failed;
    private bool _verified;
    private Task? _disposal;
    public TakeoutOriginalCapture Capture {get;}
    public long Position=>_input.Position;
    public bool ReplayVerified {get {lock(_gate)return _verified;}}
    private TakeoutOriginalReader(TakeoutSelectedInput source,Stream input,TakeoutOriginalCapture capture)
    {_source=source;_input=input;Capture=capture;}

    public static async Task<TakeoutOriginalReader> CaptureAsync(TakeoutCompletedInventory completed,TakeoutAssociationCatalog catalog,
        TakeoutCapturedEntry entry,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(completed);ArgumentNullException.ThrowIfNull(catalog);ArgumentNullException.ThrowIfNull(entry);
        token.ThrowIfCancellationRequested();catalog.AssertCompletedSelection(completed);
        if(entry.Kind!=TakeoutEntryKind.Media||entry.Bytes is <1 or >TakeoutSelectedInput.MaximumEntryBytes)
            throw new InvalidDataException("Select one nonempty captured original media item.");
        var part=completed.Parts.SingleOrDefault(value=>value.PartId==entry.PartId)??throw new InvalidDataException("Original is outside its captured input selection.");
        var source=part.Kind switch
        {
            "folder"=>TakeoutSelectedInput.OpenFolder(part.PartId,part.Location),
            "zip"=>await TakeoutSelectedInput.OpenZipAsync(part.PartId,part.Location,part.Bytes!.Value,part.Sha256!,token).ConfigureAwait(false),
            _=>throw new InvalidDataException("Unknown captured original part.")
        };
        Stream? input=null;
        try
        {
            input=catalog.OpenCaptured(source,entry,TakeoutSelectedInput.MaximumEntryBytes);
            var sha=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token).ConfigureAwait(false));token.ThrowIfCancellationRequested();
            if(input is not TakeoutInputReadStream physical)throw new InvalidOperationException("Original lost its native reader.");
            if(!physical.TryRewindVerified())
            {
                await input.DisposeAsync().ConfigureAwait(false);input=null;
                token.ThrowIfCancellationRequested();input=catalog.OpenCaptured(source,entry,TakeoutSelectedInput.MaximumEntryBytes);
            }
            token.ThrowIfCancellationRequested();return new(source,input,new(completed,entry,sha));
        }
        catch
        {
            if(input is not null)await input.DisposeAsync().ConfigureAwait(false);
            source.Dispose();throw;
        }
    }
    public async ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken token)
    {
        if(bytes.Length is <1 or >MaximumReadBytes)throw new ArgumentException("Original reads must use a bounded nonempty buffer.",nameof(bytes));
        lock(_gate)ObjectDisposedException.ThrowIf(_closing,this);
        token.ThrowIfCancellationRequested();
        await _reading.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock(_gate){ObjectDisposedException.ThrowIf(_closing,this);if(_failed)throw new InvalidOperationException("A failed original reader must retire before retry.");}
            token.ThrowIfCancellationRequested();var count=await _input.ReadAsync(bytes,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            if(count!=0)_hash.AppendData(bytes.Span[..count]);
            else
            {
                lock(_gate)
                {
                    if(!_verified)
                    {
                        if(_input.Position!=Capture.Entry.Bytes||Convert.ToHexStringLower(_hash.GetHashAndReset())!=Capture.Sha256)
                            throw new InvalidDataException("Original content changed between capture and transfer; no backup receipt is admitted.");
                        _verified=true;
                    }
                }
            }
            return count;
        }
        catch {lock(_gate)_failed=true;throw;}
        finally {_reading.Release();}
    }
    public ValueTask DisposeAsync()
    {lock(_gate){_closing=true;return new(_disposal??=DisposeJoinedAsync());}}
    private async Task DisposeJoinedAsync()
    {
        await _reading.WaitAsync().ConfigureAwait(false);
        try {await _input.DisposeAsync().ConfigureAwait(false);_source.Dispose();_hash.Dispose();}
        finally {_reading.Release();}
    }
}
