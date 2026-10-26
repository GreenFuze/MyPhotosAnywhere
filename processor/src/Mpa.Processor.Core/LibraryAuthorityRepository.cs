using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record LibraryControllerPin(Guid LibraryId, Guid ActorId, long Epoch, string AccountId, string FolderId, string PublicKeySpki, string KeyId)
{
    /// <summary>Exports only this independently approved public pin. Receiving the code
    /// is not enrollment, and never grants access to credentials or original writes.</summary>
    public LibraryConnectionDescriptor ConnectionDescriptor()
    {
        var descriptor = new LibraryConnectionDescriptor(LibraryId, ActorId, Epoch, new(AccountId, FolderId), Convert.FromBase64String(PublicKeySpki));
        if (descriptor.ControllerKeyId != KeyId) throw new InvalidDataException("Pinned controller fingerprint changed during connection export.");
        return descriptor;
    }
}
public sealed class ControllerAuthorityConflictException() : InvalidOperationException("Controller authority conflicts with previously accepted library state. Retain the agreed state and reconcile explicitly.");

/// <summary>Serial local public trust/selection store. Bootstrap is exclusively for an
/// independently approved host/device setup, never a received Drive descriptor. Native
/// private-key persistence/possession must be established by that setup owner first.
/// No key replacement, source permission, original mutation or catalog activation occurs here.</summary>
public sealed partial class LibraryAuthorityRepository(ProcessorStateDatabase database, StorageBudgetManager? budget = null)
{
    private const string PinKey = "library_controller_v1";
    private const string SelectedKey = "current_publisher_policy_v1";
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager? _budget = budget;
    private StorageBudgetManager Budget => _budget ?? throw new InvalidOperationException("This authority reader has no write budget; use its configured write owner for mutations.");
    private static readonly string[] ControllerKinds = ["library-publisher-authorization", "library-source-configuration"];

    public LibraryControllerPin? Controller() => ReadPin(null);
    internal LibraryControllerPin? Controller(SqliteTransaction? transaction) => ReadPin(transaction);

    public LibraryControllerPin Bootstrap(Guid actorId, long epoch, CatalogWorkspace selectedWorkspace,
        long expectedAccountRevision, ReadOnlySpan<byte> persistedNativePublicKey, PublisherSigningRegistration? signingRegistration = null, LibraryBootstrapApproval? sourceApproval = null)
    {
        _ = Budget;
        ArgumentNullException.ThrowIfNull(selectedWorkspace);
        if (actorId == Guid.Empty || epoch < 1 || expectedAccountRevision < 1 || selectedWorkspace.FolderId == "root")
            throw new ArgumentException("Independently approved controller, workspace and account revision are required.");
        using var verifier = new ProtocolDocumentVerifier(persistedNativePublicKey);
        using var transaction = _database.Connection.BeginTransaction(); var library = LibraryId(transaction);
        var candidate = new LibraryControllerPin(library, actorId, epoch, selectedWorkspace.AccountId, selectedWorkspace.FolderId,
            Convert.ToBase64String(persistedNativePublicKey), verifier.KeyId);
        var existing = ReadPin(transaction);
        if (existing is not null)
        {
            if (candidate != existing) throw new ControllerAuthorityConflictException();
            if (signingRegistration is not null) new ProtocolSigningJournal(_database, Budget).AssertBootstrapActors(transaction, existing, signingRegistration);
            using var sourceReservation = sourceApproval is null ? null : StoreBootstrapSources(transaction, existing, sourceApproval);
            transaction.Commit(); return existing;
        }
        using (var account = Command(transaction, "SELECT 1 FROM drive_account WHERE permission_id=$account AND connected=1 AND revision=$revision",
            ("$account", selectedWorkspace.AccountId), ("$revision", expectedAccountRevision)))
            if (account.ExecuteScalar() is null) throw new StaleLibraryConfigurationException();
        var encoded = EncodePin(candidate); using var reservation = Budget.Reserve(65536); reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(transaction, PinKey, encoded); Insert(transaction, PinKey + ":sha256", Hash(Encoding.UTF8.GetBytes(encoded)));
        if (signingRegistration is not null) new ProtocolSigningJournal(_database, Budget).InitializeBootstrapActors(transaction, candidate, signingRegistration);
        using var bootstrapSourceReservation = sourceApproval is null ? null : StoreBootstrapSources(transaction, candidate, sourceApproval);
        transaction.Commit(); return candidate;
    }

    public CatalogPublisherAuthorization? CurrentPublisherPolicy()
    {
        using var transaction = _database.Connection.BeginTransaction(); var pin = ReadPin(transaction); var pointer = SelectedSequence(transaction);
        if (pointer is null) { transaction.Commit(); return null; }
        if (pin is null) throw new InvalidDataException("Selected publisher policy has no pinned controller.");
        var sequence = Counter(pointer); var current = ReadPolicy(transaction, pin, sequence);
        transaction.Commit(); return current;
    }

