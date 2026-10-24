using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record PublisherSigningRegistration(Guid ActorId, long Epoch, string PublicKeySpki, string KeyId);
public sealed record ProtocolSigningIntent(Guid Id, Guid LibraryId, Guid ActorId, long Epoch, long Sequence, string Kind,
    string BodySha256, ReadOnlyMemory<byte> Body, ReadOnlyMemory<byte>? Envelope, string? EnvelopeSha256);
public sealed class ProtocolSigningConflictException() : InvalidOperationException("This signing identity, sequence or captured document conflicts with durable work.");

/// <summary>Serial durable outgoing intent owner. Register both actors atomically with
/// fresh controller bootstrap, reserve counter/body before native signing, and seal exact
/// bytes before Drive allocation. Never re-register a missing actor or reset its counter.
/// Restore/cloud high-water reconciliation and catalog predecessor admission remain the
/// generation owner's mandatory responsibilities before any live publication.</summary>
public sealed partial class ProtocolSigningJournal(ProcessorStateDatabase database, StorageBudgetManager budget)
{
    public const int MaximumBodyBytes = ProtocolDocumentReader.MaximumDocumentBytes - 1024;
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    private static readonly string[] Capabilities = [CatalogPublisherAuthorization.Capability, CatalogSourceConfiguration.Capability,
        CatalogPublicationReader.Capability, DriveCatalogFacts.Capability];

