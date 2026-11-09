using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record CatalogDependencyReference(Guid Id, WorkspacePublicationScope Scope, Guid OriginalPublicationId,
    string Role, string MimeType, long Bytes, string Sha256, string FileId, string ProvenanceSha256);

/// <summary>Immutable LOCAL reference journal, never cloud permission or duplicated
/// media/payload storage. Concrete owners must perform their own actual current read
/// and readback. A lost reply resumes the exact selected prior identity. This journal
/// does not authorize collection/deletion of referenced metadata or originals.</summary>
public sealed class CatalogDependencyReferenceRepository(ProcessorStateDatabase database, StorageBudgetManager budget)
{
    private const string Prefix = "catalog_dependency_reference_v1:";
    private readonly ProcessorStateDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly StorageBudgetManager _budget = budget ?? throw new ArgumentNullException(nameof(budget));

    public CatalogDependencyReference? Read(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A captured dependency identity is required.", nameof(id));
        using var transaction = _database.Connection.BeginTransaction(); var result = Read(id, transaction); transaction.Commit(); return result;
    }

    public CatalogDependencyReference? ForFile(WorkspacePublicationScope scope, string file) => ForFile(scope, file, null);

    internal CatalogDependencyReference? ForFile(WorkspacePublicationScope scope, string file, SqliteTransaction? transaction) =>
        ForScope(scope, transaction).SingleOrDefault(item => item.FileId == file);

    internal IReadOnlyList<CatalogDependencyReference> ForScope(WorkspacePublicationScope scope, SqliteTransaction? transaction)
    {
        using var command = Command(transaction, "SELECT key,length(value),value FROM processor_meta WHERE key GLOB $prefix ORDER BY key LIMIT 4097", ("$prefix", Prefix + "*"));
        using var reader = command.ExecuteReader(); var rows = new List<CatalogDependencyReference>();
        while (reader.Read())
        {
            if (rows.Count == 4096 || reader.GetInt64(1) is < 1 or > 16384 || !Guid.TryParseExact(reader.GetString(0)[Prefix.Length..], "D", out var id))
                throw new InvalidDataException("Dependency reference inventory exceeds its complete bound or lost its identity.");
            rows.Add(Decode(id, reader.GetString(2)));
        }
        reader.Close();
        foreach (var row in rows) Validate(row, transaction);
        return rows.Where(item => item.Scope == scope).ToArray();
    }

