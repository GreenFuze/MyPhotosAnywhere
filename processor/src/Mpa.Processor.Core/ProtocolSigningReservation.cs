namespace Mpa.Processor.Core;

/// <summary>Owns signing-intent write admission through a borrowing transaction's
/// commit. Reserving a body is neither a native signature nor cloud write authority.</summary>
internal sealed class ProtocolSigningReservation(ProtocolSigningIntent value,StorageReservation? reservation) : IDisposable
{
    private StorageReservation? _reservation=reservation;
    private int _disposed;
    internal ProtocolSigningIntent Value {get;}=value ?? throw new ArgumentNullException(nameof(value));
    internal void Recheck()
    {ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);var owned=_reservation;owned?.RecheckBeforeWrite(owned.Bytes);}
    public void Dispose()
    {if(Interlocked.Exchange(ref _disposed,1)==0) Interlocked.Exchange(ref _reservation,null)?.Dispose();}
}
