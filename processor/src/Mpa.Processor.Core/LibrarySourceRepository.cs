using System.Globalization;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record LibrarySourceRecord(Guid Id, string AccountId, string FolderId, string DisplayName,
    bool IncludeDescendants, bool Enabled, long Revision, bool AccountConnected);
public sealed record LibrarySourceSnapshot(long Revision, IReadOnlyList<LibrarySourceRecord> Sources);
public sealed record PendingConfiguration(long Revision, string CanonicalSnapshot);
public sealed class StaleLibraryConfigurationException() : InvalidOperationException("Library configuration changed. Refresh its current revision before applying this request.");
public sealed class ExistingLibrarySourceException() : InvalidOperationException("This Google account/folder already has a source binding. Update its existing entry.");

/// <summary>Serial SQLite owner. Source changes never mutate Drive, erase indexed assets or
/// retarget captured jobs. Global expected revisions fence concurrent clients; a durable
/// publication snapshot commits in the same transaction as every accepted change.</summary>
public sealed class LibrarySourceRepository(ProcessorStateDatabase database)
{
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    public LibrarySourceSnapshot Snapshot()
    {
        using var revision = Command(null, "SELECT revision FROM library_configuration WHERE id=1");
        var value = revision.ExecuteScalar() ?? throw new InvalidDataException("The library configuration revision is missing.");
        return new(Convert.ToInt64(value, CultureInfo.InvariantCulture), Sources(null));
    }

    public LibrarySourceSnapshot Add(Guid id, string accountId, long expectedAccountRevision, string verifiedFolderId,
        string verifiedDisplayName, bool includeDescendants, long expectedConfigurationRevision)
    {
        if (id == Guid.Empty || !Identity(accountId) || !Identity(verifiedFolderId) || !Display(verifiedDisplayName) || expectedAccountRevision < 1)
            throw new ArgumentException("Invalid verified source binding.");
        using var transaction = _database.Connection.BeginTransaction();
        using (var duplicate = Command(transaction, "SELECT 1 FROM source_binding WHERE account_id=$account AND folder_id=$folder", ("$account", accountId), ("$folder", verifiedFolderId)))
            if (duplicate.ExecuteScalar() is not null) throw new ExistingLibrarySourceException();
        using (var account = Command(transaction, "SELECT 1 FROM drive_account WHERE permission_id=$account AND connected=1 AND revision=$revision", ("$account", accountId), ("$revision", expectedAccountRevision)))
            if (account.ExecuteScalar() is null) throw new StaleLibraryConfigurationException();
        using (var count = Command(transaction, "SELECT count(*) FROM source_binding"))
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= 256) throw new InvalidOperationException("Maximum library source count reached. Retired source identities cannot be silently reused.");
        var revision = Advance(transaction, expectedConfigurationRevision);
        using (var insert = Command(transaction, "INSERT INTO source_binding VALUES($id,$account,$folder,$name,$descendants,1,1)",
            ("$id", id.ToString("D")), ("$account", accountId), ("$folder", verifiedFolderId), ("$name", verifiedDisplayName), ("$descendants", includeDescendants)))
            if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Source insertion did not affect exactly one record.");
        var snapshot = Capture(transaction, revision); transaction.Commit(); return snapshot;
    }

    public LibrarySourceSnapshot Update(Guid id, bool enabled, bool includeDescendants, long expectedSourceRevision, long expectedConfigurationRevision)
    {
        if (id == Guid.Empty || expectedSourceRevision < 1 || expectedSourceRevision == long.MaxValue) throw new ArgumentException("Invalid captured source revision.");
        using var transaction = _database.Connection.BeginTransaction();
        var revision = Advance(transaction, expectedConfigurationRevision);
        using (var update = Command(transaction, "UPDATE source_binding SET enabled=$enabled,descendants=$descendants,revision=revision+1 WHERE id=$id AND revision=$revision",
            ("$enabled", enabled), ("$descendants", includeDescendants), ("$id", id.ToString("D")), ("$revision", expectedSourceRevision)))
            if (update.ExecuteNonQuery() != 1) throw new StaleLibraryConfigurationException();
        var snapshot = Capture(transaction, revision); transaction.Commit(); return snapshot;
    }

    public IReadOnlyList<PendingConfiguration> PendingPublications()
    {
        using var command = Command(null, "SELECT revision,snapshot FROM configuration_outbox WHERE published=0 ORDER BY revision LIMIT 32");
        using var reader = command.ExecuteReader(); var pending = new List<PendingConfiguration>();
        while (reader.Read()) pending.Add(new(reader.GetInt64(0), reader.GetString(1)));
        return pending.AsReadOnly();
    }
    public void ConfirmPublished(long revision, string exactCanonicalSnapshot)
    {
        ArgumentNullException.ThrowIfNull(exactCanonicalSnapshot);
        using var command = Command(null, "UPDATE configuration_outbox SET published=1 WHERE revision=$revision AND snapshot=$snapshot AND published=0",
            ("$revision", revision), ("$snapshot", exactCanonicalSnapshot));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Configuration publication acknowledgment does not match an exact pending snapshot.");
    }
    private long Advance(SqliteTransaction transaction, long expectedRevision)
    {
        if (expectedRevision is < 1 or >= long.MaxValue) throw new ArgumentException("Invalid captured configuration revision.", nameof(expectedRevision));
        using var command = Command(transaction, "UPDATE library_configuration SET revision=revision+1 WHERE id=1 AND revision=$expected", ("$expected", expectedRevision));
        if (command.ExecuteNonQuery() != 1) throw new StaleLibraryConfigurationException();
        return expectedRevision + 1;
    }
    private LibrarySourceSnapshot Capture(SqliteTransaction transaction, long revision)
    {
        var sources = Sources(transaction);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa-source-configuration", major = 1, revision = revision.ToString(CultureInfo.InvariantCulture),
            sources = sources.Select(source => new { id = source.Id.ToString("D"), accountId = source.AccountId, folderId = source.FolderId,
                displayName = source.DisplayName, descendants = source.IncludeDescendants, enabled = source.Enabled,
                revision = source.Revision.ToString(CultureInfo.InvariantCulture) })
        });
        var canonical = Encoding.UTF8.GetString(new CanonicalJson(1024 * 1024).Encode(bytes));
        using var command = Command(transaction, "INSERT INTO configuration_outbox(revision,snapshot) VALUES($revision,$snapshot)", ("$revision", revision), ("$snapshot", canonical));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Configuration outbox did not commit exactly one publication snapshot.");
        return new(revision, sources);
    }
    private ReadOnlyCollection<LibrarySourceRecord> Sources(SqliteTransaction? transaction)
    {
        using var command = Command(transaction, """
            SELECT s.id,s.account_id,s.folder_id,s.display_name,s.descendants,s.enabled,s.revision,a.connected
            FROM source_binding s JOIN drive_account a ON a.permission_id=s.account_id ORDER BY s.id LIMIT 257
            """);
        using var reader = command.ExecuteReader(); var sources = new List<LibrarySourceRecord>();
        while (reader.Read()) sources.Add(new(Guid.ParseExact(reader.GetString(0), "D"), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt64(6), reader.GetBoolean(7)));
        if (sources.Count > 256) throw new InvalidDataException("Stored library source count exceeds its supported bound.");
        return sources.AsReadOnly();
    }
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static bool Identity(string value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool Display(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
}
