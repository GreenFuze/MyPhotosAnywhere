using System.Text.Json;

namespace Mpa.Processor.Core;

public sealed class CatalogPreparationCapacityException() : IOException("Finish or release another catalog preparation before starting a new one.");
public sealed class CatalogPreparationDirectoryStartException(Exception cause, CatalogPreparationDirectory retainedOwner)
    : Exception("Catalog preparation directory startup cleanup is unconfirmed; ownership is retained.", cause)
{ public CatalogPreparationDirectory RetainedOwner { get; } = retainedOwner; }

/// <summary>One leased, marked directory for generated catalog preparation indexes only. It never accepts
/// media paths. Recovery validates the complete bounded inventory before removing stale indexes.</summary>
public sealed class CatalogPreparationDirectory : IDisposable
{
    private readonly string _root;
    private FileStream? _lease;
    private bool _closed;
    private const string MarkerName = "owner.json";
    private const string LeaseName = "owner.lock";
    public CatalogPreparationDirectory(string stateDirectory, Guid installationId)
    {
        if (installationId == Guid.Empty) throw new ArgumentException("Installation identity is required.", nameof(installationId));
        var state = Path.GetFullPath(stateDirectory); _root = Path.Combine(state, "catalog-preparations");
        CheckRegular(state, directory: true);
        var stateMarker = Path.Combine(state, "mpa-processor-state.json"); CheckRegular(stateMarker, directory: false);
        using (var identity = ReadMarker(stateMarker))
            if (identity.RootElement.GetProperty("installationId").GetString() != installationId.ToString("D"))
                throw new InvalidDataException("Catalog preparation directory installation does not own this Processor state.");
        var existed = Directory.Exists(_root);
        if (!existed) Directory.CreateDirectory(_root);
        CheckRegular(_root, directory: true);
        var markerPath = Path.Combine(_root, MarkerName);
        if (existed && !File.Exists(markerPath)) throw new InvalidDataException("Refusing to claim an unmarked catalog preparation directory.");
        FileStream? lease = null;
        try
        {
            if (File.Exists(Path.Combine(_root, LeaseName))) CheckRegular(Path.Combine(_root, LeaseName), directory: false);
            lease = new(Path.Combine(_root, LeaseName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!existed)
            {
                using var output = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(output, new { format = "mpa-catalog-preparations", major = 1, installationId = installationId.ToString("D") }); output.Flush(flushToDisk: true);
            }
            CheckRegular(markerPath, directory: false);
            using (var marker = ReadMarker(markerPath))
                if (marker.RootElement.GetProperty("format").GetString() != "mpa-catalog-preparations" || marker.RootElement.GetProperty("major").GetInt32() != 1 ||
                    marker.RootElement.GetProperty("installationId").GetString() != installationId.ToString("D")) throw new InvalidDataException("Unknown catalog preparation directory owner.");
            var files = Inventory();
            // No recursive delete, wildcard delete or unvalidated path is used. Every
            // candidate is a regular generated file inside this leased private directory.
            foreach (var path in files.Where(path => Path.GetFileName(path) is not (MarkerName or LeaseName)).OrderByDescending(path => path.EndsWith("-journal", StringComparison.Ordinal))) File.Delete(path);
            _lease = lease; lease = null;
        }
        catch (Exception error)
        {
            if (!existed)
            {
                _lease = lease; lease = null;
                try { Dispose(); }
                catch (Exception cleanup) { throw new CatalogPreparationDirectoryStartException(new AggregateException(error, cleanup), this); }
            }
            throw;
        }
        finally { lease?.Dispose(); }
    }
    public string NewIndexPath()
    {
        ObjectDisposedException.ThrowIf(_closed, this); CheckRegular(_root, directory: true);
        if (Inventory().Count(path => Path.GetFileName(path).EndsWith(".sqlite", StringComparison.Ordinal)) >= 2)
            throw new CatalogPreparationCapacityException();
        return Path.Combine(_root, "capture-" + Guid.NewGuid().ToString("N") + ".sqlite");
    }
    private string[] Inventory()
    {
        CheckRegular(_root, directory: true);
        var files = Directory.EnumerateFileSystemEntries(_root).Take(7).ToArray();
        if (files.Length > 6) throw new InvalidDataException("Catalog preparation directory exceeds its bounded recovery inventory.");
        foreach (var path in files)
        {
            CheckRegular(path, directory: false); var name = Path.GetFileName(path);
            if (name is MarkerName or LeaseName) continue;
            var stem = name.EndsWith("-journal", StringComparison.Ordinal) ? name[..^8] : name;
            if (!stem.StartsWith("capture-", StringComparison.Ordinal) || !stem.EndsWith(".sqlite", StringComparison.Ordinal) ||
                stem.Length != 47 || !Guid.TryParseExact(stem[8..40], "N", out var id) || id == Guid.Empty || id.ToString("N") != stem[8..40])
                throw new InvalidDataException("Catalog preparation recovery found an unrecognized file; no inventory entry was removed.");
        }
        return files;
    }
    private static JsonDocument ReadMarker(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length is < 1 or > 4096) throw new InvalidDataException("Catalog preparation owner marker exceeds its bound.");
        return JsonDocument.Parse(input);
    }
    private static void CheckRegular(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Catalog preparation ownership refuses a linked or unexpected filesystem entry.");
    }
    public void Dispose()
    {
        if (_closed) return;
        var files = Inventory();
        if (files.Any(path => Path.GetFileName(path) is not (MarkerName or LeaseName))) throw new InvalidOperationException("Catalog preparation indexes must join and close before their directory owner.");
        _lease?.Dispose(); _lease = null;
        foreach (var name in new[] { LeaseName, MarkerName })
        {
            var path = Path.Combine(_root, name); if (!File.Exists(path)) continue; CheckRegular(path, directory: false); File.Delete(path);
        }
        Directory.Delete(_root); _closed = true;
    }
}