    internal void InitializeBootstrapActors(SqliteTransaction transaction, LibraryControllerPin controller, PublisherSigningRegistration publisher)
    {
        using (var prior = Command(transaction, "SELECT (SELECT count(*) FROM signing_actor)+(SELECT count(*) FROM signing_intent)"))
            if (Convert.ToInt64(prior.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new InvalidDataException("Fresh controller bootstrap cannot replace existing signing state.");
        var actors = BootstrapActors(controller, publisher);
        foreach (var actor in actors)
        {
            using var insert = Command(transaction, "INSERT INTO signing_actor(actor_id,epoch,library_id,role,key_id,public_key) VALUES($id,$epoch,$library,$role,$key,$public)",
                ("$id", actor.Id.ToString("D")), ("$epoch", actor.Epoch), ("$library", actor.Library.ToString("D")), ("$role", actor.Role),
                ("$key", actor.KeyId), ("$public", actor.PublicKey));
            if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Signing bootstrap did not create exactly one actor record.");
        }
    }
    internal void AssertBootstrapActors(SqliteTransaction transaction, LibraryControllerPin controller, PublisherSigningRegistration publisher)
    {
        foreach (var expected in BootstrapActors(controller, publisher))
        {
            var actor = Actor(expected.Id, expected.Epoch, transaction);
            if (actor.Library != expected.Library || actor.Role != expected.Role || actor.KeyId != expected.KeyId || !actor.PublicKey.AsSpan().SequenceEqual(expected.PublicKey))
                throw new ProtocolSigningConflictException();
        }
    }

    /// <summary>The body factory is bounded synchronous state/serialization work only,
    /// never network, native processing or signing. Replayed IDs rebuild against the
    /// captured sequence and must produce the same canonical bytes.</summary>
    public ProtocolSigningIntent Reserve(Guid id, Guid actorId, long epoch, string kind, Func<long, byte[]> bodyFactory)
    {
        CheckId(id); CheckId(actorId); ArgumentOutOfRangeException.ThrowIfLessThan(epoch, 1L); ArgumentNullException.ThrowIfNull(bodyFactory);
        using var transaction = _database.Connection.BeginTransaction(); var actor = Actor(actorId, epoch, transaction);
        var existing = Read(id, transaction);
        if (existing is not null)
        {
            if (existing.ActorId != actorId || existing.Epoch != epoch || existing.Kind != kind) throw new ProtocolSigningConflictException();
            var replay = bodyFactory(existing.Sequence); ValidateBody(actor, existing.Sequence, kind, replay);
            if (!existing.Body.Span.SequenceEqual(replay)) throw new ProtocolSigningConflictException();
            transaction.Commit(); return existing;
        }
        if (actor.Sequence == long.MaxValue) throw new InvalidOperationException("The signing sequence is exhausted; explicitly enroll a new authorized epoch.");
        var sequence = actor.Sequence + 1; var owned = bodyFactory(sequence).ToArray(); ValidateBody(actor, sequence, kind, owned); var hash = Hash(owned);
        using var reservation = _budget.Reserve(checked(owned.LongLength * 3 + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        using (var insert = Command(transaction, "INSERT INTO signing_intent(id,actor_id,epoch,sequence,kind,body,body_hash) VALUES($id,$actor,$epoch,$sequence,$kind,$body,$hash)",
            ("$id", id.ToString("D")), ("$actor", actorId.ToString("D")), ("$epoch", epoch), ("$sequence", sequence), ("$kind", kind), ("$body", owned), ("$hash", hash)))
            if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Signing intent did not insert exactly one captured record.");
        using (var advance = Command(transaction, "UPDATE signing_actor SET sequence=$next WHERE actor_id=$id AND epoch=$epoch AND sequence=$previous",
            ("$next", sequence), ("$id", actorId.ToString("D")), ("$epoch", epoch), ("$previous", actor.Sequence)))
            if (advance.ExecuteNonQuery() != 1) throw new ProtocolSigningConflictException();
        transaction.Commit(); return new(id, actor.Library, actorId, epoch, sequence, kind, hash, owned, null, null);
    }

    public ProtocolSigningIntent? Read(Guid id) { CheckId(id); return Read(id, null); }

    public ProtocolSigningIntent Seal(Guid id, string expectedBodySha256, DocumentSignature signature)
    {
        CheckId(id); ArgumentNullException.ThrowIfNull(signature);
        using var transaction = _database.Connection.BeginTransaction(); var intent = Read(id, transaction) ?? throw new InvalidOperationException("The reserved signing intent is missing.");
        if (intent.BodySha256 != expectedBodySha256) throw new ProtocolSigningConflictException();
        var actor = Actor(intent.ActorId, intent.Epoch, transaction);
        _ = new ProtocolDocumentReader(Capabilities).VerifyAndRead(intent.Body.Span, signature, Authority(actor));
        var envelope = new SignedProtocolDocuments(Capabilities).Encode(intent.Body.Span, signature); var hash = Hash(envelope);
        if (intent.Envelope is not null)
        {
            if (!intent.Envelope.Value.Span.SequenceEqual(envelope)) throw new ProtocolSigningConflictException();
            transaction.Commit(); return intent;
        }
        using var reservation = _budget.Reserve(checked(envelope.LongLength * 3 + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        using (var update = Command(transaction, "UPDATE signing_intent SET envelope=$bytes,envelope_hash=$hash WHERE id=$id AND body_hash=$bodyHash AND envelope IS NULL",
            ("$bytes", envelope), ("$hash", hash), ("$id", id.ToString("D")), ("$bodyHash", expectedBodySha256)))
            if (update.ExecuteNonQuery() != 1) throw new ProtocolSigningConflictException();
        transaction.Commit(); return intent with { Envelope = envelope, EnvelopeSha256 = hash };
    }

    internal ProtocolSigningIntent? Read(Guid id, SqliteTransaction? transaction)
    {
        Stored row;
        using (var command = Command(transaction, "SELECT actor_id,epoch,sequence,kind,length(body),body,body_hash,length(envelope),envelope,envelope_hash FROM signing_intent WHERE id=$id", ("$id", id.ToString("D"))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetInt64(4) is < 1 or > MaximumBodyBytes || !reader.IsDBNull(7) && reader.GetInt64(7) is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes)
                throw new InvalidDataException("Stored signing document exceeds its byte bound.");
            row = new(ParseId(reader.GetString(0)), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetFieldValue<byte[]>(5), reader.GetString(6),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8), reader.IsDBNull(9) ? null : reader.GetString(9));
        }
        var actor = Actor(row.Actor, row.Epoch, transaction);
        if (row.Sequence < 1 || row.Sequence > actor.Sequence || Hash(row.Body) != row.BodyHash || (row.Envelope is null) != (row.EnvelopeHash is null))
            throw new InvalidDataException("Stored signing intent integrity or sequence is invalid.");
        try
        {
            ValidateBody(actor, row.Sequence, row.Kind, row.Body);
            if (row.Envelope is not null)
            {
                if (Hash(row.Envelope) != row.EnvelopeHash) throw new InvalidDataException("Stored signed envelope integrity failed.");
                _ = new SignedProtocolDocuments(Capabilities).VerifyAndRead(row.Envelope, Authority(actor));
                using var document = JsonDocument.Parse(row.Envelope);
                if (Hash(Encoding.UTF8.GetBytes(document.RootElement.GetProperty("document").GetRawText())) != row.BodyHash)
                    throw new InvalidDataException("Sealed envelope changed its reserved document body.");
            }
        }
        catch (ProtocolFormatException error) { throw new InvalidDataException("Stored signing intent is invalid; preserve its identity for recovery.", error); }
        return new(id, actor.Library, row.Actor, row.Epoch, row.Sequence, row.Kind, row.BodyHash, row.Body,
            row.Envelope is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(row.Envelope), row.EnvelopeHash);
    }

    private ActorRecord Actor(Guid id, long epoch, SqliteTransaction? transaction)
    {
        using var command = Command(transaction, """
            SELECT a.library_id,a.role,a.key_id,length(a.public_key),a.public_key,a.sequence,
              (SELECT count(*) FROM signing_intent i WHERE i.actor_id=a.actor_id AND i.epoch=a.epoch),
              (SELECT coalesce(max(sequence),0) FROM signing_intent i WHERE i.actor_id=a.actor_id AND i.epoch=a.epoch),
              (SELECT value FROM processor_meta WHERE key='library_id')
            FROM signing_actor a WHERE a.actor_id=$actor AND a.epoch=$epoch
            """, ("$actor", id.ToString("D")), ("$epoch", epoch));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("The registered signing actor is missing. Reconcile authority/history; never recreate its counter.");
        var library = ParseId(reader.GetString(0)); var role = reader.GetString(1); var key = reader.GetString(2);
        if (reader.GetInt64(3) is < 1 or > 512 || reader.GetInt64(5) < 0 || reader.GetInt64(5) != reader.GetInt64(6) || reader.GetInt64(5) != reader.GetInt64(7) ||
            reader.IsDBNull(8) || reader.GetString(8) != library.ToString("D") || role is not ("controller" or "publisher"))
            throw new InvalidDataException("Signing actor/counter does not match its complete captured intent history.");
        var publicKey = reader.GetFieldValue<byte[]>(4);
        try { using var verifier = new ProtocolDocumentVerifier(publicKey); if (verifier.KeyId != key) throw new InvalidDataException("Registered signing key identity changed."); }
        catch (ProtocolFormatException error) { throw new InvalidDataException("Registered signing public key is invalid.", error); }
        return new(library, id, epoch, role, key, publicKey, reader.GetInt64(5));
    }

    private static ActorRecord[] BootstrapActors(LibraryControllerPin controller, PublisherSigningRegistration publisher)
    {
        if (publisher.ActorId == Guid.Empty || publisher.ActorId == controller.ActorId || publisher.Epoch < 1 || publisher.PublicKeySpki is null || publisher.PublicKeySpki.Length is < 1 or > 684)
            throw new ArgumentException("A separate persisted native publisher identity is required.", nameof(publisher));
        var controllerKey = Convert.FromBase64String(controller.PublicKeySpki); byte[] publisherKey;
        try { publisherKey = Convert.FromBase64String(publisher.PublicKeySpki); }
        catch (FormatException error) { throw new ArgumentException("Publisher public key is not base64.", nameof(publisher), error); }
        using var verifier = new ProtocolDocumentVerifier(publisherKey);
        if (Convert.ToBase64String(publisherKey) != publisher.PublicKeySpki || verifier.KeyId != publisher.KeyId || publisher.KeyId == controller.KeyId)
            throw new ArgumentException("Native publisher key identity does not match its separate public key.", nameof(publisher));
        return [new(controller.LibraryId, controller.ActorId, controller.Epoch, "controller", controller.KeyId, controllerKey, 0),
            new(controller.LibraryId, publisher.ActorId, publisher.Epoch, "publisher", publisher.KeyId, publisherKey, 0)];
    }
    private static void ValidateBody(ActorRecord actor, long sequence, string kind, ReadOnlySpan<byte> body)
    {
        if (body.Length is < 1 or > MaximumBodyBytes) throw new ProtocolFormatException("Outgoing signing body exceeds its bounded envelope headroom.");
        var canonical = new CanonicalJson().Encode(body);
        if (!body.SequenceEqual(canonical)) throw new ProtocolFormatException("Capture canonical document bytes before reserving a signing sequence.");
        new ProtocolDocumentReader(Capabilities).ValidateForSigning(body, Authority(actor));
        using var document = JsonDocument.Parse(canonical); var root = document.RootElement;
        if (root.GetProperty("sequence").GetString() != sequence.ToString(CultureInfo.InvariantCulture) || root.GetProperty("kind").GetString() != kind)
            throw new ProtocolFormatException("Outgoing document does not match its reserved kind/sequence.");
    }
    private static TrustedProtocolActor Authority(ActorRecord actor) => new(actor.Library, actor.Id, actor.Epoch, actor.PublicKey,
        actor.Role == "controller" ? ["library-publisher-authorization", "library-source-configuration"] : ["catalog-commit"]);
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Key, object Value)[] values)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var pair in values) command.Parameters.AddWithValue(pair.Key, pair.Value);
        return command;
    }
    private static void CheckId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A stable signing identity is required.", nameof(id)); }
    private static Guid ParseId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw new InvalidDataException("Stored signing identity is not canonical.");
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record ActorRecord(Guid Library, Guid Id, long Epoch, string Role, string KeyId, byte[] PublicKey, long Sequence);
    private sealed record Stored(Guid Actor, long Epoch, long Sequence, string Kind, byte[] Body, string BodyHash, byte[]? Envelope, string? EnvelopeHash);
}
