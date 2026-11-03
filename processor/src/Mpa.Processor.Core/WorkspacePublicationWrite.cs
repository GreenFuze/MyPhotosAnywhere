namespace Mpa.Processor.Core;

/// <summary>Owns a local outbox write's storage reservation through its caller's
/// actual commit. No current observation or cloud grant can escape this owner.</summary>
internal sealed class WorkspacePublicationWrite(WorkspacePublicationRecord value,StorageReservation? storage) : IDisposable
{
    private StorageReservation? _storage=storage;
    private int _disposed;
    internal WorkspacePublicationRecord Value {get;}=value ?? throw new ArgumentNullException(nameof(value));
    internal void Recheck()
    {ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);var owned=_storage;owned?.RecheckBeforeWrite(owned.Bytes);}
    public void Dispose()
    {if(Interlocked.Exchange(ref _disposed,1)==0) Interlocked.Exchange(ref _storage,null)?.Dispose();}
}
