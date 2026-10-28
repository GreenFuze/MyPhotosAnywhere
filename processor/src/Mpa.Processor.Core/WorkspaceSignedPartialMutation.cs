using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class WorkspacePublicationRepository
{
    private void AssertCurrentPartialWorkMutable(WorkspacePublicationScope scope,SqliteTransaction? transaction)
    {
        if(transaction is not null)
        {
            if(new CatalogGenerationCaptureRepository(_database,_budget).IsCurrentPartialDisposedIn(scope,transaction))
                throw new CatalogGenerationCaptureConflictException();
            return;
        }
        using var owned=_database.Connection.BeginTransaction();
        AssertCurrentPartialWorkMutable(scope,owned);owned.Commit();
    }
}
