using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

/// <summary>Opaque run-owned LOCAL credential fence. It borrows exactly one native
/// database owner and preserves original capture identities. No cloud upload, current
/// remote availability, history admission or independent-client activation is granted.</summary>
public sealed class CatalogGenerationLocalAccess : IDisposable
{
    private readonly ProcessorStateDatabase _database;
    private readonly StorageBudgetManager _budget;
    private readonly CancellationToken _ownerStop;
    private int _retired;
    internal CatalogGenerationAccessAttempt Attempt { get; }
    internal LocalCatalogPublicationAuthority Authority { get; }
    public long AccountRevision => Attempt.AccountRevision;

    internal CatalogGenerationLocalAccess(ProcessorStateDatabase database, StorageBudgetManager budget, CatalogGenerationAccessAttempt attempt,
        LocalCatalogPublicationAuthority authority, CancellationToken ownerStop)
    { _database=database; _budget=budget; Attempt=attempt; Authority=authority; _ownerStop=ownerStop; }

    internal void AssertOwned(ProcessorStateDatabase database, WorkspacePublicationScope original)
    {
        _ownerStop.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Volatile.Read(ref _retired)!=0,this);
        if (!ReferenceEquals(database,_database) || original!=Attempt.Scope) throw new StaleLibraryConfigurationException();
    }

    internal void AssertCurrent(ProcessorStateDatabase database, WorkspacePublicationScope original, SqliteTransaction? transaction)
    {
        AssertOwned(database,original);
        if (transaction is not null) { new CatalogGenerationCaptureRepository(database,_budget).AssertLocalAccess(this,original,transaction); return; }
        using var owned=database.Connection.BeginTransaction();
        new CatalogGenerationCaptureRepository(database,_budget).AssertLocalAccess(this,original,owned); owned.Commit();
    }

    public void Dispose() => Interlocked.Exchange(ref _retired,1);
}

public sealed partial class CatalogGenerationCaptureRepository
{
    /// <summary>Open a local fence only after the actual concrete reconciler supplies
    /// its current completed scan and unchanged complete inventories. Returned local
    /// ownership is separate from the private one-object cloud admission.</summary>
    public CatalogGenerationLocalAccess OpenLocalAccess(Guid generationId,long accountRevision,WorkspaceInventoryCheckpoint checkpoint,
        ProtocolSigningInventory signing,WorkspacePublicationInventory publications,CancellationToken ownerStop)
    {
        ownerStop.ThrowIfCancellationRequested(); Id(generationId);
        ArgumentNullException.ThrowIfNull(checkpoint); ArgumentNullException.ThrowIfNull(signing); ArgumentNullException.ThrowIfNull(publications);
        using var transaction=_database.Connection.BeginTransaction(); var capture=RequiredCapture(generationId,transaction);
        var current=new LibraryAuthorityRepository(_database,_budget).ReadLocalPublicationAuthority(transaction);
        var attempt=ReadAccessAttempt(capture,accountRevision,transaction) ?? throw new StaleLibraryConfigurationException();
        if (checkpoint.Context!=publications.Context || checkpoint.Context.AccountRevision!=accountRevision ||
            checkpoint.Context.LibraryId!=capture.Scope.LibraryId || checkpoint.Context.AccountId!=capture.Scope.AccountId ||
            checkpoint.Context.FolderId!=capture.Scope.FolderId || checkpoint.Context.SharedDriveId!=attempt.Observation.Context.SharedDriveId ||
            signing.LibraryId!=capture.Scope.LibraryId) throw new StaleLibraryConfigurationException();
        new WorkspaceObservationRepository(_database,_budget).AssertCompletedScan(checkpoint,null,transaction);
        new ProtocolSigningJournal(_database,_budget).AssertInventoryUnchanged(signing.LibraryId,signing.Sha256,transaction);
        new WorkspacePublicationRepository(_database,_budget).AssertInventoryUnchanged(publications,transaction);
        var local=new CatalogGenerationLocalAccess(_database,_budget,attempt,current,ownerStop);
        try { AssertLocalAccess(local,capture.Scope,transaction); transaction.Commit(); return local; }
        catch { local.Dispose(); throw; }
    }

    internal void AssertLocalAccess(CatalogGenerationLocalAccess local,WorkspacePublicationScope original,SqliteTransaction transaction)
    {
        local.AssertOwned(_database,original); var capture=RequiredCapture(original.GenerationId,transaction);
        if (capture.Scope!=original || capture.Phase is "retired" or "abandoned" or "snapshot-retiring") throw new CatalogGenerationCaptureConflictException();
        var authority=new LibraryAuthorityRepository(_database,_budget); var current=authority.ReadLocalPublicationAuthority(transaction);
        if (current!=local.Authority || current.AccountRevision!=local.AccountRevision || current.AccountRevision<=original.AccountRevision ||
            original.LibraryId!=current.LibraryId || original.ActorId!=current.ActorId || original.ActorEpoch!=current.ActorEpoch ||
            original.AccountId!=current.AccountId || original.FolderId!=current.FolderId || original.ConfigurationRevision!=current.ConfigurationRevision ||
            original.AuthoritySha256!=current.AuthoritySha256 || current.PublisherKeyId!=local.Attempt.PublisherKeyId ||
            authority.Controller(transaction)!=local.Attempt.Controller || ReadAccessAttempt(capture,current.AccountRevision,transaction)!=local.Attempt)
            throw new StaleLibraryConfigurationException();
    }
}
