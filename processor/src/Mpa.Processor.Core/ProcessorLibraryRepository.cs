namespace Mpa.Processor.Core;

/// <summary>Serial state work beneath the installation lease. Local library identity is
/// created once, before private keys; a malformed stored identity is never reset.</summary>
public sealed class ProcessorLibraryRepository(ProcessorStateDatabase database)
{
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    public Guid GetOrCreateIdentity()
    {
        using var transaction = _database.Connection.BeginTransaction();
        using var command = _database.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM processor_meta WHERE key='library_id'";
        var stored = command.ExecuteScalar();
        if (stored is not null)
        {
            if (stored is not string value || !Guid.TryParseExact(value, "D", out var existing) || existing == Guid.Empty)
                throw new InvalidDataException("The stored library identity is invalid. Existing state and private keys were preserved.");
            transaction.Commit(); return existing;
        }
        var identity = Guid.NewGuid();
        command.CommandText = "INSERT INTO processor_meta(key,value) VALUES('library_id',$id)";
        command.Parameters.AddWithValue("$id", identity.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Library identity creation did not commit exactly one record.");
        transaction.Commit(); return identity;
    }
}
