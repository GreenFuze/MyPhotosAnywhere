namespace Mpa.Processor.Core;

/// <summary>One actual completed private-file retirement, borrowing the snapshot store's
/// retained native directory lease. Cannot be manufactured by an application caller.
/// Dispose after checkpointing (or failure); cancellation never clears the durable claim.</summary>
public sealed class CatalogSnapshotRetirement : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _assert;
    private readonly Action _release;
    private bool _confirmed;
    private bool _disposed;
    internal CatalogGenerationCapture Capture { get; }
    internal CatalogSnapshotRetirement(CatalogGenerationCapture capture, Action assert, Action release)
    { Capture = capture; _assert = assert; _release = release; }
    internal void AssertOwned(CatalogGenerationCapture capture)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_confirmed || capture != Capture) throw new CatalogGenerationCaptureConflictException();
            _assert();
        }
    }
    internal void Confirm() { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); if (_confirmed) throw new InvalidOperationException("Retirement receipt was already consumed."); _confirmed = true; } }
    public void Dispose() { lock (_gate) { if (_disposed) return; _release(); _disposed = true; } }
}

public sealed partial class CatalogGenerationSnapshotStore
{
    /// <summary>Only a persisted completed snapshot-retiring claim permits removal of
    /// this exact private generated file. Never accepts an original/media path. Keep the
    /// returned native-use lease and this store alive through the state checkpoint.</summary>
    public CatalogSnapshotRetirement RetireCompletedSnapshot(CatalogGenerationCapture capture, CancellationToken token)
    {
        CheckClaim(capture);
        if (capture.Phase != "snapshot-retiring" || capture.Snapshot is null) throw new CatalogGenerationCaptureConflictException();
        BeginUse(capture.SnapshotId); var transferred = false;
        try
        {
            token.ThrowIfCancellationRequested(); CheckRegular(_root, true); var path = SnapshotPath(capture.SnapshotId);
            if (File.Exists(path))
            {
                CheckFile(capture.SnapshotId);
                if (FrozenCatalogSnapshot.InspectRetained(path, capture, token) != capture.Snapshot) throw new InvalidDataException("Completed private snapshot changed before retirement.");
                token.ThrowIfCancellationRequested();
                lock (AccountingGate) { CheckRegular(path, false); File.Delete(path); }
            }
            // A missing file is recoverable only after the journal persisted this
            // phase, e.g. a crash after actual unlink before the state checkpoint.
            AssertRetirementOwned(capture); token.ThrowIfCancellationRequested();
            var result = new CatalogSnapshotRetirement(capture, () => AssertRetirementOwned(capture), () => EndUse(capture.SnapshotId));
            transferred = true; return result;
        }
        finally { if (!transferred) EndUse(capture.SnapshotId); }
    }
    private void AssertRetirementOwned(CatalogGenerationCapture capture)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_lease is null || !_busy.Contains(capture.SnapshotId)) throw new InvalidOperationException("Completed retirement lost its native directory/use lease.");
            CheckClaim(capture); CheckRegular(_root, true); var path = SnapshotPath(capture.SnapshotId);
            if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + "-journal") || Directory.Exists(path + "-journal"))
                throw new InvalidDataException("Private snapshot absence is unconfirmed; preserve its retirement claim.");
        }
    }
}
