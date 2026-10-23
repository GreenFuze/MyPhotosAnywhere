using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record ProtocolSigningActorExpectation(Guid ActorId, long Epoch, string Role, string KeyId,
    string PublicKeySpki, long Sequence);
public sealed record ProtocolSigningIntentExpectation(Guid Id, Guid ActorId, long Epoch, long Sequence,
    string Kind, int BodyBytes, string BodySha256, int? EnvelopeBytes, string? EnvelopeSha256);

/// <summary>Complete bounded local signing evidence, including reserved but unsealed
/// work and actors with no outgoing documents. Payloads are verified one at a time.
/// An empty inventory proves neither native ownership nor a safe cloud high-water mark.
/// Current native keys, enrolled authority and complete remote history remain separate.</summary>
public sealed record ProtocolSigningInventory(Guid LibraryId,
    ReadOnlyCollection<ProtocolSigningActorExpectation> Actors,
    ReadOnlyCollection<ProtocolSigningIntentExpectation> Intents, string Sha256);

public sealed class ProtocolSigningInventoryChangedException() : InvalidOperationException(
    "Captured signing work changed during reconciliation. Preserve its identities and check the complete current history again.");

public sealed partial class ProtocolSigningJournal
{
    public const int MaximumInventoryActors = 64;
    public const int MaximumInventoryIntents = 4096;

    public ProtocolSigningInventory Inventory(Guid libraryId)
    {
        using var transaction = _database.Connection.BeginTransaction();
        var result = Inventory(libraryId, transaction); transaction.Commit(); return result;
    }

