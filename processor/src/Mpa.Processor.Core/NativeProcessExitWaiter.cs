using System.Diagnostics;
using System.Globalization;

namespace Mpa.Processor.Core;

/// <summary>Owns one actual blocking process-wait thread, not the borrowed Process.
/// Does not use Exited events, a pool wait, cancellation or a cached exit boolean.
/// Completion proves the real wait returned; TryJoin additionally proves the actual
/// thread returned. The process/pipe owner must join both before releasing resources.</summary>
public sealed class NativeProcessExitWaiter
{
    private readonly Process _process;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private bool _startAttempted;
    private bool _started;
    private bool _cleanupStarted;
    private bool _cleanupJoined;
    private int _joined;
    private long _nativeSignalTicks;

    public NativeProcessExitWaiter(Process process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        if (process.EnableRaisingEvents) throw new ArgumentException("Native worker waiting cannot share event-based exit ownership.", nameof(process));
        _thread = new Thread(Wait)
        {
            IsBackground = true,
            Name = "MPA native exit " + process.Id.ToString(CultureInfo.InvariantCulture)
        };
    }

    public Task Completion
    {
        get { return _completion.Task; }
    }
    public bool Started { get { lock (_gate) return _started; } }
    public bool RetirementConfirmed { get { lock (_gate) return _completion.Task.IsCompletedSuccessfully && (_started ? ThreadJoined : _cleanupJoined); } }
    public bool ThreadJoined => Volatile.Read(ref _joined) != 0;
    public long NativeSignalTicks => Interlocked.Read(ref _nativeSignalTicks);

    /// <summary>Called once after the launcher retains cleanup ownership. A failed start cannot be retried
    /// or reported as an exited process; the launcher retains its process cleanup duty.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_startAttempted || _cleanupStarted) throw new InvalidOperationException("A native exit waiter starts exactly once.");
            _startAttempted = true;
            _thread.Start();
            _started = true;
        }
    }

    public bool TryJoin(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate) if (!_started)
        {
            if (_cleanupJoined) return true;
            throw new InvalidOperationException("An unstarted native exit waiter cannot be joined.");
        }
        if (ReferenceEquals(Thread.CurrentThread, _thread)) throw new InvalidOperationException("A native waiter cannot join itself.");
        if (!ThreadJoined)
        {
            if (!_thread.Join(timeout)) return false;
            Interlocked.Exchange(ref _joined, 1);
        }
        // Join even a failed actual wait before propagating its original failure.
        _completion.Task.GetAwaiter().GetResult();
        return true;
    }

    /// <summary>Recovery joins the same original thread without pretending a bounded
    /// stop succeeded. The owning launcher must first terminate its actual child.</summary>
    public void Join()
    {
        lock (_gate) if (!_started)
        {
            if (_cleanupJoined) return;
            throw new InvalidOperationException("An unstarted native exit waiter cannot be joined.");
        }
        if (ReferenceEquals(Thread.CurrentThread, _thread)) throw new InvalidOperationException("A native waiter cannot join itself.");
        if (!ThreadJoined) { _thread.Join(); Interlocked.Exchange(ref _joined, 1); }
        _completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Startup cleanup only: if no wait thread started, the retained launcher
    /// performs a real bounded native wait itself. This never replaces an existing wait
    /// thread, and cannot mark a live child or a timed-out wait as completed.</summary>
    public bool CompleteUnstartedCleanup(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("A started native waiter must join its original thread.");
            _cleanupStarted = true;
            if (_cleanupJoined) return true;
            if (!_process.WaitForExit(timeout)) return false;
            Interlocked.Exchange(ref _nativeSignalTicks, Stopwatch.GetTimestamp());
            _cleanupJoined = true; _completion.SetResult(); return true;
        }
    }

    /// <summary>Actual retirement, including no-thread startup recovery, without
    /// blocking the caller before its first await. Bounded failure preserves ownership;
    /// infinite recovery remains an owned asynchronous task until the child is joined.</summary>
    public async Task WaitForRetirementAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (Started ? TryJoin(TimeSpan.Zero) : CompleteUnstartedCleanup(TimeSpan.Zero)) return;
            var delay = TimeSpan.FromMilliseconds(100);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Actual worker native retirement exceeded its termination deadline.");
                if (remaining < delay) delay = remaining;
            }
            if (_completion.Task.IsCompleted) await Task.Delay(delay).ConfigureAwait(false);
            else await Task.WhenAny(_completion.Task, Task.Delay(delay)).ConfigureAwait(false);
        }
    }

    private void Wait()
    {
        try
        {
            _process.WaitForExit();
            Interlocked.Exchange(ref _nativeSignalTicks, Stopwatch.GetTimestamp());
            _completion.SetResult();
        }
        catch (Exception error) { _completion.SetException(error); }
    }
}
