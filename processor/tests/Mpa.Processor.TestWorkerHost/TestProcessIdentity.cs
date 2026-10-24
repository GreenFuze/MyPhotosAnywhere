using System.Diagnostics;
using System.Globalization;

namespace Mpa.Processor.TestSupport;

/// <summary>Test-only instance identity shared by the observing processes. Linux's
/// kernel start counter avoids independently estimated wall-clock boot times.</summary>
internal static class TestProcessIdentity
{
    public static string Read(Process process)
    {
        var pid = process.Id.ToString(CultureInfo.InvariantCulture);
        if (!OperatingSystem.IsLinux())
            return pid + ":" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        // https://www.kernel.org/doc/html/latest/filesystems/proc.html : stat field 22.
        // tcomm may itself contain spaces/parentheses; numeric fields follow its final ')'.
        var stat = File.ReadAllText("/proc/" + pid + "/stat");
        var end = stat.LastIndexOf(')');
        if (end < 0) throw new InvalidDataException("Missing Linux process command boundary.");
        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            throw new InvalidDataException("Missing Linux kernel process start counter.");
        var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        if (!Guid.TryParseExact(boot, "D", out _)) throw new InvalidDataException("Invalid Linux boot identity.");
        return pid + ":" + boot + ":" + start.ToString(CultureInfo.InvariantCulture);
    }
}
