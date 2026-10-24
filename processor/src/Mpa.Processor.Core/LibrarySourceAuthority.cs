using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record AcceptedSourceConfiguration(long Sequence, string EnvelopeSha256, CatalogSourceConfiguration Configuration);

public sealed partial class LibraryAuthorityRepository
{
    private const string SourceDocumentPrefix = "controller_source_document_v1:";
    private const string SelectedSourceKey = "current_source_configuration_v1";
    private static readonly string[] SourceCapabilities = [CatalogSourceConfiguration.Capability];

    /// <summary>Accept only an independently enrolled controller's exact signed policy.
    /// This grants no permission to sign local settings or mutate originals. The caller
    /// still owns dependency reconciliation and the publication barrier.</summary>
    public AcceptedSourceConfiguration SelectSourceConfiguration(ReadOnlySpan<byte> signedEnvelope)
    {
        _ = Budget;
        if (signedEnvelope.Length is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes)
            throw new ArgumentException("A bounded signed source policy is required.", nameof(signedEnvelope));
        var owned = signedEnvelope.ToArray(); using var transaction = _database.Connection.BeginTransaction();
        var pin = ReadPin(transaction) ?? throw new InvalidOperationException("Independently approve library setup before accepting source policies.");
        var incoming = DecodeSources(pin, owned); var key = SourceDocumentKey(incoming.Sequence);
        AssertControllerSequenceKind(transaction, incoming.Sequence, source: true);
        var current = ReadCurrentSources(transaction, pin); var previous = ReadMeta(transaction, key, ProtocolDocumentReader.MaximumDocumentBytes);
        var previousHash = ReadMeta(transaction, key + ":sha256", 64);
        if ((previous is null) != (previousHash is null)) throw new InvalidDataException("Accepted source integrity record is incomplete.");
        if (previous is not null)
        {
            var retained = ReadSources(transaction, pin, incoming.Sequence);
            if (retained.EnvelopeSha256 != incoming.EnvelopeSha256 || previous != Encoding.UTF8.GetString(owned)) throw new ControllerAuthorityConflictException();
            transaction.Commit(); return current ?? throw new InvalidDataException("Accepted source history lost its current policy.");
        }
        if (current is not null)
        {
            var higherSequence = incoming.Sequence > current.Sequence;
            if (higherSequence && incoming.Configuration.Revision < current.Configuration.Revision ||
                !higherSequence && incoming.Configuration.Revision >= current.Configuration.Revision ||
                higherSequence && incoming.Configuration.Revision == current.Configuration.Revision &&
                SourcePayload(pin, owned) != SourcePayload(pin, StoredSourceEnvelope(transaction, current.Sequence)))
                throw new ControllerAuthorityConflictException();
        }
        using var reservation = Budget.Reserve(checked(owned.LongLength * 3 + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        Insert(transaction, key, Encoding.UTF8.GetString(owned)); Insert(transaction, key + ":sha256", incoming.EnvelopeSha256);
        var selected = current is null || incoming.Sequence > current.Sequence;
        if (selected)
        {
            using var update = Command(transaction, "INSERT INTO processor_meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$key", SelectedSourceKey), ("$value", incoming.Sequence.ToString(CultureInfo.InvariantCulture)));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Source selection did not update exactly one pointer.");
        }
        transaction.Commit(); return selected ? incoming : current ?? throw new InvalidDataException("Retained source selection is missing.");
    }

    public AcceptedSourceConfiguration? CurrentSourceConfiguration()
    {
        using var transaction = _database.Connection.BeginTransaction(); var pin = ReadPin(transaction);
        var result = pin is null ? null : ReadCurrentSources(transaction, pin); transaction.Commit(); return result;
    }

    internal AcceptedSourceConfiguration? CurrentSourceConfiguration(SqliteTransaction transaction)
    {
        var pin = ReadPin(transaction); return pin is null ? null : ReadCurrentSources(transaction, pin);
    }

    private AcceptedSourceConfiguration? ReadCurrentSources(SqliteTransaction transaction, LibraryControllerPin pin)
    {
        var selected = ReadMeta(transaction, SelectedSourceKey, 19); var highest = HighestSourceSequence(transaction);
        if (selected is null)
        {
            if (highest != 0) throw new InvalidDataException("Accepted source history lost its selection; never infer a replacement.");
            return null;
        }
        var sequence = Counter(selected);
        if (sequence != highest) throw new InvalidDataException("Source selection disagrees with accepted history; never reactivate an older policy.");
        return ReadSources(transaction, pin, sequence);
    }
    private AcceptedSourceConfiguration ReadSources(SqliteTransaction transaction, LibraryControllerPin pin, long sequence)
    {
        var bytes = StoredSourceEnvelope(transaction, sequence);
        try
        {
            AssertControllerSequenceKind(transaction, sequence, source: true);
            var result = DecodeSources(pin, bytes);
            if (result.Sequence != sequence) throw new InvalidDataException("Stored source sequence differs from its document.");
            return result;
        }
        catch (Exception error) when (error is ProtocolFormatException or ProtocolCompatibilityException)
        { throw new InvalidDataException("Stored source authorization is invalid; accepted state was preserved.", error); }
    }
    private byte[] StoredSourceEnvelope(SqliteTransaction transaction, long sequence)
    {
        var key = SourceDocumentKey(sequence); var text = ReadMeta(transaction, key, ProtocolDocumentReader.MaximumDocumentBytes);
        var hash = ReadMeta(transaction, key + ":sha256", 64);
        if (text is null || hash is null || hash != Hash(Encoding.UTF8.GetBytes(text))) throw new InvalidDataException("Accepted source document is missing or corrupt.");
        return Encoding.UTF8.GetBytes(text);
    }
    private static AcceptedSourceConfiguration DecodeSources(LibraryControllerPin pin, byte[] bytes)
    {
        var verified = new SignedProtocolDocuments(SourceCapabilities).VerifyAndRead(bytes, Actor(pin));
        return new(verified.Sequence, Hash(bytes), Reader(pin).ReadSourceConfiguration(verified, pin.LibraryId));
    }
    private static string SourcePayload(LibraryControllerPin pin, byte[] bytes) =>
        new SignedProtocolDocuments(SourceCapabilities).VerifyAndRead(bytes, Actor(pin)).CanonicalPayload;
    private void AssertControllerSequenceKind(SqliteTransaction transaction, long sequence, bool source)
    {
        var other = source ? DocumentKey(sequence) : SourceDocumentKey(sequence);
        if (ReadMeta(transaction, other, ProtocolDocumentReader.MaximumDocumentBytes) is not null || ReadMeta(transaction, other + ":sha256", 64) is not null)
            throw new ControllerAuthorityConflictException();
    }
    private bool HasSourceHistory(SqliteTransaction? transaction)
    {
        using var command = Command(transaction, "SELECT 1 FROM processor_meta WHERE key=$pointer OR key GLOB 'controller_source_document_v1:*' LIMIT 1", ("$pointer", SelectedSourceKey));
        return command.ExecuteScalar() is not null;
    }
    private long HighestSourceSequence(SqliteTransaction transaction)
    {
        using var command = Command(transaction, "SELECT key FROM processor_meta WHERE key GLOB 'controller_source_document_v1:*'");
        using var reader = command.ExecuteReader(); long highest = 0;
        while (reader.Read())
        {
            var suffix = reader.GetString(0)[SourceDocumentPrefix.Length..];
            if (suffix.EndsWith(":sha256", StringComparison.Ordinal)) suffix = suffix[..^7];
            highest = Math.Max(highest, Counter(suffix));
        }
        return highest;
    }
    private static string SourceDocumentKey(long sequence) => SourceDocumentPrefix + sequence.ToString(CultureInfo.InvariantCulture);
}
