using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    internal Task<WorkspacePublicationRecord> ConfirmCatalogHeaderReceiptAsync(LocalCurrentCatalogHeaderCloudAdmission admission,
        DriveWorkspaceObject actual,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(admission);ArgumentNullException.ThrowIfNull(actual);
        return StateOnlyOperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertCatalogHeaderCloudAdmissionOwner(admission);
            var inventory=admission.Inventory;var fence=inventory.Fence;
            AssertScope(database,new(fence.Capture.Pin,fence.Capture.Slot,fence.Attempt));
            return new CatalogGenerationCaptureRepository(database,_budget).ConfirmCurrentCatalogHeaderReceipt(admission.Original,
                inventory.Signing,fence.Attempt,inventory.Publications,actual.AccountId,actual.FolderId,actual.FileId,
                actual.MimeType,actual.DriveVersion,actual.Bytes,token);
        },token),caller);
    }
}
