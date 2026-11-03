using Mpa.Processor.Core;

namespace Mpa.Processor.TestSupport;

internal sealed class InventoryCrashSpace(string root) : IStorageSpaceReader
{
    public StorageSpaceSnapshot Read()=>new(50*StorageBudgetManager.Gibibyte,40*StorageBudgetManager.Gibibyte,TakeoutAssociationCatalog.MeasureOwnedBytes(root));
}
