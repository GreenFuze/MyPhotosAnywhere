using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices;

public sealed partial class LocalCurrentControlReader
{
    internal bool BorrowsDependencyUploadAccount(ProcessorAccountService accounts)=>ReferenceEquals(accounts,_accounts);

    internal Task<WorkspacePublicationRecord> ConfirmCatalogDependencyReceiptAsync(LocalCurrentCatalogDependencyCloudAdmission admission,
        DriveWorkspaceObject actual,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(admission);ArgumentNullException.ThrowIfNull(actual);
        return StateOnlyOperationAsync(token=>_state.ExecuteAsync(database=>
        {
            Check(caller,token);AssertCatalogDependencyCloudAdmissionOwner(admission);var inventory=admission.Inventory;
            AssertSnapshotInventory(database,inventory);
            return new CatalogGenerationCaptureRepository(database,_budget).ConfirmCurrentCatalogDependencyReceipt(admission.Original,
                inventory.Signing,inventory.Fence.Attempt,inventory.Publications,actual.AccountId,actual.FolderId,actual.FileId,
                actual.MimeType,actual.DriveVersion,actual.Bytes,token);
        },token),caller);
    }
}
