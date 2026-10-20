using System.Globalization;
using System.Text.Json;

namespace Mpa.Processor.Protocol;

public enum OriginalActionKind { Backup, Import, SaveEditedCopy, Trash, Restore }
public abstract record ApprovedInput;
public sealed record ApprovedDriveInput(Guid AssetId, string AccountPermissionId, string FileId,
    long Bytes, string Sha256, string Revision, string Etag, bool Trashed) : ApprovedInput;
public sealed record ApprovedDeviceInput(Guid AssetId, Guid ActorId, string MediaKey, long Bytes, string Sha256) : ApprovedInput;
public sealed record ApprovedImportInput(string ManifestSha256, string SelectionSha256) : ApprovedInput;
public sealed record CapturedDriveDestination(string AccountPermissionId, string FolderId, string WorkspaceFolderId);
public sealed record ApprovedOriginalAction(Guid ApprovalId, Guid LibraryId, Guid ActorId, long Epoch,
    long Sequence, OriginalActionKind Action, ApprovedInput Source, CapturedDriveDestination? Destination,
    string? RecipeSha256, Guid? TombstoneId);

/// <summary>Closed v1 mutation scope. Parsing does not execute or grant a mutation: the durable
/// action owner must still check revocation, replay journal, current source revision/conditional
/// write support and destination membership immediately before the exact approved action.</summary>
public sealed class ApprovedOriginalActionReader(Guid controllingActorId)
{
    private readonly Guid _controllingActor = controllingActorId != Guid.Empty ? controllingActorId :
        throw new ArgumentException("A controlling phone actor is required.", nameof(controllingActorId));

    public ApprovedOriginalAction Read(VerifiedProtocolDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Kind != "original-action" || document.ActorId != _controllingActor)
            throw new ProtocolFormatException("Original actions require the approved controlling phone.");
        using var payload = JsonDocument.Parse(document.CanonicalPayload);
        var root = new ClosedObject(payload.RootElement, "approvalId", "action", "source", "destination", "recipeSha256", "tombstoneId");
        var action = root.String("action") switch
        {
            "backup" => OriginalActionKind.Backup, "import" => OriginalActionKind.Import,
            "save-edited-copy" => OriginalActionKind.SaveEditedCopy, "trash" => OriginalActionKind.Trash,
            "restore" => OriginalActionKind.Restore, _ => throw new ProtocolFormatException("Unsupported original action.")
        };
        var sourceElement = root.Value("source");
        if (sourceElement.ValueKind != JsonValueKind.Object || !sourceElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new ProtocolFormatException("Original action requires a typed captured source.");
        ApprovedInput source = type.GetString() switch
        {
            "drive-original" => DriveInput(sourceElement), "device-original" => DeviceInput(sourceElement),
            "import-manifest" => ImportInput(sourceElement), _ => throw new ProtocolFormatException("Unsupported captured source type.")
        };
        CapturedDriveDestination? destination = null;
        if (root.Value("destination").ValueKind != JsonValueKind.Null)
        {
            var target = new ClosedObject(root.Value("destination"), "accountPermissionId", "folderId", "workspaceFolderId");
            destination = new(target.OpaqueId("accountPermissionId"), target.OpaqueId("folderId"), target.OpaqueId("workspaceFolderId"));
        }
        var recipe = root.Value("recipeSha256").ValueKind == JsonValueKind.Null ? null : root.Hash("recipeSha256");
        Guid? tombstone = root.Value("tombstoneId").ValueKind == JsonValueKind.Null ? null : root.Uuid("tombstoneId");
        var valid = action switch
        {
            OriginalActionKind.Backup => source is ApprovedDeviceInput device && device.ActorId == document.ActorId && destination is not null && recipe is null && tombstone is null,
            OriginalActionKind.Import => source is ApprovedImportInput && destination is not null && recipe is null && tombstone is null,
            OriginalActionKind.SaveEditedCopy => (source is ApprovedDeviceInput local && local.ActorId == document.ActorId || source is ApprovedDriveInput { Trashed: false }) &&
                destination is not null && recipe is not null && tombstone is null,
            OriginalActionKind.Trash => source is ApprovedDriveInput { Trashed: false } && destination is null && recipe is null && tombstone is null,
            OriginalActionKind.Restore => source is ApprovedDriveInput { Trashed: true } && destination is null && recipe is null && tombstone is not null,
            _ => false
        };
        if (!valid) throw new ProtocolFormatException("Action/source/destination/recipe/tombstone scope does not match the approved action.");
        return new(root.Uuid("approvalId"), document.LibraryId, document.ActorId, document.Epoch, document.Sequence,
            action, source, destination, recipe, tombstone);
    }

