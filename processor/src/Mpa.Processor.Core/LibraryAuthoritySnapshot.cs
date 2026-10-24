namespace Mpa.Processor.Core;

public sealed partial class LibraryAuthorityRepository
{
    /// <summary>Read only from the caller's existing WAL transaction. Looking up today's
    /// writable state here would change the meaning of an earlier coverage observation.</summary>
    internal static LibraryControllerPin? SnapshotController(ProcessorStateReadSnapshot snapshot, Guid library)
    {
        var text = Read(PinKey, 4096); var hash = Read(PinKey + ":sha256", 64);
        if (text is not null || hash is not null) return DecodeControllerPin(text, hash, library);
        using var history = snapshot.Connection.CreateCommand(); history.Transaction = snapshot.Transaction;
        history.CommandText = """
            SELECT 1 FROM processor_meta WHERE key IN ($publishers,$sources,$approval,$approvalHash)
              OR key GLOB 'controller_document_v1:*' OR key GLOB 'controller_source_document_v1:*' LIMIT 1
            """;
        history.Parameters.AddWithValue("$publishers", SelectedKey); history.Parameters.AddWithValue("$sources", SelectedSourceKey);
        history.Parameters.AddWithValue("$approval", BootstrapSourcesKey); history.Parameters.AddWithValue("$approvalHash", BootstrapSourcesKey + ":sha256");
        if (history.ExecuteScalar() is not null) throw new InvalidDataException("Captured accepted authority lost its pinned controller.");
        return null;

        string? Read(string key, int limit)
        {
            using var command = snapshot.Connection.CreateCommand(); command.Transaction = snapshot.Transaction;
            command.CommandText = "SELECT length(value),value FROM processor_meta WHERE key=$key"; command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
            if (reader.GetInt64(0) is < 1 || reader.GetInt64(0) > limit) throw new InvalidDataException("Captured authority exceeds its bound.");
            return reader.GetString(1);
        }
    }
}