    /// <summary>Return the selected policy, including revocation. Previously accepted
    /// older bytes are idempotent but never rewind selection. Same sequence/different
    /// bytes is a conflict. Controller sequence allocation across other document kinds
    /// remains the enrollment/operation owner's separate responsibility.</summary>
    public CatalogPublisherAuthorization SelectPublisherPolicy(ReadOnlySpan<byte> signedEnvelope)
    {
        _ = Budget;
        if (signedEnvelope.Length is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes) throw new ArgumentException("A bounded signed policy is required.", nameof(signedEnvelope));
        using var transaction = _database.Connection.BeginTransaction();
        using var selected = SelectPublisherPolicyIn(signedEnvelope,transaction);
        selected.Recheck();transaction.Commit();return selected.Value;
    }

    internal LibraryPolicySelection<CatalogPublisherAuthorization> SelectPublisherPolicyIn(ReadOnlySpan<byte> signedEnvelope,SqliteTransaction transaction)
    {
        _ = Budget;
        if (signedEnvelope.Length is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes) throw new ArgumentException("A bounded signed policy is required.", nameof(signedEnvelope));
        var owned = signedEnvelope.ToArray();
        var pin = ReadPin(transaction) ?? throw new InvalidOperationException("Independently enroll the controller before accepting Drive policies.");
        var policy = Reader(pin).ReadPublisherAuthorization(owned, Actor(pin)); var sequence = policy.Sequence.ToString(CultureInfo.InvariantCulture);
        AssertControllerSequenceKind(transaction, policy.Sequence, source: false);
        var key = DocumentKey(policy.Sequence); var old = ReadMeta(transaction, key, ProtocolDocumentReader.MaximumDocumentBytes);
        var oldHash = ReadMeta(transaction, key + ":sha256", 64); var hash = Hash(owned);
        if ((old is null) != (oldHash is null)) throw new InvalidDataException("Controller document integrity record is incomplete.");
        if (old is not null && Hash(Encoding.UTF8.GetBytes(old)) != oldHash) throw new InvalidDataException("Stored controller document integrity failed.");
        if (old is not null && oldHash != hash) throw new ControllerAuthorityConflictException();
        var selected = SelectedSequence(transaction); var currentSequence = selected is null ? 0 : Counter(selected);
        if (currentSequence > 0) _ = ReadPolicy(transaction, pin, currentSequence);
        if (old is not null && policy.Sequence <= currentSequence) return new(ReadPolicy(transaction,pin,currentSequence),null);
        StorageReservation? reservation = Budget.Reserve(checked(owned.LongLength * 3 + 65536));
        try
        {
            reservation.RecheckBeforeWrite(reservation.Bytes);
            if (old is null) { Insert(transaction, key, Encoding.UTF8.GetString(owned)); Insert(transaction, key + ":sha256", hash); }
            CatalogPublisherAuthorization result;
            if (policy.Sequence > currentSequence)
            {
                using var update = Command(transaction, "INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    ("$key", SelectedKey), ("$value", sequence));
                if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Publisher selection did not update exactly one pointer.");
                result = policy;
            }
            else result = ReadPolicy(transaction,pin,currentSequence);
            var mutation = new LibraryPolicySelection<CatalogPublisherAuthorization>(result,reservation);reservation=null;return mutation;
        }
        finally {reservation?.Dispose();}
    }

    private CatalogPublisherAuthorization ReadPolicy(SqliteTransaction transaction, LibraryControllerPin pin, long sequence)
    {
        AssertControllerSequenceKind(transaction, sequence, source: false);
        var key = DocumentKey(sequence); var text = ReadMeta(transaction, key, ProtocolDocumentReader.MaximumDocumentBytes);
        var hash = ReadMeta(transaction, key + ":sha256", 64);
        if (text is null || hash is null || hash != Hash(Encoding.UTF8.GetBytes(text))) throw new InvalidDataException("Selected publisher document is missing or corrupt.");
        try
        {
            var policy = Reader(pin).ReadPublisherAuthorization(Encoding.UTF8.GetBytes(text), Actor(pin));
            if (policy.Sequence != sequence) throw new InvalidDataException("Selected publisher sequence does not match its captured document.");
            return policy;
        }
        catch (ProtocolFormatException error) { throw new InvalidDataException("Stored publisher authorization is invalid; accepted state was preserved.", error); }
    }

    private LibraryControllerPin? ReadPin(SqliteTransaction? transaction)
    {
        var text = ReadMeta(transaction, PinKey, 4096); var hash = ReadMeta(transaction, PinKey + ":sha256", 64);
        if (text is null && hash is null)
        {
            if (ReadMeta(transaction, SelectedKey, 19) is not null || HasPublisherHistory(transaction) || HasSourceHistory(transaction) || HasBootstrapSources(transaction) || HasCurrentControlState(transaction))
                throw new InvalidDataException("Accepted publisher state has lost its pinned controller; never bootstrap a replacement.");
            return null;
        }
        return DecodeControllerPin(text, hash, LibraryId(transaction));
    }

    internal static LibraryControllerPin DecodeControllerPin(string? text, string? hash, Guid library)
    {
        if (text is null || hash is null || hash != Hash(Encoding.UTF8.GetBytes(text))) throw new InvalidDataException("Pinned library controller is incomplete or corrupt.");
        try
        {
            var pin = JsonSerializer.Deserialize<LibraryControllerPin>(text) ?? throw new InvalidDataException("Pinned library controller is missing.");
            if (pin.LibraryId != library || pin.ActorId == Guid.Empty || pin.Epoch < 1 || pin.AccountId is null ||
                pin.FolderId is null or "root" || pin.KeyId is not { Length: 64 } || EncodePin(pin) != text)
                throw new InvalidDataException("Pinned controller does not match this library or its canonical encoding.");
            _ = new CatalogWorkspace(pin.AccountId, pin.FolderId);
            if (pin.PublicKeySpki is null || pin.PublicKeySpki.Length is < 1 or > 684) throw new InvalidDataException("Pinned controller public key exceeds its bound.");
            var bytes = Convert.FromBase64String(pin.PublicKeySpki);
            using var verifier = new ProtocolDocumentVerifier(bytes);
            if (Convert.ToBase64String(bytes) != pin.PublicKeySpki || verifier.KeyId != pin.KeyId) throw new InvalidDataException("Pinned controller key identity does not match.");
            return pin;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
        { throw new InvalidDataException("Pinned controller cannot be decoded; never substitute another root key.", error); }
    }

    private Guid LibraryId(SqliteTransaction? transaction)
    {
        var value = ReadMeta(transaction, "library_id", 36);
        if (value is null || !Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new InvalidDataException("Controller enrollment requires this installation's established library identity.");
        return id;
    }
    private string? SelectedSequence(SqliteTransaction transaction)
    {
        var selected = ReadMeta(transaction, SelectedKey, 19);
        var highest = HighestPublisherSequence(transaction);
        if (selected is null && highest != 0 || selected is not null && Counter(selected) != highest)
            throw new InvalidDataException("Publisher selection disagrees with accepted history; never reactivate an older policy.");
        return selected;
    }
    private bool HasCurrentControlState(SqliteTransaction? transaction)
    {
        using var command = Command(transaction,"SELECT 1 FROM processor_meta WHERE key GLOB 'current_control_*' LIMIT 1");
        return command.ExecuteScalar() is not null;
    }
    private bool HasPublisherHistory(SqliteTransaction? transaction)
    {
        // Publisher and source documents have separate selected pointers and share the
        // enrolled controller's sequence namespace. Other roles require explicit admission.
        using var command = Command(transaction, "SELECT 1 FROM processor_meta WHERE key GLOB 'controller_document_v1:*' LIMIT 1");
        return command.ExecuteScalar() is not null;
    }
    private long HighestPublisherSequence(SqliteTransaction transaction)
    {
        using var command = Command(transaction, "SELECT key FROM processor_meta WHERE key GLOB 'controller_document_v1:*'");
        using var reader = command.ExecuteReader(); long highest = 0;
        while (reader.Read())
        {
            var key = reader.GetString(0); var suffix = key["controller_document_v1:".Length..];
            if (suffix.EndsWith(":sha256", StringComparison.Ordinal)) suffix = suffix[..^7];
            highest = Math.Max(highest, Counter(suffix));
        }
        return highest;
    }
    private string? ReadMeta(SqliteTransaction? transaction, string key, int maximumCharacters)
    {
        using var command = Command(transaction, "SELECT length(value),value FROM processor_meta WHERE key=$key", ("$key", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(0) is < 1 || reader.GetInt64(0) > maximumCharacters) throw new InvalidDataException("Authority metadata exceeds its bound.");
        return reader.GetString(1);
    }
    private void Insert(SqliteTransaction transaction, string key, string value)
    {
        using var command = Command(transaction, "INSERT INTO processor_meta(key,value) VALUES($key,$value)", ("$key", key), ("$value", value));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Controller state did not insert exactly one record.");
    }
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Key, object Value)[] values)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var pair in values) command.Parameters.AddWithValue(pair.Key, pair.Value);
        return command;
    }
    private static CatalogPublicationReader Reader(LibraryControllerPin pin) => new(new(pin.AccountId, pin.FolderId));
    private static TrustedProtocolActor Actor(LibraryControllerPin pin) => new(pin.LibraryId, pin.ActorId, pin.Epoch, Convert.FromBase64String(pin.PublicKeySpki), ControllerKinds);
    private static string EncodePin(LibraryControllerPin pin) => Encoding.UTF8.GetString(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(pin)));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string DocumentKey(long sequence) => "controller_document_v1:" + sequence.ToString(CultureInfo.InvariantCulture);
    private static long Counter(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 && value.ToString(CultureInfo.InvariantCulture) == text
        ? value : throw new InvalidDataException("Stored controller sequence is not canonical.");
}
