namespace Mpa.Processor.Core;

/// <summary>Owns write admission through the caller's commit. The value is a policy
/// result, never a current graph or original-write grant. Borrowed transactions are
/// committed/rolled back by their owner; mutation reservations retire afterward.</summary>
internal sealed class LibraryPolicySelection<T>(T value,StorageReservation? reservation) : IDisposable where T:class
{
    private StorageReservation? _reservation=reservation;
    private int _disposed;
    public T Value {get;}=value ?? throw new ArgumentNullException(nameof(value));
    public void Recheck()
    {ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);var owned=_reservation;owned?.RecheckBeforeWrite(owned.Bytes);}
    public void Dispose()
    {if(Interlocked.Exchange(ref _disposed,1)==0) Interlocked.Exchange(ref _reservation,null)?.Dispose();}
}
