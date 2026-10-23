using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record WorkerLaunchSpec(string ExecutablePath, string ExecutableSha256,
    IReadOnlyList<string> Arguments, string InstalledWorkingDirectory, string NewDiagnosticPath,
    WorkerResourcePolicy? ResourcePolicy = null);
public sealed class WorkerStartCleanupException(FramedWorkerProcess retainedWorker, Exception startup, Exception cleanup)
    : AggregateException("Worker startup failed and exit/cleanup could not be confirmed. Retain ownership and investigate.", startup, cleanup)
{
    public FramedWorkerProcess RetainedWorker { get; } = retainedWorker;
}
public sealed class WorkerProcessFailedException(int exitCode) : IOException("First-party worker exited unexpectedly with code " + exitCode)
{
    public int ExitCode { get; } = exitCode;
}
public sealed record WorkerProcessFailure(Exception Primary, ReadOnlyCollection<Exception> Secondary);
public sealed record WorkerTerminationObservation(int ProcessId, bool? NativeExitObserved, string? NativeObservationError,
    TaskStatus ExitBeforeNativeCheck, TaskStatus ExitAfterNativeCheck, TaskStatus Frames, TaskStatus Diagnostics,
    TaskStatus Monitor, TaskStatus Supervision, long MillisecondsAfterKill);
public sealed class WorkerTerminationTimeoutException(WorkerTerminationObservation observation, TimeoutException inner)
    : TimeoutException("Worker termination deadline expired; ownership is retained. Native exit=" + observation.NativeExitObserved +
        ", exit task=" + observation.ExitBeforeNativeCheck + "->" + observation.ExitAfterNativeCheck +
        ", frames=" + observation.Frames + ", diagnostics=" + observation.Diagnostics + ", monitor=" + observation.Monitor +
        ", supervision=" + observation.Supervision + ", elapsed after kill=" + observation.MillisecondsAfterKill + "ms.", inner)
{
    public WorkerTerminationObservation Observation { get; } = observation;
}

