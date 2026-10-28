using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed partial class WorkspacePublicationRepository
{
    private void AssertSignedPartialWorkMutable(WorkspacePublicationScope scope,SqliteTransaction? transaction)
    {
        if(transaction is not null)
        {
            if(new CatalogGenerationCaptureRepository(_database,_budget).IsSignedPartialDisposedIn(scope,transaction))
                throw new CatalogGenerationCaptureConflictException();
            return;
        }
        using var owned=_database.Connection.BeginTransaction();
        AssertSignedPartialWorkMutable(scope,owned);owned.Commit();
    }
}