    internal ProtocolSigningInventory Inventory(Guid libraryId, SqliteTransaction transaction)
    {
        CheckId(libraryId);
        using (var library = Command(transaction, "SELECT value FROM processor_meta WHERE key='library_id'"))
            if (library.ExecuteScalar() as string != libraryId.ToString("D")) throw new ProtocolSigningConflictException();
        var identities = new List<(Guid Id, long Epoch)>();
        using (var command = Command(transaction, "SELECT actor_id,epoch FROM signing_actor ORDER BY actor_id,epoch LIMIT $limit", ("$limit", MaximumInventoryActors + 1)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (identities.Count == MaximumInventoryActors) throw new InvalidDataException("Signing actor history exceeds its complete reconciliation bound.");
                identities.Add((ParseId(reader.GetString(0)), reader.GetInt64(1)));
            }
        var actors = new List<ProtocolSigningActorExpectation>();
        foreach (var (id, epoch) in identities)
        {
            var actor = Actor(id, epoch, transaction);
            actors.Add(new(id, epoch, actor.Role, actor.KeyId, Convert.ToBase64String(actor.PublicKey), actor.Sequence));
        }
        if (actors.Count != 0)
        {
            var pin = new LibraryAuthorityRepository(_database).Controller(transaction) ?? throw new InvalidDataException("Local signing history lost its pinned controller.");
            var controller = actors.SingleOrDefault(actor => actor.ActorId == pin.ActorId && actor.Epoch == pin.Epoch);
            if (pin.LibraryId != libraryId || controller is null || controller.Role != "controller" || controller.KeyId != pin.KeyId ||
                controller.PublicKeySpki != pin.PublicKeySpki || !actors.Any(actor => actor.Role == "publisher"))
                throw new InvalidDataException("Local signing history lost its registered controller or publisher identity.");
        }
        var ids = new List<Guid>();
        using (var command = Command(transaction, "SELECT id FROM signing_intent ORDER BY actor_id,epoch,sequence LIMIT $limit", ("$limit", MaximumInventoryIntents + 1)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (ids.Count == MaximumInventoryIntents) throw new InvalidDataException("Signing intent history exceeds its complete reconciliation bound.");
                ids.Add(ParseId(reader.GetString(0)));
            }
        var intents = new List<ProtocolSigningIntentExpectation>();
        foreach (var id in ids)
        {
            // Read validates the actual body, exact signature and reserved sequence;
            // only the small immutable description survives this iteration.
            var intent = Read(id, transaction) ?? throw new InvalidDataException("A signing intent disappeared from its complete inventory.");
            intents.Add(new(intent.Id, intent.ActorId, intent.Epoch, intent.Sequence, intent.Kind, intent.Body.Length,
                intent.BodySha256, intent.Envelope?.Length, intent.EnvelopeSha256));
        }
        var digest = InventoryDigest(libraryId, actors, intents);
        return new(libraryId, actors.AsReadOnly(), intents.AsReadOnly(), digest);
    }

    public void AssertInventoryUnchanged(ProtocolSigningInventory inventory)
    {
        using var transaction = _database.Connection.BeginTransaction();
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Actors is null || inventory.Intents is null || InventoryDigest(inventory.LibraryId, inventory.Actors, inventory.Intents) != inventory.Sha256)
            throw new ProtocolSigningConflictException();
        AssertInventoryUnchanged(inventory.LibraryId, inventory.Sha256, transaction); transaction.Commit();
    }

    internal void AssertInventoryUnchanged(Guid libraryId, string expectedSha256, SqliteTransaction transaction)
    {
        if (!InventoryHash(expectedSha256)) throw new ProtocolSigningConflictException();
        if (Inventory(libraryId, transaction).Sha256 != expectedSha256) throw new ProtocolSigningInventoryChangedException();
    }

    private static string InventoryDigest(Guid libraryId, IReadOnlyCollection<ProtocolSigningActorExpectation> actors,
        IReadOnlyCollection<ProtocolSigningIntentExpectation> intents)
    {
        CheckId(libraryId);
        if (actors.Count > MaximumInventoryActors || intents.Count > MaximumInventoryIntents) throw new ProtocolSigningConflictException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("mpa-protocol-signing-inventory-v1\0"u8);
        Append(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(new { libraryId })));
        var byActor = new Dictionary<(Guid, long), ProtocolSigningActorExpectation>();
        foreach (var actor in actors.OrderBy(actor => actor.ActorId).ThenBy(actor => actor.Epoch))
        {
            CheckId(actor.ActorId);
            if (actor.Epoch < 1 || actor.Sequence < 0 || actor.Sequence > MaximumInventoryIntents || actor.Role is not ("controller" or "publisher") ||
                actor.PublicKeySpki is not { Length: > 0 and <= 684 } || !InventoryHash(actor.KeyId) || !byActor.TryAdd((actor.ActorId, actor.Epoch), actor))
                throw new ProtocolSigningConflictException();
            byte[] key;
            try { key = Convert.FromBase64String(actor.PublicKeySpki); }
            catch (FormatException error) { throw new InvalidDataException("Captured signing inventory has an invalid public key.", error); }
            using var verifier = new ProtocolDocumentVerifier(key);
            if (Convert.ToBase64String(key) != actor.PublicKeySpki || verifier.KeyId != actor.KeyId) throw new ProtocolSigningConflictException();
            Append(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(actor)));
        }
        var ids = new HashSet<Guid>(); var counts = new Dictionary<(Guid, long), long>();
        foreach (var intent in intents.OrderBy(intent => intent.ActorId).ThenBy(intent => intent.Epoch).ThenBy(intent => intent.Sequence))
        {
            CheckId(intent.Id);
            if (!ids.Add(intent.Id) || !byActor.TryGetValue((intent.ActorId, intent.Epoch), out var actor) ||
                intent.Sequence != counts.GetValueOrDefault((intent.ActorId, intent.Epoch)) + 1 || intent.Sequence > actor.Sequence ||
                intent.Kind is not ("library-publisher-authorization" or "library-source-configuration" or "catalog-commit") ||
                (actor.Role == "publisher") != (intent.Kind == "catalog-commit") || intent.BodyBytes is < 1 or > MaximumBodyBytes || !InventoryHash(intent.BodySha256) ||
                (intent.EnvelopeBytes is null) != (intent.EnvelopeSha256 is null) || intent.EnvelopeBytes is < 1 or > ProtocolDocumentReader.MaximumDocumentBytes ||
                intent.EnvelopeSha256 is not null && !InventoryHash(intent.EnvelopeSha256)) throw new ProtocolSigningConflictException();
            counts[(intent.ActorId, intent.Epoch)] = intent.Sequence;
            Append(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(intent)));
        }
        if (byActor.Any(pair => counts.GetValueOrDefault(pair.Key) != pair.Value.Sequence)) throw new ProtocolSigningConflictException();
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
        }
    }
    private static bool InventoryHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
