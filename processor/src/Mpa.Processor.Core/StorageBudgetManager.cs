namespace Mpa.Processor.Core;

public sealed record StorageSpaceSnapshot(long CapacityBytes, long AvailableBytes, long OwnedCacheBytes);
public interface IStorageSpaceReader { StorageSpaceSnapshot Read(); }
public sealed record StorageBudgetStatus(long ReserveBytes, long CacheLimitBytes, long ReservedBytes, long ReservableBytes);
public sealed class InsufficientStorageBudgetException(StorageBudgetStatus status) : IOException("Storage budget cannot safely reserve more bytes; keep checkpoints and select safe cleanup or another local cache volume.")
{
    public StorageBudgetStatus Status { get; } = status;
}

/// <summary>Owns reservations only; never deletes files. The scoped cache index supplies measured
/// cache use. Recheck before each write/checkpoint because other applications can consume disk space.</summary>
public sealed class StorageBudgetManager
{
    public const long Gibibyte = 1024L * 1024 * 1024;
    private readonly IStorageSpaceReader _reader;
    private readonly long _cacheLimit;
    private readonly long _minimumReserve;
    private readonly int _reservePercent;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, long> _reservations = [];
    private long _reserved;

    public StorageBudgetManager(IStorageSpaceReader reader, long cacheLimitBytes = 10 * Gibibyte, long minimumReserveBytes = 2 * Gibibyte, int reservePercent = 5)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheLimitBytes, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumReserveBytes, 2 * Gibibyte);
        ArgumentOutOfRangeException.ThrowIfNegative(reservePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reservePercent, 20);
        _reader = reader; _cacheLimit = cacheLimitBytes; _minimumReserve = minimumReserveBytes; _reservePercent = reservePercent;
    }

    public StorageBudgetStatus Status()
    {
        lock (_gate) return ReadStatus();
    }

    public StorageReservation Reserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1L);
        lock (_gate)
        {
            var status = ReadStatus();
            if (bytes > status.ReservableBytes) throw new InsufficientStorageBudgetException(status);
            var id = Guid.NewGuid();
            _reservations.Add(id, bytes);
            _reserved = checked(_reserved + bytes);
            return new StorageReservation(this, id, bytes);
        }
    }

    /// <summary>Validate current free-space headroom for an already owned reservation; excludes
    /// its own remaining allocation but includes every other outstanding writer reservation.</summary>
    internal void Recheck(Guid id, long remainingBytes)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(id, out var original) || remainingBytes < 0 || remainingBytes > original)
                throw new InvalidOperationException("Storage reservation accounting invariant failed.");
            var current = ReadSnapshot();
            var reserve = ReserveFor(current.CapacityBytes);
            var other = _reserved - original;
            var limit = Math.Min(_cacheLimit, Math.Max(0, current.CapacityBytes - reserve));
            var cacheRemaining = Math.Max(0, limit - current.OwnedCacheBytes - other);
            if (current.AvailableBytes < reserve || remainingBytes > Math.Max(0, current.AvailableBytes - reserve - other) || remainingBytes > cacheRemaining)
                throw new InsufficientStorageBudgetException(ReadStatus());
        }
    }

    internal void Release(Guid id)
    {
        lock (_gate)
        {
            if (!_reservations.Remove(id, out var bytes)) throw new InvalidOperationException("Storage reservation was released without ownership.");
            _reserved = checked(_reserved - bytes);
            if (_reserved < 0) throw new InvalidOperationException("Negative reserved storage.");
        }
    }

    private StorageBudgetStatus ReadStatus()
    {
        var snapshot = ReadSnapshot();
        var reserve = ReserveFor(snapshot.CapacityBytes);
        var limit = Math.Min(_cacheLimit, Math.Max(0, snapshot.CapacityBytes - reserve));
        var availableCache = snapshot.OwnedCacheBytes >= limit ? 0 : limit - snapshot.OwnedCacheBytes;
        var availableDisk = snapshot.AvailableBytes >= reserve ? snapshot.AvailableBytes - reserve : 0;
        var usable = Math.Min(availableCache, availableDisk);
        return new(reserve, limit, _reserved, usable >= _reserved ? usable - _reserved : 0);
    }

    private StorageSpaceSnapshot ReadSnapshot()
    {
        var snapshot = _reader.Read();
        if (snapshot.CapacityBytes < 1 || snapshot.AvailableBytes < 0 || snapshot.AvailableBytes > snapshot.CapacityBytes ||
            snapshot.OwnedCacheBytes < 0 || snapshot.OwnedCacheBytes > snapshot.CapacityBytes)
            throw new InvalidDataException("Storage capacity/cache measurement is unavailable or inconsistent.");
        return snapshot;
    }

    private long ReserveFor(long capacity)
    {
        var whole = capacity / 100 * _reservePercent;
        var remainder = capacity % 100 * _reservePercent;
        return Math.Max(_minimumReserve, whole + remainder / 100 + (remainder % 100 == 0 ? 0 : 1));
    }
}

/// <summary>RAII owner for one reservation. Dispose is idempotent; a disposed lease cannot authorize writes.</summary>
public sealed class StorageReservation : IDisposable
{
    private StorageBudgetManager? _owner;
    private readonly Guid _id;
    public long Bytes { get; }
    internal StorageReservation(StorageBudgetManager owner, Guid id, long bytes) { _owner = owner; _id = id; Bytes = bytes; }
    public void RecheckBeforeWrite(long remainingBytes) => (Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(StorageReservation))).Recheck(_id, remainingBytes);
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_id);
}