/// <summary>Owns one DIRECT first-party worker process that cannot spawn native delegates.
/// Installed manifest validation/role selection belongs to the launcher; no LAN command may
/// supply an executable/argument list. All I/O tasks are joined before executable/read leases release.</summary>
public sealed class FramedWorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly FileStream _executableLease;
    private readonly ParentLifetimeListener _parent;
    private readonly CancellationTokenSource _stopReading = new();
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource<Exception> _fault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Exception> _observedFaults = [];
    private readonly WorkerFrameWriter _writer;
    private readonly Task _framePump;
    private readonly Task _diagnosticPump;
    private readonly Task _processExit;
    private readonly Task _resourceMonitor;
    private readonly CancellationTokenSource _stopMonitoring = new();
    private readonly TaskCompletionSource _ownershipReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _disposalGate = new();
    private readonly object _exitOutcomeGate = new();
    private readonly HashSet<Task> _writes = [];
    private Task? _disposal;
    private bool _stopping;
    private bool _forcedStop;
    private bool _resourcesReleased;
    private AggregateException? _releaseError;
    public int ProcessId { get; }
    public Task Completion { get; }
    public bool ExitConfirmed => _processExit.IsCompletedSuccessfully;
    /// <summary>Process exit alone is not permission to release an operation's input,
    /// budget or state. The actual I/O owners and cleanup must have joined successfully.</summary>
    public bool RetirementConfirmed { get { lock (_disposalGate) return _resourcesReleased && _releaseError is null; } }
    /// <summary>All observed faults remain available after joined retirement. A forced
    /// termination's pipe fault never replaces the failure that caused termination.</summary>
    public WorkerProcessFailure? Failure
    {
        get
        {
            lock (_exitOutcomeGate) return _observedFaults.Count == 0 ? null :
                new(_observedFaults[0], _observedFaults.Skip(1).ToList().AsReadOnly());
        }
    }
    private long _observedPeakResidentBytes;
    public long ObservedPeakResidentBytes => Interlocked.Read(ref _observedPeakResidentBytes);

    private FramedWorkerProcess(Process process, FileStream executableLease, ParentLifetimeListener parent, FileStream diagnostics, WorkerResourcePolicy policy)
    {
        _process = process; _executableLease = executableLease; _parent = parent;
        ProcessId = process.Id;
        _writer = new WorkerFrameWriter(process.StandardInput.BaseStream);
        _processExit = process.WaitForExitAsync();
        _framePump = PumpFramesAsync();
        _diagnosticPump = PumpDiagnosticsAsync(diagnostics);
        _resourceMonitor = MonitorResourcesAsync(policy);
        Completion = SuperviseAsync();
    }

    public static async Task<FramedWorkerProcess> StartAsync(WorkerLaunchSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var policy = spec.ResourcePolicy ?? WorkerResourcePolicy.Default;
        policy.Validate();
        if (!Path.IsPathFullyQualified(spec.ExecutablePath) || !Path.IsPathFullyQualified(spec.InstalledWorkingDirectory) ||
            !Path.IsPathFullyQualified(spec.NewDiagnosticPath) || !Directory.Exists(spec.InstalledWorkingDirectory) ||
            spec.Arguments.Count > 128 || spec.Arguments.Any(argument => argument is null || argument.Length > 4096))
            throw new ArgumentException("Invalid installed worker launch description.", nameof(spec));
        var expected = Convert.FromHexString(spec.ExecutableSha256);
        if (expected.Length != 32) throw new ArgumentException("Worker executable requires an exact SHA-256.", nameof(spec));
        var executable = new FileStream(spec.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        ParentLifetimeListener? parent = null;
        Process? process = null;
        FileStream? diagnostic = null;
        var transferred = false;
        var started = false;
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(executable, cancellationToken).ConfigureAwait(false), expected))
                throw new InvalidDataException("Installed worker executable hash does not match its approved package.");
            parent = new ParentLifetimeListener();
            diagnostic = new FileStream(spec.NewDiagnosticPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            var start = new ProcessStartInfo(spec.ExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = spec.InstalledWorkingDirectory,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            if (OperatingSystem.IsWindows()) start.WindowStyle = ProcessWindowStyle.Hidden;
            foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["PATH"] = OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/usr/bin:/bin";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["OMP_THREAD_LIMIT"] = "2";
            start.Environment["MPA_PARENT_PORT"] = parent.Configuration.Port.ToString(CultureInfo.InvariantCulture);
            start.Environment["MPA_PARENT_KEY"] = parent.Configuration.SharedKey;
            process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("Worker process did not start.");
            started = true;
            var worker = new FramedWorkerProcess(process, executable, parent, diagnostic, policy);
            transferred = true;
            try
            {
                await parent.AcceptAsync(cancellationToken).ConfigureAwait(false);
                worker._ownershipReady.SetResult();
                return worker;
            }
            catch (Exception startup)
            {
                try { await worker.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new WorkerStartCleanupException(worker, startup, cleanup); }
                throw;
            }
        }
        finally
        {
            if (!transferred)
            {
                diagnostic?.Dispose();
                if (process is not null)
                {
                    // A canceled startup must still join its owned child; do not pass the canceled job token.
                    try { if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
                    finally { process.Dispose(); }
                }
                try { if (parent is not null) await parent.DisposeAsync().ConfigureAwait(false); }
                finally { executable.Dispose(); }
            }
        }
    }

    public IAsyncEnumerable<byte[]> ReadFramesAsync(CancellationToken cancellationToken) => _frames.Reader.ReadAllAsync(cancellationToken);
    public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        lock (_disposalGate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var write = _writer.WriteAsync(frame, cancellationToken);
            _writes.Add(write);
            _ = write.ContinueWith(completed =>
            {
                var error = completed.Exception?.GetBaseException(); // Observe all asynchronous write faults.
                lock (_disposalGate)
                {
                    _writes.Remove(completed);
                    if (!_stopping && error is not null) RecordFault(error);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return write;
        }
    }

    private async Task PumpFramesAsync()
    {
        try
        {
            var reader = new WorkerFrameReader(_process.StandardOutput.BaseStream);
            byte[]? frame;
            while ((frame = await reader.ReadAsync(_stopReading.Token).ConfigureAwait(false)) is not null)
                await _frames.Writer.WriteAsync(frame, _stopReading.Token).ConfigureAwait(false);
            _frames.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (_stopReading.IsCancellationRequested) { _frames.Writer.TryComplete(); }
        catch (Exception error) { _frames.Writer.TryComplete(RecordFault(error)); throw; }
    }

    private async Task PumpDiagnosticsAsync(FileStream destination)
    {
        await using (destination.ConfigureAwait(false))
        {
            try
            {
                var input = _process.StandardError.BaseStream;
                var buffer = new byte[4096];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                    total += count;
                    if (total > 8 * 1024 * 1024) throw new InvalidDataException("Worker exceeded its diagnostic budget; evidence retained and work stopped.");
                }
                await destination.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception error) { RecordFault(error); throw; }
        }
    }

    private async Task SuperviseAsync()
    {
        var first = await Task.WhenAny(_processExit, _fault.Task).ConfigureAwait(false);
        if (first == _fault.Task) KillOwned(requestedStop: false);
        await _processExit.ConfigureAwait(false);
        await _stopMonitoring.CancelAsync().ConfigureAwait(false);
        // Join every owned task before choosing the already observed first failure.
        // Killing a worker mid-frame can fault its reader; WhenAll's task ordering
        // must not replace the resource/diagnostic failure that caused that kill.
        try { await Task.WhenAll(_framePump, _diagnosticPump, _resourceMonitor).ConfigureAwait(false); }
        catch (Exception error) { RecordFault(error); }
        if (_fault.Task.IsCompletedSuccessfully) ExceptionDispatchInfo.Capture(await _fault.Task.ConfigureAwait(false)).Throw();
        lock (_exitOutcomeGate)
            if (_process.ExitCode != 0 && !_forcedStop) throw RecordFault(new WorkerProcessFailedException(_process.ExitCode));
    }

    private Exception RecordFault(Exception error)
    {
        lock (_exitOutcomeGate)
        {
            if (!_observedFaults.Any(observed => ReferenceEquals(observed, error))) _observedFaults.Add(error);
            var primary = _observedFaults[0]; _fault.TrySetResult(primary); return primary;
        }
    }

    private async Task MonitorResourcesAsync(WorkerResourcePolicy policy)
    {
        try
        {
            await _ownershipReady.Task.WaitAsync(_stopMonitoring.Token).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            while (!_process.HasExited)
            {
                _process.Refresh();
                var resident = _process.WorkingSet64;
                if (resident <= 0)
                {
                    // A process shedding its address space can report zero just before its exit
                    // notification is delivered. Confirm that exit; never accept zero for live work.
                    try { await _processExit.WaitAsync(TimeSpan.FromMilliseconds(250), _stopMonitoring.Token).ConfigureAwait(false); return; }
                    catch (TimeoutException error) { throw new InvalidDataException("Running worker resident-memory measurement is unavailable.", error); }
                }
                Interlocked.Exchange(ref _observedPeakResidentBytes, Math.Max(ObservedPeakResidentBytes, resident));
                if (resident > policy.ResidentMemoryStopBytes)
                    throw new WorkerResourceLimitException("resident-memory", resident, policy.ResidentMemoryStopBytes);
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed >= policy.MaximumLifetime)
                    throw new WorkerResourceLimitException("lifetime-milliseconds", (long)elapsed.TotalMilliseconds, (long)policy.MaximumLifetime.TotalMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(100), _stopMonitoring.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopMonitoring.IsCancellationRequested) { /* Owner joined monitoring on shutdown/exit. */ }
        catch (InvalidOperationException) when (_process.HasExited) { /* Exit raced the final sample. */ }
        catch (Exception error) { RecordFault(error); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            if (_disposal is null) { _stopping = true; _disposal = DisposeCoreAsync(_writes.ToArray()); }
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task[] writes)
    {
        var failures = new List<Exception>();
        await ObserveAsync(_stopMonitoring.CancelAsync).ConfigureAwait(false);
        await ObserveAsync(_stopReading.CancelAsync).ConfigureAwait(false);
        // Retire the independent Refresh/measurement loop before querying native
        // process termination; no monitor may mutate the process cache during stop.
        await ObserveAsync(() => _resourceMonitor).ConfigureAwait(false);
        try { _process.StandardInput.BaseStream.Close(); } catch (Exception error) { failures.Add(error); }
        await ObserveAsync(StopProcessAsync).ConfigureAwait(false);
        if (_processExit.IsCompletedSuccessfully)
        {
            // Exit can race a bounded-stop failure. In that case the old finally
            // released process/CTS owners without joining their actual pipe pumps.
            // Join the same real work on EVERY confirmed-exit path, even on failure.
            await ObserveAsync(() => Task.WhenAll(writes.Select(ObserveShutdownWriteAsync).Append(Completion))).ConfigureAwait(false);
            try { ReleaseResources(); } catch (Exception error) { failures.Add(error); }
        }
        else
        {
            // Signal parent loss, but retain all process/I/O ownership until the
            // recovery caller joins the actual exit through JoinAfterFailedStopAsync.
            await ObserveAsync(() => _parent.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        if (failures.Count != 0 && Failure?.Primary is { } primary)
        {
            failures.RemoveAll(error => ReferenceEquals(error, primary)); failures.Insert(0, primary);
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Worker shutdown failed; all observed stop/join/cleanup errors are retained.", failures);

        async Task ObserveAsync(Func<Task> operation)
        { try { await operation().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); } }
    }

    private async Task StopProcessAsync()
    {
        try { await WaitForActualExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            KillOwned(requestedStop: true);
            var killed = Stopwatch.GetTimestamp();
            try { await WaitForActualExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException error)
            {
                var before = _processExit.Status; bool? exited = null; string? nativeError = null;
                try { exited = _process.HasExited; } catch (Exception observationError) { nativeError = observationError.GetType().Name; }
                throw new WorkerTerminationTimeoutException(new(ProcessId, exited, nativeError, before, _processExit.Status,
                    _framePump.Status, _diagnosticPump.Status, _resourceMonitor.Status, Completion.Status,
                    (long)Stopwatch.GetElapsedTime(killed).TotalMilliseconds), error);
            }
        }
    }

    private async Task WaitForActualExitAsync(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (!_processExit.IsCompleted)
        {
            // A completed native process can precede delivery of its async exit
            // notification. The zero-time native wait checks the actual signal and
            // raises the existing exit event; its result NEVER substitutes for joining
            // the original async owner or any pipe/monitor task. Deadlines stay fixed.
            _ = _process.WaitForExit(0);
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero && !_processExit.IsCompleted) throw new TimeoutException("Actual worker exit-task join exceeded its termination deadline.");
            if (_processExit.IsCompleted) break;
            await Task.WhenAny(_processExit, Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);
        }
        await _processExit.ConfigureAwait(false);
    }

    /// <summary>Recovery owner uses this if the bounded stop could not confirm exit. Keep the
    /// controller/reader leases alive and refuse clean Exit until this actual join completes.</summary>
    public async Task JoinAfterFailedStopAsync()
    {
        Task[] writes;
        lock (_disposalGate)
        {
            if (!_stopping) throw new InvalidOperationException("No stop attempt is in progress.");
            writes = _writes.ToArray();
        }
        await _processExit.ConfigureAwait(false);
        try { await Task.WhenAll(writes.Select(ObserveShutdownWriteAsync).Append(Completion)).ConfigureAwait(false); }
        finally { ReleaseResources(); }
    }

    private static async Task ObserveShutdownWriteAsync(Task write)
    {
        try { await write.ConfigureAwait(false); }
        catch (IOException) { /* Closing the owned input interrupts pending writes; no success receipt is emitted. */ }
        catch (OperationCanceledException) { /* Requested shutdown. */ }
        catch (ObjectDisposedException) { /* Owned input was closed after stopping new sends. */ }
    }

    private void KillOwned(bool requestedStop)
    {
        lock (_exitOutcomeGate)
        {
            if (_process.HasExited) return;
            try { _process.Kill(entireProcessTree: true); if (requestedStop) _forcedStop = true; }
            catch (InvalidOperationException) when (_process.HasExited) { /* It exited between the check and termination. */ }
        }
    }

    private void ReleaseResources()
    {
        lock (_disposalGate)
        {
            if (_resourcesReleased) { if (_releaseError is not null) throw _releaseError; return; }
            if (!_processExit.IsCompletedSuccessfully || !Completion.IsCompleted || !_framePump.IsCompleted || !_diagnosticPump.IsCompleted || !_resourceMonitor.IsCompleted || _writes.Any(write => !write.IsCompleted))
                throw new InvalidOperationException("Cannot release worker resources before actual exit, writes, pipe pumps and monitoring have joined.");
            _resourcesReleased = true;
            var failures = new List<Exception>();
            foreach (var close in new Action[] { _parent.Dispose, _writer.Dispose, _stopReading.Dispose, _stopMonitoring.Dispose, _process.Dispose, _executableLease.Dispose })
                try { close(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) { _releaseError = new AggregateException("Worker resource cleanup failed.", failures); throw _releaseError; }
        }
    }
}
