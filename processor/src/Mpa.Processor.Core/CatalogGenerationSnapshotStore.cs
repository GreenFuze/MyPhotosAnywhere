using System.Text.Json;

namespace Mpa.Processor.Core;

/// <summary>Owns a marked private directory lease and per-file synchronous users.
/// Its claims come from the durable state journal. Disposal retains claimed files;
/// recovery deletes only validated generated files absent from ALL active claims.
/// Never accepts media paths or recursively deletes a directory.</summary>
public sealed class CatalogGenerationSnapshotStore : IDisposable
{
    private const string DirectoryName = "catalog-generation-snapshots";
    private static readonly object AccountingGate = new();
    private readonly string _root;
    private readonly StorageBudgetManager _budget;
    private readonly Dictionary<Guid, CatalogGenerationCapture> _claims;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _busy = [];
    private FileStream? _lease;
    private bool _closed;

    public CatalogGenerationSnapshotStore(string stateDirectory, ProcessorStateDatabase database, StorageBudgetManager budget)
    {
        ArgumentNullException.ThrowIfNull(database); _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        var state = Path.GetFullPath(stateDirectory); CheckRegular(state, true); CheckState(state, database.InstallationId);
        // SQLite may resolve parent aliases (macOS /var -> /private/var). The
        // single-instance owner retains the exact path it actually leased.
        if (!string.Equals(Path.TrimEndingDirectorySeparator(state), database.StateDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("Retained snapshots must use the leased database's exact state directory.", nameof(stateDirectory));
        var journal = new CatalogGenerationCaptureRepository(database, budget);
        _claims = journal.Active().ToDictionary(claim => claim.SnapshotId);
        _root = Path.Combine(state, DirectoryName);
        using var reservation = budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
        // Budget reads acquire their own reservation lock before AccountingGate.
        // Never reserve while holding this gate; that would invert the lock order.
        lock (AccountingGate) InitializeDirectory(database.InstallationId, journal);
    }
    private void InitializeDirectory(Guid installationId, CatalogGenerationCaptureRepository journal)
    {
        var existed = Directory.Exists(_root);
        if (!existed) Directory.CreateDirectory(_root); CheckRegular(_root, true);
        var marker = Path.Combine(_root, "owner.json");
        if (existed && !File.Exists(marker)) throw new InvalidDataException("Refusing to claim an unmarked retained snapshot directory.");
        FileStream? lease = null;
        try
        {
            var lockPath = Path.Combine(_root, "owner.lock"); if (File.Exists(lockPath)) CheckRegular(lockPath, false);
            lease = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!existed)
            {
                using var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(output, new { format = "mpa-generation-snapshots", major = 1, installationId = installationId.ToString("D") }); output.Flush(true);
            }
            CheckOwner(_root, installationId); var inventory = Inventory(_root);
            foreach (var claim in _claims.Values)
            {
                var path = SnapshotPath(claim.SnapshotId);
                if (claim.Snapshot is not null && (!File.Exists(path) || new FileInfo(path).Length != claim.Snapshot.Bytes))
                    throw new InvalidDataException("A claimed frozen snapshot is missing or changed; no recovery file was removed.");
                if (claim.Snapshot is not null && File.Exists(path + "-journal")) throw new InvalidDataException("A sealed snapshot has an unconfirmed transaction journal; preserve it for recovery.");
            }
            var unclaimed = inventory.Where(path => ParseSnapshotName(Path.GetFileName(path)) is Guid id && !_claims.ContainsKey(id)).ToArray();
            foreach (var id in unclaimed.Select(path => ParseSnapshotName(Path.GetFileName(path))!.Value).Distinct()) journal.AssertUnpublishedSnapshotCleanup(id);
            foreach (var path in unclaimed.OrderByDescending(path => path.EndsWith("-journal", StringComparison.Ordinal))) File.Delete(path);
            _lease = lease; lease = null;
        }
        finally { lease?.Dispose(); }
    }

    public CatalogSnapshotDescriptor Save(FrozenCatalogSnapshot frozen, CatalogGenerationCapture capture, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frozen); CheckClaim(capture); token.ThrowIfCancellationRequested();
        if (capture.Phase != "capturing" || capture.Snapshot is not null) throw new InvalidOperationException("A frozen generation cannot be captured again.");
        BeginUse(capture.SnapshotId);
        try
        {
            using var reservation = _budget.Reserve(frozen.RetainedWriteBudget()); reservation.RecheckBeforeWrite(reservation.Bytes);
            var path = SnapshotPath(capture.SnapshotId); CheckRegular(_root, true);
            using (var create = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            // An interrupted file remains claimed for inspection; never overwrite it
            // or delete it merely because native copy/cancellation failed.
            frozen.CopyForRetention(path, capture, token); token.ThrowIfCancellationRequested();
            using (var flush = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flush.Flush(true);
            return FrozenCatalogSnapshot.InspectRetained(path, capture, token);
        }
        finally { EndUse(capture.SnapshotId); }
    }

    public bool ContainsCaptureFile(CatalogGenerationCapture capture)
    {
        CheckClaim(capture); BeginUse(capture.SnapshotId);
        try
        {
            CheckRegular(_root, true); var path = SnapshotPath(capture.SnapshotId);
            if (File.Exists(path + "-journal")) throw new InvalidDataException("An interrupted snapshot journal must be inspected before any new capture.");
            if (Directory.Exists(path)) throw new InvalidDataException("The claimed snapshot was replaced by a directory.");
            if (!File.Exists(path)) return false;
            CheckRegular(path, false); return true;
        }
        finally { EndUse(capture.SnapshotId); }
    }

    public CatalogSnapshotDescriptor Inspect(CatalogGenerationCapture capture, CancellationToken token)
    {
        CheckClaim(capture); BeginUse(capture.SnapshotId);
        try { CheckFile(capture.SnapshotId); return FrozenCatalogSnapshot.InspectRetained(SnapshotPath(capture.SnapshotId), capture, token); }
        finally { EndUse(capture.SnapshotId); }
    }
    public FrozenCatalogSnapshot Open(CatalogGenerationCapture capture, CancellationToken token)
    {
        CheckClaim(capture); if (capture.Phase is not ("frozen" or "publication-planned" or "dependencies-ready" or "commit-sealed") || capture.Snapshot is null) throw new InvalidOperationException("A sealed frozen snapshot is required.");
        CatalogGenerationCaptureRepository.ValidateDescriptor(capture.Snapshot); BeginUse(capture.SnapshotId);
        try
        {
            CheckFile(capture.SnapshotId);
            return FrozenCatalogSnapshot.OpenRetained(SnapshotPath(capture.SnapshotId), capture, _budget, () => EndUse(capture.SnapshotId), token);
        }
        catch { EndUse(capture.SnapshotId); throw; }
    }

    public static long MeasureOwnedBytes(string stateDirectory)
    {
        lock (AccountingGate) return MeasureOwnedBytesCore(stateDirectory);
    }
    private static long MeasureOwnedBytesCore(string stateDirectory)
    {
        var state = Path.GetFullPath(stateDirectory); var root = Path.Combine(state, DirectoryName);
        if (!Directory.Exists(root)) { if (File.Exists(root)) throw new IOException("Retained snapshot accounting refuses a replaced directory."); return 0; } CheckRegular(root, true);
        using var identity = ReadMarker(Path.Combine(state, "mpa-processor-state.json"));
        if (!Guid.TryParseExact(identity.RootElement.GetProperty("installationId").GetString(), "D", out var id) || id == Guid.Empty) throw new InvalidDataException("Retained snapshot accounting lost its state identity.");
        CheckOwner(root, id); long bytes = 0;
        foreach (var path in Inventory(root, accounting: true))
        {
            try { bytes = checked(bytes + new FileInfo(path).Length); }
            catch (FileNotFoundException) when (TemporaryJournal(path)) { /* SQLite completed this measured temporary file concurrently. */ }
        }
        return bytes;
    }
    private void CheckClaim(CatalogGenerationCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!_claims.TryGetValue(capture.SnapshotId, out var expected) || expected.Scope != capture.Scope || expected.CommitIntentId != capture.CommitIntentId ||
            expected.Phase != capture.Phase || expected.Snapshot != capture.Snapshot || capture.Phase == "abandoned")
            throw new CatalogGenerationCaptureConflictException();
    }
    private string SnapshotPath(Guid id) => Path.Combine(_root, "snapshot-" + id.ToString("N") + ".sqlite");
    private void CheckFile(Guid id)
    { CheckRegular(_root, true); CheckRegular(SnapshotPath(id), false); if (File.Exists(SnapshotPath(id) + "-journal")) throw new InvalidDataException("Retained snapshot transaction recovery is unconfirmed."); }
    private void BeginUse(Guid id)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); if (!_busy.Add(id)) throw new InvalidOperationException("The retained snapshot already has an active native owner."); } }
    private void EndUse(Guid id)
    { lock (_gate) { if (!_busy.Remove(id)) throw new InvalidOperationException("Retained snapshot lease released without ownership."); } }
    private static string[] Inventory(string root, bool accounting = false)
    {
        CheckRegular(root, true); var paths = Directory.EnumerateFileSystemEntries(root).Take(7).ToArray();
        if (paths.Length > 6) throw new InvalidDataException("Retained snapshot recovery inventory exceeds its bound.");
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name is not ("owner.json" or "owner.lock") && ParseSnapshotName(name) is null) throw new InvalidDataException("Unrecognized retained snapshot entry; no recovery file was removed.");
            try { CheckRegular(path, false); }
            catch (FileNotFoundException) when (accounting && TemporaryJournal(path)) { /* Actual generated journal was removed after enumeration. */ }
        }
        return paths;
    }
    private static Guid? ParseSnapshotName(string name)
    {
        var stem = name.EndsWith("-journal", StringComparison.Ordinal) ? name[..^8] : name;
        return stem.Length == 48 && stem.StartsWith("snapshot-", StringComparison.Ordinal) && stem.EndsWith(".sqlite", StringComparison.Ordinal) &&
            Guid.TryParseExact(stem[9..41], "N", out var id) && id != Guid.Empty && id.ToString("N") == stem[9..41] ? id : null;
    }
    private static bool TemporaryJournal(string path) => path.EndsWith("-journal", StringComparison.Ordinal) && ParseSnapshotName(Path.GetFileName(path)) is not null;
    private static void CheckState(string state, Guid installation)
    { using var marker = ReadMarker(Path.Combine(state, "mpa-processor-state.json")); if (marker.RootElement.GetProperty("installationId").GetString() != installation.ToString("D")) throw new InvalidDataException("Retained snapshot directory belongs to another state installation."); }
    private static void CheckOwner(string root, Guid installation)
    {
        using var marker = ReadMarker(Path.Combine(root, "owner.json")); var data = marker.RootElement;
        if (data.GetProperty("format").GetString() != "mpa-generation-snapshots" || data.GetProperty("major").GetInt32() != 1 || data.GetProperty("installationId").GetString() != installation.ToString("D"))
            throw new InvalidDataException("Retained snapshot directory ownership failed.");
    }
    private static JsonDocument ReadMarker(string path)
    { CheckRegular(path, false); using var input = File.OpenRead(path); if (input.Length is < 1 or > 4096) throw new InvalidDataException("Snapshot ownership marker exceeds its bound."); return JsonDocument.Parse(input); }
    private static void CheckRegular(string path, bool directory)
    { var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory) throw new IOException("Retained snapshot ownership refuses a linked or unexpected file."); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_busy.Count != 0) throw new InvalidOperationException("Join all retained snapshot native owners before releasing their directory.");
            _lease?.Dispose(); _lease = null; _closed = true; // Durable files/markers survive Exit.
        }
    }
}
