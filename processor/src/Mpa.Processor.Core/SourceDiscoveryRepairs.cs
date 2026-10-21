using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mpa.Processor.Core;

public sealed record SourceDiscoveryRepair(StageLease Lease, SourceDiscoveryScope Scope, SourceDiscoveryFolder Folder, string? FileId, string? PageToken);

public sealed partial class SourceDiscoveryRepository
{
    private const string RepairStage = "repair-metadata";
    private static readonly string RepairRecipe = Convert.ToHexStringLower(SHA256.HashData("mpa-source-metadata-repair:1"u8));

    /// <summary>Record only the failed observation before advancing its folder checkpoint.
    /// Known identities retry that exact file; an invalid/missing identity retries the captured
    /// metadata page without assigning its position to a different photo. Both are read-only.</summary>
    public bool RecordFailure(SourceDiscoveryWork parent, string? fileId)
    {
        ValidateActive(parent); ValidateCurrent(parent);
        if (fileId is not null && !Identity(fileId)) throw new ArgumentException("Invalid failed file identity.", nameof(fileId));
        var checkpoint = Checkpoint(parent);
        if (checkpoint.Started && checkpoint.NextPageToken is null) throw new InvalidOperationException("A completed folder cannot add a failed page.");
        return _jobs.EnsureFailedItem(parent.Lease.JobId, RepairItem(parent.Scope, parent.Folder, fileId, fileId is null ? checkpoint.NextPageToken : null),
            "invalid-source-metadata", fileId is null ? "Drive returned an item without a usable identity. Retry this metadata page." :
                "Drive returned invalid metadata for this item. Other items can continue; retry this item after correcting the cause.");
    }

    public SourceDiscoveryRepair? ClaimRepair()
    {
        var lease = _jobs.ClaimNext(JobKind, RepairStage);
        if (lease is null) return null;
        using var context = Object(lease.CapturedItemContext, 4, "mpa-source-repair-1"); var root = context.RootElement;
        var folder = Folder(root.GetProperty("folder").GetRawText());
        var repair = new SourceDiscoveryRepair(lease, Scope(lease.CapturedContext), folder, root.GetProperty("fileId").GetString(), root.GetProperty("pageToken").GetString());
        ValidateShape(repair); return repair;
    }

    public void ValidateCurrent(SourceDiscoveryRepair repair)
    {
        ValidateShape(repair); ValidateActive(repair.Lease); ValidateCurrent(repair.Scope);
    }
    private void ValidateShape(SourceDiscoveryRepair repair)
    {
        ArgumentNullException.ThrowIfNull(repair);
        if (repair.Scope != Scope(repair.Lease.CapturedContext) || repair.Lease.Stage != RepairStage ||
            repair.FileId is not null && (!Identity(repair.FileId) || repair.PageToken is not null) ||
            repair.PageToken is not null && (repair.PageToken.Length is < 1 or > 4096 || repair.PageToken.Any(char.IsControl)) ||
            repair.Folder.Depth == 0 && repair.Folder.FolderId != repair.Scope.RootFolderId)
            throw new InvalidDataException("Repair work does not match its captured source.");
        var expected = RepairItem(repair.Scope, repair.Folder, repair.FileId, repair.PageToken);
        if (expected.Id != repair.Lease.ItemId || expected.CapturedContext != repair.Lease.CapturedItemContext)
            throw new InvalidDataException("Repair work cannot substitute another file, page or folder.");
    }
    private JobItemSpec RepairItem(SourceDiscoveryScope scope, SourceDiscoveryFolder folder, string? fileId, string? pageToken)
    {
        // Reuse the strict folder decoder before constructing an immutable repair context.
        using var folderJson = JsonDocument.Parse(Item(scope, folder).CapturedContext);
        _ = Folder(folderJson.RootElement.GetRawText());
        var context = _codec.Encode(JsonSerializer.SerializeToUtf8Bytes(new { format = "mpa-source-repair-1", folder = folderJson.RootElement, fileId, pageToken }));
        var hash = Convert.ToHexStringLower(SHA256.HashData(context));
        return new(_identity.Derive(scope.AccountId, "repair-" + hash), hash, [new(RepairStage, RepairRecipe, [])], Encoding.UTF8.GetString(context));
    }
}
