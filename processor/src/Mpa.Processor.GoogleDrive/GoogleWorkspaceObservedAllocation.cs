using System.Text.Json;

namespace Mpa.Processor.GoogleDrive;

public sealed record DriveWorkspaceAllocation(string AccountId,string FileId);

/// <summary>Joined immutable LOCAL receipt persistence only. Never issue network,
/// reacquire this Google/account owner or dispose any borrowed resource here.
/// No caller cancellation token is supplied after the canonical ID is known.</summary>
public interface IGoogleWorkspaceAllocationObserver
{
    Task RememberAllocatedIdAsync(DriveWorkspaceAllocation allocation);
}
public interface IGoogleWorkspaceObservedAllocation
{
    Task<string> ObserveWorkspaceObjectIdAllocationAsync(IGoogleWorkspaceAllocationObserver observer,
        Func<CancellationToken,Task> admit,CancellationToken cancellationToken);
}

public sealed partial class GoogleAccountConnection : IGoogleWorkspaceObservedAllocation
{
    public Task<string> ObserveWorkspaceObjectIdAllocationAsync(IGoogleWorkspaceAllocationObserver observer,
        Func<CancellationToken,Task> admit,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observer);ArgumentNullException.ThrowIfNull(admit);
        var account=_expectedPermissionId ?? throw new InvalidOperationException("Observed allocation requires its captured Google account.");
        return ExecuteAdmittedMetadataAsync(async token=>
        {
            var credential=await MediaCredentialAsync(token).ConfigureAwait(false);
            var bytes=await MediaGetAsync(credential,new("https://www.googleapis.com/drive/v3/files/generateIds?count=1&space=drive&type=files&fields=ids,space"),4096,token).ConfigureAwait(false);
            var id=ReadObservedAllocatedId(bytes);
            // Once complete bytes have been parsed/validated, do not lose a known
            // live receipt at the normal ExecuteAsync cancellation/result gate.
            await observer.RememberAllocatedIdAsync(new(account,id)).ConfigureAwait(false);
            CheckNetworkActive(token);return id;
        },admit,cancellationToken);
    }

    private static string ReadObservedAllocatedId(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document=JsonDocument.Parse(bytes);var root=document.RootElement;var ids=root.GetProperty("ids");
            if(root.GetProperty("space").GetString()!="drive" || ids.ValueKind!=JsonValueKind.Array || ids.GetArrayLength()!=1 ||
                ids[0].ValueKind!=JsonValueKind.String || !ValidIdentity(ids[0].GetString()) || ids[0].GetString()=="root")
                throw new InvalidDataException("Drive did not allocate exactly one canonical ordinary-file identity.");
            return ids[0].GetString()!;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException)
        {throw new InvalidDataException("Drive returned malformed generated-file identities.",error);}
    }
}
