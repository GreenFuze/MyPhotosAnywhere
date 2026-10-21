using System.Diagnostics;
using System.Globalization;
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
                    if (!_stopping && error is not null) _fault.TrySetResult(error);
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
        catch (Exception error) { _frames.Writer.TryComplete(error); _fault.TrySetResult(error); throw; }
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
            catch (Exception error) { _fault.TrySetResult(error); throw; }
        }
    }

    private async Task SuperviseAsync()
    {
        var first = await Task.WhenAny(_processExit, _fault.Task).ConfigureAwait(false);
        if (first == _fault.Task) KillOwned(requestedStop: false);
        await _processExit.ConfigureAwait(false);
        await _stopMonitoring.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_framePump, _diagnosticPump, _resourceMonitor).ConfigureAwait(false);
        if (_fault.Task.IsCompletedSuccessfully) throw await _fault.Task.ConfigureAwait(false);
        lock (_exitOutcomeGate)
            if (_process.ExitCode != 0 && !_forcedStop) throw new WorkerProcessFailedException(_process.ExitCode);
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
        catch (Exception error) { _fault.TrySetResult(error); }
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
        try
        {
            await _stopMonitoring.CancelAsync().ConfigureAwait(false);
            await _stopReading.CancelAsync().ConfigureAwait(false);
            _process.StandardInput.BaseStream.Close();
            try { await _processExit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                KillOwned(requestedStop: true);
                await _processExit.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            await Task.WhenAll(writes.Select(ObserveShutdownWriteAsync).Append(Completion)).ConfigureAwait(false);
        }
        finally
        {
            if (_processExit.IsCompletedSuccessfully) ReleaseResources();
            else await _parent.DisposeAsync().ConfigureAwait(false); // Fail-safe signal and joined handshake; process/executable ownership remains retained.
        }
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
            if (!_processExit.IsCompletedSuccessfully) throw new InvalidOperationException("Cannot release worker resources before exit.");
            _resourcesReleased = true;
            var failures = new List<Exception>();
            foreach (var close in new Action[] { _parent.Dispose, _writer.Dispose, _stopReading.Dispose, _stopMonitoring.Dispose, _process.Dispose, _executableLease.Dispose })
                try { close(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) { _releaseError = new AggregateException("Worker resource cleanup failed.", failures); throw _releaseError; }
        }
    }
}
