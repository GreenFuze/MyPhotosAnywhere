using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

/// <summary>Owned read-only WAL snapshot. Open under the source coordinator, then transfer
/// this owner to a background reader. No source write and no long coordinator lease is held.</summary>
public sealed class ProcessorStateReadSnapshot : IDisposable
{
    private readonly Action<ProcessorStateReadSnapshot> _release;
    internal SqliteConnection Connection { get; }
    internal SqliteTransaction Transaction { get; }
    private bool _closed;
    private ProcessorStateReadSnapshot(SqliteConnection connection, SqliteTransaction transaction, Action<ProcessorStateReadSnapshot> release)
    { Connection = connection; Transaction = transaction; _release = release; }
    internal static ProcessorStateReadSnapshot Open(string path, Action<ProcessorStateReadSnapshot> release)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        SqliteTransaction? transaction = null;
        try
        {
            connection.Open();
            using (var settings = connection.CreateCommand()) { settings.CommandText = "PRAGMA query_only=ON; PRAGMA cache_size=-2048; PRAGMA temp_store=FILE;"; settings.ExecuteNonQuery(); }
            transaction = connection.BeginTransaction(deferred: true);
            using var anchor = connection.CreateCommand(); anchor.Transaction = transaction;
            anchor.CommandText = "SELECT revision FROM discovery_revision WHERE id=1";
            if (anchor.ExecuteScalar() is not long revision || revision < 0) throw new InvalidDataException("The source snapshot cannot anchor its catalog revision.");
            return new(connection, transaction, release);
        }
        catch { try { transaction?.Dispose(); } finally { connection.Dispose(); } throw; }
    }
    public void Dispose()
    {
        if (_closed) return;
        Transaction.Dispose(); Connection.Dispose(); _release(this); _closed = true;
    }
}