    public CatalogDependencyReference Record(Guid id, WorkspacePublicationScope scope, Guid originalPublicationId,
        WorkspaceInventoryCheckpoint checkpoint, Guid attemptId, CatalogGenerationLocalAccess? localAccess = null)
    {
        // Full existing historical validators run outside the transaction. The exact
        // immutable provenance is compared again inside the atomic write; no recursive
        // validation of the current generation is performed by a reference read.
        var captures = new CatalogGenerationCaptureRepository(_database, _budget);
        var current = captures.Publication(scope.GenerationId) ?? throw new CatalogGenerationCaptureConflictException();
        var original = new WorkspacePublicationRepository(_database, _budget).Read(originalPublicationId) ?? throw new WorkspacePublicationConflictException();
        var prior = captures.Publication(original.Scope.GenerationId) ?? throw new CatalogGenerationCaptureConflictException();
        var verification = captures.Verification(original.Scope.GenerationId) ?? throw new CatalogGenerationCaptureConflictException();
        if (current.Capture.Scope != scope || current.Capture.Phase != "publication-planned" || current.Plan is not null ||
            prior.Capture.Scope != original.Scope || prior.Capture.Phase is not ("complete" or "snapshot-retiring" or "retired") ||
            verification.Sequence > current.Basis.ParentSequence) throw new CatalogGenerationCaptureConflictException();
        var provenance = Provenance(original, null);
        using var transaction = _database.Connection.BeginTransaction();
        new LibraryAuthorityRepository(_database, _budget).AssertLocalPublicationScope(scope, transaction, localAccess);
        var observations = new WorkspaceObservationRepository(_database, _budget);
        observations.AssertCompletedScan(checkpoint, attemptId, transaction);
        if (checkpoint.Context.LibraryId != scope.LibraryId || checkpoint.Context.AccountId != scope.AccountId || checkpoint.Context.FolderId != scope.FolderId)
            throw new WorkspaceObservationConflictException();
        using (var query = Command(transaction, "SELECT scope,phase FROM catalog_generation_capture WHERE id=$id", ("$id", scope.GenerationId.ToString("D"))))
        using (var reader = query.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != WorkspacePublicationRepository.EncodeScope(scope) || reader.GetString(1) != "publication-planned")
                throw new CatalogGenerationCaptureConflictException();
        var record = new CatalogDependencyReference(id, scope, original.Id, original.Role, original.MimeType,
            original.Payload.Length, original.Sha256, original.FileId ?? throw new WorkspacePublicationConflictException(), provenance);
        Validate(record, transaction);
        var observed = observations.Known(checkpoint.Context, transaction).SingleOrDefault(item => item.FileId == record.FileId);
        if (observed is null || observed.Bytes != record.Bytes || observed.Sha256 != record.Sha256 ||
            (observed.MimeType ?? "application/json") != record.MimeType || observed.Version < original.VerifiedVersion)
            throw new WorkspaceObservationConflictException();
        var old = Read(id, transaction);
        if (old is not null)
        {
            if (old != record) throw new WorkspacePublicationConflictException();
            transaction.Commit(); return old;
        }
        using (var count = Command(transaction, "SELECT count(*) FROM processor_meta WHERE key GLOB $prefix", ("$prefix", Prefix + "*")))
            if ((long)count.ExecuteScalar()! >= 4096) throw new InvalidDataException("Dependency reference inventory exceeds its retained bound.");
        var body = Encode(record); var value = Encode(new Stored(body, Hash(body)));
        using var reservation = _budget.Reserve(checked(Encoding.UTF8.GetByteCount(value) * 3L + 65536)); reservation.RecheckBeforeWrite(reservation.Bytes);
        using var insert = Command(transaction, "INSERT INTO processor_meta(key,value) VALUES($key,$value)", ("$key", Prefix + id.ToString("D")), ("$value", value));
        if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Dependency reference was not checkpointed exactly once.");
        transaction.Commit(); return record;
    }

