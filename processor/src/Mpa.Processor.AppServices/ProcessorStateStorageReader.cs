using System.ComponentModel;
using System.Runtime.InteropServices;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices;

/// <summary>Measures the engine's state SQLite files and marked retained-catalog inventory,
/// never user media or arbitrary source directories. Call on the serial state owner. The native
/// volume query uses the actual directory, including mounted volumes and per-user quotas.
/// This metadata budget does not authorize future preview/original-cache writes.</summary>
public sealed partial class ProcessorStateStorageReader : IStorageSpaceReader
{
    private readonly string _directory;
    private static readonly string[] StateFiles = ["processor.db", "processor.db-wal", "processor.db-shm"];
    public ProcessorStateStorageReader(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        if (!Path.IsPathFullyQualified(stateDirectory)) throw new ArgumentException("An absolute state directory is required.", nameof(stateDirectory));
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        if (_directory == Path.GetPathRoot(_directory)) throw new ArgumentException("A dedicated state directory is required.", nameof(stateDirectory));
    }

    public StorageSpaceSnapshot Read()
    {
        var root = new DirectoryInfo(_directory);
        if (!root.Exists) throw new DirectoryNotFoundException("The owned Processor state directory is unavailable.");
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The owned state directory cannot be replaced by a link.");
        long owned = 0;
        foreach (var name in StateFiles)
        {
            var path = Path.Combine(_directory, name);
            // FileInfo.Exists would disguise a directory as an absent optional sidecar.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) when (name != "processor.db") { continue; }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("An owned state file was replaced by a directory or link.");
            owned = checked(owned + new FileInfo(path).Length);
        }
        owned = checked(owned + CatalogGenerationSnapshotStore.MeasureOwnedBytes(_directory));

        if (OperatingSystem.IsWindows())
        {
            if (GetDiskFreeSpace(_directory + Path.DirectorySeparatorChar, out var available, out var capacity, out _) == 0)
                throw new IOException("The selected state volume cannot be measured.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return new(checked((long)capacity), checked((long)available), owned);
        }
        // .NET's Unix DriveInfo retains this exact path and uses statfs on it. Taking
        // Path.GetPathRoot here would incorrectly measure / instead of a nested mount.
        var volume = new DriveInfo(_directory);
        return new(volume.TotalSize, volume.AvailableFreeSpace, owned);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int GetDiskFreeSpace(string directory, out ulong available, out ulong capacity, out ulong totalFree);
}
