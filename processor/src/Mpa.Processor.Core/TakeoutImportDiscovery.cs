namespace Mpa.Processor.Core;

public sealed partial class SourceDiscoveryRepository
{
    /// <summary>A completed explicit import requests one real source scan. This is
    /// read-only discovery, never an original-write grant or fabricated photo fact.
    /// Its first durable scan identity/scope survives a lost reply and restart.</summary>
    public SourceDiscoverySchedule EnsureImportedOriginalsScheduled(Guid importId)
    {
        var imports=new TakeoutImportInventoryRepository(_database);
        var completed=imports.ReadCompleted(importId)??throw new InvalidOperationException("Review the captured import first.");
        var activity=imports.Activity(importId);
        if(!activity.Requested||activity.Media is null or 0||activity.Verified!=activity.Media||activity.State is "paused" or "canceled")
            throw new InvalidOperationException("Only independently verified originals can request their imported-source scan.");
        var target=completed.Target;
        if(new ProcessorLibraryRepository(_database).GetOrCreateIdentity()!=target.LibraryId)throw new InvalidDataException("Imported originals belong to another native library.");
        var request=_identity.Derive(target.AccountId,"takeout-import-discovery-1-"+importId.ToString("N"));
        if(ReadScope(request) is{}retained)
        {
            if(retained.AccountId!=target.AccountId||retained.RootFolderId!=target.FolderId)throw new InvalidDataException("The first import discovery scope changed its destination.");
            return new(request,false); // Local recovery only; grants no IO or new scope.
        }
        var snapshot=new LibrarySourceRepository(_database).Snapshot();
        var source=snapshot.Sources.SingleOrDefault(value=>value.AccountId==target.AccountId&&value.FolderId==target.FolderId&&value.Enabled&&value.AccountConnected);
        if(snapshot.Revision!=target.ConfigurationRevision||source is null)throw new StaleLibraryConfigurationException();
        var account=new DriveAccountRepository(_database).Accounts().SingleOrDefault(value=>value.PermissionId==target.AccountId&&value.Connected)
            ??throw new StaleLibraryConfigurationException();
        // Current read access is separate from the immutable original intent.
        return RequestScan(new(request,source.Id,source.Revision,account.Revision));
    }
}
