using Microsoft.Data.Sqlite;

namespace Mpa.Processor.Core;

public sealed record DriveAccountRecord(string PermissionId, Guid ConnectionId, string DisplayName, string? EmailAddress, bool Connected, long Revision);
public sealed record PendingDriveConnection(Guid Id, string? ExpectedPermissionId, long? ExpectedRevision);
public sealed record CredentialCleanupRecord(Guid ConnectionId, string Reason);
public sealed class StaleDriveConnectionException() : InvalidOperationException("The account changed during setup. Existing bindings and credentials were preserved; restart this connection attempt.");
public sealed class DriveAccountAlreadyConnectedException() : InvalidOperationException("This Google account already exists. Reconnect its existing entry instead of creating a second binding.");

/// <summary>Serial background state only. Tokens remain in the OS vault. Network/consent never
/// runs inside these transactions. Stable permission IDs survive connection replacement.</summary>
public sealed class DriveAccountRepository(ProcessorStateDatabase database)
{
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public IReadOnlyList<DriveAccountRecord> Accounts()
    {
        using var command = Command(null, "SELECT permission_id,connection_id,display_name,email_address,connected,revision FROM drive_account ORDER BY permission_id LIMIT 129");
        using var reader = command.ExecuteReader();
        var records = new List<DriveAccountRecord>();
        while (reader.Read()) records.Add(new(reader.GetString(0), Guid.ParseExact(reader.GetString(1), "D"), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5)));
        if (records.Count > 128) throw new InvalidDataException("Processor account count exceeds the supported bound.");
        return records.AsReadOnly();
    }

    public IReadOnlyList<PendingDriveConnection> PendingConnections()
    {
        using var command = Command(null, "SELECT id,expected_permission_id,expected_revision FROM pending_drive_connection ORDER BY id LIMIT 33");
        using var reader = command.ExecuteReader();
        var records = new List<PendingDriveConnection>();
        while (reader.Read()) records.Add(new(Guid.ParseExact(reader.GetString(0), "D"), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        if (records.Count > 32) throw new InvalidDataException("Processor setup attempt count exceeds the supported bound.");
        return records.AsReadOnly();
    }

    public PendingDriveConnection Begin(Guid connectionId, string? expectedPermissionId = null)
    {
        if (connectionId == Guid.Empty || expectedPermissionId is not null && !Identity(expectedPermissionId)) throw new ArgumentException("Invalid connection/account identity.");
        if (PendingConnections().Count >= 32) throw new InvalidOperationException("Finish or dismiss an earlier account setup before starting another.");
        using (var cleanup = Command(null, "SELECT 1 FROM credential_cleanup WHERE connection_id=$id", ("$id", connectionId.ToString("D"))))
            if (cleanup.ExecuteScalar() is not null) throw new ArgumentException("A retired credential identity cannot be reused.", nameof(connectionId));
        var accounts = Accounts();
        if (accounts.Any(account => account.ConnectionId == connectionId)) throw new ArgumentException("A connection identity cannot be reused.", nameof(connectionId));
        if (expectedPermissionId is null && accounts.Count >= 128) throw new InvalidOperationException("Maximum Processor account count reached.");
        var previous = expectedPermissionId is null ? null : accounts.SingleOrDefault(account => account.PermissionId == expectedPermissionId)
            ?? throw new ArgumentException("The account to reconnect does not exist.", nameof(expectedPermissionId));
        using var command = Command(null, "INSERT INTO pending_drive_connection VALUES($id,$expected,$revision)",
            ("$id", connectionId.ToString("D")), ("$expected", expectedPermissionId), ("$revision", previous?.Revision));
        command.ExecuteNonQuery();
        return new(connectionId, expectedPermissionId, previous?.Revision);
    }

    public DriveAccountRecord Promote(Guid pendingId, string verifiedPermissionId, string displayName, string? emailAddress)
    {
        if (!Identity(verifiedPermissionId) || !Display(displayName) || emailAddress is not null && !Display(emailAddress)) throw new ArgumentException("Invalid verified account fields.");
        var pending = PendingConnections().SingleOrDefault(item => item.Id == pendingId) ?? throw new StaleDriveConnectionException();
        if (pending.ExpectedPermissionId is not null && pending.ExpectedPermissionId != verifiedPermissionId) throw new StaleDriveConnectionException();
        var current = Accounts().SingleOrDefault(account => account.PermissionId == verifiedPermissionId);
        if (pending.ExpectedPermissionId is null && current is not null) throw new DriveAccountAlreadyConnectedException();
        if (pending.ExpectedPermissionId is not null && (current is null || current.Revision != pending.ExpectedRevision)) throw new StaleDriveConnectionException();
        using var transaction = _database.Connection.BeginTransaction();
        if (current is null)
        {
            using var insert = Command(transaction, "INSERT INTO drive_account VALUES($permission,$connection,$name,$email,1,1)",
                ("$permission", verifiedPermissionId), ("$connection", pendingId.ToString("D")), ("$name", displayName), ("$email", emailAddress));
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = Command(transaction, "UPDATE drive_account SET connection_id=$connection,display_name=$name,email_address=$email,connected=1,revision=revision+1 WHERE permission_id=$permission AND revision=$revision",
                ("$connection", pendingId.ToString("D")), ("$name", displayName), ("$email", emailAddress), ("$permission", verifiedPermissionId), ("$revision", current.Revision));
            if (update.ExecuteNonQuery() != 1) throw new StaleDriveConnectionException();
            QueueCleanup(transaction, current.ConnectionId, "reconnected");
        }
        using (var remove = Command(transaction, "DELETE FROM pending_drive_connection WHERE id=$id", ("$id", pendingId.ToString("D"))))
            if (remove.ExecuteNonQuery() != 1) throw new StaleDriveConnectionException();
        transaction.Commit();
        return new(verifiedPermissionId, pendingId, displayName, emailAddress, true, current is null ? 1 : checked(current.Revision + 1));
    }

    public void Disconnect(string permissionId, long expectedRevision)
    {
        var account = Accounts().SingleOrDefault(account => account.PermissionId == permissionId) ?? throw new StaleDriveConnectionException();
        if (account.Revision != expectedRevision) throw new StaleDriveConnectionException();
        using var transaction = _database.Connection.BeginTransaction();
        using (var update = Command(transaction, "UPDATE drive_account SET connected=0,revision=revision+1 WHERE permission_id=$permission AND revision=$revision",
            ("$permission", permissionId), ("$revision", expectedRevision)))
            if (update.ExecuteNonQuery() != 1) throw new StaleDriveConnectionException();
        QueueCleanup(transaction, account.ConnectionId, "disconnected");
        transaction.Commit();
    }

    public void Abandon(Guid pendingId)
    {
        using var transaction = _database.Connection.BeginTransaction();
        using var remove = Command(transaction, "DELETE FROM pending_drive_connection WHERE id=$id", ("$id", pendingId.ToString("D")));
        if (remove.ExecuteNonQuery() != 1) throw new StaleDriveConnectionException();
        QueueCleanup(transaction, pendingId, "abandoned");
        transaction.Commit();
    }

    public IReadOnlyList<CredentialCleanupRecord> PendingCleanup()
    {
        using var command = Command(null, "SELECT connection_id,reason FROM credential_cleanup ORDER BY connection_id LIMIT 64");
        using var reader = command.ExecuteReader();
        var records = new List<CredentialCleanupRecord>();
        while (reader.Read()) records.Add(new(Guid.ParseExact(reader.GetString(0), "D"), reader.GetString(1)));
        return records.AsReadOnly();
    }
    public void ConfirmCleanup(Guid connectionId)
    {
        AssertCleanupAllowed(connectionId);
        using var remove = Command(null, "DELETE FROM credential_cleanup WHERE connection_id=$id", ("$id", connectionId.ToString("D")));
        if (remove.ExecuteNonQuery() != 1) throw new InvalidOperationException("Unknown credential cleanup acknowledgement.");
    }
    public void AssertCleanupAllowed(Guid connectionId)
    {
        if (Accounts().Any(account => account.Connected && account.ConnectionId == connectionId) || PendingConnections().Any(pending => pending.Id == connectionId))
            throw new InvalidOperationException("An active connection cannot be acknowledged as removed.");
        using var find = Command(null, "SELECT 1 FROM credential_cleanup WHERE connection_id=$id", ("$id", connectionId.ToString("D")));
        if (find.ExecuteScalar() is null) throw new InvalidOperationException("Unknown credential cleanup acknowledgement.");
    }

    private void QueueCleanup(SqliteTransaction transaction, Guid id, string reason)
    {
        using var command = Command(transaction, "INSERT INTO credential_cleanup VALUES($id,$reason) ON CONFLICT(connection_id) DO NOTHING", ("$id", id.ToString("D")), ("$reason", reason));
        command.ExecuteNonQuery();
    }
    private SqliteCommand Command(SqliteTransaction? transaction, string text, params (string Name, object? Value)[] parameters)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = text;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }
    private static bool Identity(string text) => text.Length is > 0 and <= 256 && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool Display(string text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 512 && !text.Any(char.IsControl);
}
