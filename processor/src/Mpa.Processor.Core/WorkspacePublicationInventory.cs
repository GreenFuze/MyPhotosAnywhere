using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record WorkspacePublicationExpectation(Guid Id, WorkspacePublicationScope Scope, string Role, string MimeType,
    long Bytes, string Sha256, string? FileId, long? VerifiedVersion);
public sealed class WorkspacePublicationInventoryChangedException() : InvalidOperationException(
    "Saved library work changed during its scan. Preserve the exact work and reconcile the current inventory before retrying.");

/// <summary>Complete bounded local outbox evidence, including unallocated, uncertain
/// and verified work. Original scopes/identities remain unchanged after reconnecting.
/// This is neither remote availability nor authority to upload or activate a generation.</summary>
public sealed record WorkspacePublicationInventory(WorkspaceObservationContext Context, int Maximum,
    ReadOnlyCollection<WorkspacePublicationExpectation> Objects, string Sha256);

public sealed partial class WorkspacePublicationRepository
{
    public WorkspacePublicationInventory Inventory(WorkspaceObservationContext context, int maximum = 4096)
    {
        using var transaction = _database.Connection.BeginTransaction();
        var result = Inventory(context, maximum, transaction); transaction.Commit(); return result;
    }

    private WorkspacePublicationInventory Inventory(WorkspaceObservationContext context, int maximum, SqliteTransaction transaction)
    {
        new WorkspaceObservationRepository(_database, _budget).AssertContext(context, transaction);
        var identities = Identities(maximum, transaction); var objects = new List<WorkspacePublicationExpectation>();
        foreach (var id in identities)
        {
            // Read/validate actual immutable payloads one at a time, then retain only
            // their bounded descriptions. Never accumulate the whole outbox's bytes.
            var record = Read(id, transaction) ?? throw new InvalidDataException("A captured publication disappeared from its complete inventory.");
            objects.Add(new(record.Id, record.Scope, record.Role, record.MimeType, record.Payload.Length, record.Sha256, record.FileId, record.VerifiedVersion));
        }
        var hash = InventoryDigest(context, maximum, objects); return new(context, maximum, objects.AsReadOnly(), hash);
    }

    public void AssertInventoryUnchanged(WorkspacePublicationInventory inventory)
    {
        using var transaction = _database.Connection.BeginTransaction(); AssertInventoryUnchanged(inventory, transaction); transaction.Commit();
    }

    internal void AssertInventoryUnchanged(WorkspacePublicationInventory inventory, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Objects is null || InventoryDigest(inventory.Context, inventory.Maximum, inventory.Objects) != inventory.Sha256) throw new WorkspacePublicationConflictException();
        if (Inventory(inventory.Context, inventory.Maximum, transaction).Sha256 != inventory.Sha256) throw new WorkspacePublicationInventoryChangedException();
    }

    // Postcondition for ONE prepared local object only. This does not refresh an
    // observation/checkpoint, and cannot authorize a physical provider request.
    internal void AssertInventoryAfterPreparedObjectIn(WorkspacePublicationInventory before,WorkspacePublicationRecord prepared,SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(before);ArgumentNullException.ThrowIfNull(prepared);
        if(before.Objects is null || InventoryDigest(before.Context,before.Maximum,before.Objects)!=before.Sha256)
            throw new WorkspacePublicationConflictException();
        var actual=Inventory(before.Context,before.Maximum,transaction);
        var expected=new WorkspacePublicationExpectation(prepared.Id,prepared.Scope,prepared.Role,prepared.MimeType,
            prepared.Payload.Length,prepared.Sha256,prepared.FileId,prepared.VerifiedVersion);
        var prior=before.Objects.SingleOrDefault(value=>value.Id==prepared.Id);
        if(prior is not null && prior!=expected) throw new WorkspacePublicationInventoryChangedException();
        var replacement=before.Objects.Where(value=>value.Id!=prepared.Id).Append(expected).OrderBy(value=>value.Id.ToString("D"),StringComparer.Ordinal);
        if(!actual.Objects.SequenceEqual(replacement)) throw new WorkspacePublicationInventoryChangedException();
    }

    private static string InventoryDigest(WorkspaceObservationContext context, int maximum, IReadOnlyCollection<WorkspacePublicationExpectation> objects)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (maximum is < 1 or > 4096 || objects.Count > maximum) throw new ArgumentException("Publication inventory exceeds its captured complete bound.", nameof(maximum));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData("mpa-workspace-publication-inventory-v1\0"u8);
        Append(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(new { context, maximum })));
        var ids = new HashSet<Guid>(); var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in objects.OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            CheckId(item.Id); ValidateScope(item.Scope); ValidateContent(item.Role, item.MimeType);
            if (!ids.Add(item.Id) || item.Scope.LibraryId != context.LibraryId || item.Scope.AccountId != context.AccountId || item.Scope.FolderId != context.FolderId ||
                item.Scope.AccountRevision > context.AccountRevision || item.Bytes < 1 || item.Bytes > (item.MimeType == "application/json" ? ProtocolDocumentReader.MaximumDocumentBytes : MaximumPayloadBytes) ||
                !Digest(item.Sha256) || item.FileId is not null && (!Identity(item.FileId) || item.FileId == "root" || item.FileId == context.FolderId || !files.Add(item.FileId)) ||
                item.VerifiedVersion is not null && (item.VerifiedVersion < 1 || item.FileId is null)) throw new WorkspacePublicationConflictException();
            Append(new CanonicalJson(4096).Encode(JsonSerializer.SerializeToUtf8Bytes(item)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
        }
    }
}
