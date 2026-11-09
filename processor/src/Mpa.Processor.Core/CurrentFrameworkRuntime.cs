using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mpa.Processor.Core;

/// <summary>Captures only the shared runtime already executing this native owner.
/// Does not inherit DOTNET_ROOT, search PATH, select an SDK or accept a web path.
/// Self-contained applications keep their app-local runtime discovery.</summary>
internal sealed class CurrentFrameworkRuntime
{
    private readonly string _root;
    private readonly string _architecture;
    private CurrentFrameworkRuntime(string root,string architecture){_root=root;_architecture=architecture;}
    internal static CurrentFrameworkRuntime? Capture()
    {
        var runtime=new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory())));
        if(runtime.Parent?.Name!="Microsoft.NETCore.App"||runtime.Parent.Parent?.Name!="shared")return null;
        var root=runtime.Parent.Parent.Parent??throw new InvalidDataException("The executing shared runtime lost its installation root.");
        var core=typeof(object).Assembly.Location;
        if(string.IsNullOrEmpty(core)||!string.Equals(Path.GetDirectoryName(Path.GetFullPath(core)),runtime.FullName,
            OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidDataException("Shared runtime discovery does not match the already loaded Core library.");
        if(!root.Exists)throw new DirectoryNotFoundException("The executing shared runtime installation disappeared.");
        return new(root.FullName,RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant());
    }
    internal void ApplyTo(ProcessStartInfo start)=>start.Environment["DOTNET_ROOT_"+_architecture]=_root;
}
