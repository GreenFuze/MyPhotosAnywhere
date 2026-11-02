using System.Security.Cryptography;
using System.Text.Json;

namespace Mpa.Processor.Core;

public sealed partial class TakeoutWorkerInstallation
{
    /// <summary>Fixed first-party installed bundle, supplied by desktop composition.
    /// Never searches PATH, downloads a plugin or substitutes an SDK executable.</summary>
    public static async Task<TakeoutWorkerInstallation> OpenInstalledAsync(string directory,CancellationToken token)
    {
        if(!Path.IsPathFullyQualified(directory))throw new ArgumentException("The installed worker directory must be absolute.",nameof(directory));
        TakeoutMetadataStageStorage.Regular(directory,true);
        await using var manifest=TakeoutMetadataStageStorage.OpenRead(Path.Combine(directory,"worker.json"));
        if(manifest.Length is <1 or >4096)throw new InvalidDataException("The installed worker descriptor exceeds its fixed bound.");
        var bytes=new byte[(int)manifest.Length];await manifest.ReadExactlyAsync(bytes,token).ConfigureAwait(false);
        var offset=bytes.Length>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf?3:0;
        try
        {
            using var document=JsonDocument.Parse(bytes.AsMemory(offset),new JsonDocumentOptions{MaxDepth=4});var root=document.RootElement;
            var hostName=OperatingSystem.IsWindows()?"Mpa.Processor.WorkerHost.exe":"Mpa.Processor.WorkerHost";
            if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count()!=5||root.GetProperty("format").GetString()!="mpa-installed-takeout-worker-1"||
                root.GetProperty("assembly").GetString()!="Mpa.Processor.WorkerHost.dll"||root.GetProperty("host").GetString()!=hostName)
                throw new InvalidDataException("The first-party worker descriptor changed its fixed file identities.");
            var installation=ForAppHost(Path.Combine(directory,hostName),root.GetProperty("hostSha256").GetString()!,
                Path.Combine(directory,"Mpa.Processor.WorkerHost.dll"),root.GetProperty("assemblySha256").GetString()!);
            await using var assembly=await installation.OpenAssemblyAsync(token).ConfigureAwait(false);
            await using var host=TakeoutMetadataStageStorage.OpenRead(installation._runtime);
            if(Convert.ToHexStringLower(await SHA256.HashDataAsync(host,token).ConfigureAwait(false))!=installation._runtimeHash)
                throw new InvalidDataException("The installed first-party worker host changed; retain pending work and repair the installation.");
            return installation;
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {throw new InvalidDataException("The installed first-party worker descriptor is invalid.",error);}
    }
}