    private CatalogDependencyReference? Read(Guid id, SqliteTransaction transaction)
    {
        using var command = Command(transaction, "SELECT length(value),value FROM processor_meta WHERE key=$key", ("$key", Prefix + id.ToString("D")));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(0) is < 1 or > 16384) throw new InvalidDataException("Retained dependency reference exceeds its bound.");
        var record = Decode(id, reader.GetString(1)); reader.Close(); Validate(record, transaction); return record;
    }

    private void Validate(CatalogDependencyReference record, SqliteTransaction? transaction)
    {
        if (record.Id == Guid.Empty || record.OriginalPublicationId == Guid.Empty) throw new WorkspacePublicationConflictException();
        var outbox = new WorkspacePublicationRepository(_database, _budget);
        if (outbox.Read(record.Id, transaction) is not null) throw new WorkspacePublicationConflictException();
        using (var current = Command(transaction, "SELECT scope FROM catalog_generation_capture WHERE id=$id", ("$id", record.Scope.GenerationId.ToString("D"))))
            if (current.ExecuteScalar() is not string encoded || encoded != WorkspacePublicationRepository.EncodeScope(record.Scope))
                throw new CatalogGenerationCaptureConflictException();
        var original = outbox.Read(record.OriginalPublicationId, transaction) ?? throw new WorkspacePublicationConflictException();
        WorkspacePublicationRepository.ValidateScope(record.Scope);
        if (original.Scope.GenerationId == record.Scope.GenerationId ||
            original.Scope.LibraryId != record.Scope.LibraryId || original.Scope.ActorId != record.Scope.ActorId || original.Scope.ActorEpoch != record.Scope.ActorEpoch ||
            original.Scope.AccountId != record.Scope.AccountId || original.Scope.FolderId != record.Scope.FolderId || original.FileId != record.FileId ||
            original.VerifiedVersion is null || original.Role != record.Role || original.MimeType != record.MimeType || original.Payload.Length != record.Bytes || original.Sha256 != record.Sha256 ||
            record.Role is not ("facts-index" or "facts-chunk" or "coverage-root" or "coverage-page") || record.Id != Identity(record) ||
            Provenance(original, transaction) != record.ProvenanceSha256)
            throw new WorkspacePublicationConflictException();
    }

    // A selection pins the already fully validated immutable native provenance. Later
    // reads compare exact records without recursively walking all earlier generations.
    // This is historical evidence only; live bytes/access/graph checks remain mandatory.
    private string Provenance(WorkspacePublicationRecord original, SqliteTransaction? transaction)
    {
        using var query = Command(transaction, """
            SELECT c.scope,c.snapshot_id,c.commit_intent_id,c.context_hash,c.snapshot,c.snapshot_hash,c.phase,
              p.basis,p.basis_hash,p.plan,p.plan_hash,v.document,v.document_hash
            FROM catalog_generation_capture c JOIN catalog_generation_publication p ON p.id=c.id
              JOIN catalog_generation_verification v ON v.id=c.id WHERE c.id=$id
            """, ("$id", original.Scope.GenerationId.ToString("D")));
        using var reader = query.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != WorkspacePublicationRepository.EncodeScope(original.Scope) ||
            reader.GetString(6) is not ("complete" or "snapshot-retiring" or "retired")) throw new CatalogGenerationCaptureConflictException();
        var values = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i == 6) continue; // Completed metadata-copy retirement legitimately advances.
            if (reader.IsDBNull(i) || reader.GetString(i).Length is < 1 or > 49152)
                throw new InvalidDataException("Completed dependency provenance is incomplete or exceeds its bound.");
            values.Add(reader.GetString(i));
        }
        var intentId = Guid.ParseExact(reader.GetString(2), "D"); reader.Close();
        var intent = new ProtocolSigningJournal(_database, _budget).Read(intentId, transaction) ?? throw new ProtocolSigningConflictException();
        if (intent.Envelope is null || intent.ActorId != original.Scope.ActorId || intent.Epoch != original.Scope.ActorEpoch || intent.Kind != "catalog-commit")
            throw new ProtocolSigningConflictException();
        values.Add(intent.EnvelopeSha256!); values.Add(intent.BodySha256);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("mpa-catalog-dependency-provenance-v1"u8);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value); BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Guid Identity(CatalogDependencyReference record)
    {
        var body = new CanonicalJson(8192).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        { format = "mpa-catalog-dependency-outbox", major = 1, scope = record.Scope, role = record.Role, mimeType = record.MimeType, payloadSha256 = record.Sha256 }));
        var digest = SHA256.HashData(body); digest[6] = (byte)((digest[6] & 15) | 128); digest[8] = (byte)((digest[8] & 63) | 128);
        return new(digest.AsSpan(0, 16), bigEndian: true);
    }
    private static CatalogDependencyReference Decode(Guid id, string value)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(value) ?? throw new InvalidDataException("Dependency reference lost its wrapper.");
            if (stored.Document is null || Encode(stored) != value || Hash(stored.Document) != stored.Sha256) throw new InvalidDataException("Dependency reference integrity failed.");
            var record = JsonSerializer.Deserialize<CatalogDependencyReference>(stored.Document) ?? throw new InvalidDataException("Dependency reference is missing.");
            if (record.Id != id || Encode(record) != stored.Document) throw new InvalidDataException("Dependency reference identity or encoding changed."); return record;
        }
        catch (JsonException error) { throw new InvalidDataException("Dependency reference cannot be decoded safely.", error); }
    }
    private static string Encode<T>(T value) => Encoding.UTF8.GetString(new CanonicalJson(16384).Encode(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = _database.Connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value); return command;
    }
    private sealed record Stored(string Document, string Sha256);
}