    private static ApprovedDriveInput DriveInput(JsonElement element)
    {
        var source = new ClosedObject(element, "type", "assetId", "accountPermissionId", "fileId", "bytes", "sha256", "revision", "etag", "trashed");
        var etag = source.String("etag");
        if (etag.Length is < 3 or > 256 || etag[0] != '"' || etag[^1] != '"' || etag[1..^1].Any(character => character is < '!' or > '~' or '"'))
            throw new ProtocolFormatException("A captured strong source ETag is required.");
        _ = source.PositiveDecimal("revision");
        return new(source.Uuid("assetId"), source.OpaqueId("accountPermissionId"), source.OpaqueId("fileId"),
            source.PositiveDecimal("bytes"), source.Hash("sha256"), source.String("revision"), etag, source.Boolean("trashed"));
    }

    private static ApprovedDeviceInput DeviceInput(JsonElement element)
    {
        var source = new ClosedObject(element, "type", "assetId", "actorId", "mediaKey", "bytes", "sha256");
        return new(source.Uuid("assetId"), source.Uuid("actorId"), source.Hash("mediaKey"), source.PositiveDecimal("bytes"), source.Hash("sha256"));
    }

    private static ApprovedImportInput ImportInput(JsonElement element)
    {
        var source = new ClosedObject(element, "type", "manifestSha256", "selectionSha256");
        return new(source.Hash("manifestSha256"), source.Hash("selectionSha256"));
    }
}

internal readonly struct ClosedObject
{
    private readonly JsonElement _element;

    internal ClosedObject(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ProtocolFormatException("Expected a closed object.");
        var expected = fields.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Remove(property.Name)) throw new ProtocolFormatException("Unknown or duplicate action field: " + property.Name);
        if (expected.Count != 0) throw new ProtocolFormatException("Missing captured action scope field.");
        _element = element;
    }

    internal JsonElement Value(string name) => _element.GetProperty(name);
    internal string String(string name) => Value(name).ValueKind == JsonValueKind.String ? Value(name).GetString()! :
        throw new ProtocolFormatException("Expected action string: " + name);
    internal bool Boolean(string name) => Value(name).ValueKind is JsonValueKind.True or JsonValueKind.False ? Value(name).GetBoolean() :
        throw new ProtocolFormatException("Expected action boolean: " + name);

    internal Guid Uuid(string name)
    {
        var value = String(name);
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || id.ToString("D") != value)
            throw new ProtocolFormatException("Expected canonical action UUID: " + name);
        return id;
    }

    internal string Hash(string name)
    {
        var value = String(name);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ProtocolFormatException("Invalid action digest: " + name);
        return value;
    }

    internal string OpaqueId(string name)
    {
        var value = String(name);
        if (value.Length is < 1 or > 256 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ProtocolFormatException("Invalid opaque Drive identity: " + name);
        return value;
    }

    internal long PositiveDecimal(string name)
    {
        var value = String(name);
        if (value.Length is < 1 or > 19 || value[0] == '0' || value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new ProtocolFormatException("Invalid positive action int64 string: " + name);
        return number;
    }
}
