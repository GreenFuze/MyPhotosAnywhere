using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

/// <summary>Borrowed proof of complete initial-policy verification in one live SQL
/// transaction. It owns no database/transaction and cannot cross an I/O boundary.
/// Public reads still reconstruct all policy and signing evidence independently.</summary>
internal sealed class InitialPolicyReadSnapshot
{
    private readonly ProcessorStateDatabase _database;
    private readonly SqliteTransaction _transaction;
    private readonly LibraryInitialPublicationCompletion _completion;

    private InitialPolicyReadSnapshot(ProcessorStateDatabase database,SqliteTransaction transaction,LibraryInitialPublicationCompletion completion)
    { _database=database;_transaction=transaction;_completion=completion; }

    internal static InitialPolicyReadSnapshot Capture(ProcessorStateDatabase database,StorageBudgetManager budget,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(database);ArgumentNullException.ThrowIfNull(budget);ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection,database.Connection)) throw new InvalidOperationException("Initial-policy verification requires its owning live SQL transaction.");
        var completion=new LibraryAuthorityRepository(database,budget).InitialPublicationCompletion(transaction)
            ?? throw new InvalidOperationException("Verify the original policy completion before current observations.");
        return new(database,transaction,completion);
    }

    internal LibraryInitialPublicationCompletion Require(ProcessorStateDatabase database,SqliteTransaction transaction)
    {
        if (!ReferenceEquals(_database,database) || !ReferenceEquals(_transaction,transaction) || !ReferenceEquals(transaction.Connection,database.Connection))
            throw new InvalidOperationException("Initial-policy evidence cannot be reused outside its owning live SQL transaction.");
        return _completion;
    }
}
