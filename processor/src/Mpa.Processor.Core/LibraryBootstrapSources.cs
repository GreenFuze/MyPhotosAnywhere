using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed record LibraryBootstrapApproval(long ConfigurationRevision, bool AllowSharedMetadata);
public sealed record LibraryBootstrapSources(long ConfigurationRevision, bool AllowSharedMetadata, string CanonicalSnapshot, string Sha256);
public sealed class LibraryBootstrapApprovalConflictException() : InvalidOperationException("The initial source approval is immutable. Apply later source changes through their separately reviewed policy.");

public sealed partial class LibraryAuthorityRepository
{
    private const string BootstrapSourcesKey = "library_bootstrap_sources_v1";

    /// <summary>Original human-approved association, not a signature, recognition consent
    /// or permission to mutate originals. Later source changes must get their own policy;
    /// neither a pinned key nor a currently connected account implies approval.</summary>
    public LibraryBootstrapSources? BootstrapSources()
    {
        using var transaction = _database.Connection.BeginTransaction(); var pin = ReadPin(transaction);
        var result = pin is null ? null : ReadBootstrapSources(transaction, pin); transaction.Commit(); return result;
    }

    /// <summary>Admission for the initial source policy only. Never automatically replace
    /// the original approved snapshot with the latest local settings after restart.</summary>
    public LibraryBootstrapSources AssertBootstrapSourcesCurrent()
    {
        using var transaction = _database.Connection.BeginTransaction(); var pin = ReadPin(transaction)
            ?? throw new InvalidOperationException("Approve library setup first.");
        var approved = ReadBootstrapSources(transaction, pin) ?? throw new InvalidOperationException("The initial source association was not explicitly approved.");
        var current = new LibrarySourceRepository(_database).Snapshot(transaction);
        if (current.Revision != approved.ConfigurationRevision || CaptureBootstrapSources(pin, current, approved.AllowSharedMetadata) != approved.CanonicalSnapshot)
            throw new StaleLibraryConfigurationException();
        transaction.Commit(); return approved;
    }

    // The transaction owner retains this lease through its actual commit.
    private StorageReservation? StoreBootstrapSources(SqliteTransaction transaction, LibraryControllerPin pin, LibraryBootstrapApproval approval)
    {
        if (approval.ConfigurationRevision < 1) throw new ArgumentException("An explicitly reviewed configuration revision is required.", nameof(approval));
        var current = new LibrarySourceRepository(_database).Snapshot(transaction);
        if (current.Revision != approval.ConfigurationRevision) throw new StaleLibraryConfigurationException();
        var text = CaptureBootstrapSources(pin, current, approval.AllowSharedMetadata); var existing = ReadBootstrapSources(transaction, pin);
        if (existing is not null)
        {
            if (existing.CanonicalSnapshot != text) throw new LibraryBootstrapApprovalConflictException();
            return null;
        }
        var reservation = _budget.Reserve(checked(Encoding.UTF8.GetByteCount(text) * 3L + 65536));
        try
        {
            reservation.RecheckBeforeWrite(reservation.Bytes);
            Insert(transaction, BootstrapSourcesKey, text); Insert(transaction, BootstrapSourcesKey + ":sha256", Hash(Encoding.UTF8.GetBytes(text)));
            return reservation;
        }
        catch { reservation.Dispose(); throw; }
    }

    private static string CaptureBootstrapSources(LibraryControllerPin pin, LibrarySourceSnapshot snapshot, bool shared) => Encoding.UTF8.GetString(
        new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "mpa-bootstrap-sources", major = 1, libraryId = pin.LibraryId.ToString("D"), controllerId = pin.ActorId.ToString("D"),
            controllerEpoch = pin.Epoch.ToString(CultureInfo.InvariantCulture), controllerKeyId = pin.KeyId,
            workspace = new { accountId = pin.AccountId, folderId = pin.FolderId }, configurationRevision = snapshot.Revision.ToString(CultureInfo.InvariantCulture),
            allowSharedMetadata = shared,
            accounts = snapshot.Sources.Select(source => source.AccountId).Append(pin.AccountId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            sources = snapshot.Sources.OrderBy(source => source.Id.ToString("D"), StringComparer.Ordinal).Select(source => new
            { id = source.Id.ToString("D"), accountId = source.AccountId, folderId = source.FolderId, revision = source.Revision.ToString(CultureInfo.InvariantCulture), includeDescendants = source.IncludeDescendants, enabled = source.Enabled }).ToArray()
        })));

    private LibraryBootstrapSources? ReadBootstrapSources(SqliteTransaction transaction, LibraryControllerPin pin)
    {
        var text = ReadMeta(transaction, BootstrapSourcesKey, ProtocolDocumentReader.MaximumDocumentBytes); var hash = ReadMeta(transaction, BootstrapSourcesKey + ":sha256", 64);
        if (text is null && hash is null) return null;
        if (text is null || hash is null || Hash(Encoding.UTF8.GetBytes(text)) != hash) throw new InvalidDataException("The captured initial source approval is incomplete or corrupt.");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text); var canonical = new CanonicalJson(ProtocolDocumentReader.MaximumDocumentBytes).Encode(bytes);
            if (!bytes.AsSpan().SequenceEqual(canonical)) throw new InvalidDataException("The initial source approval is not canonical.");
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 11 || root.GetProperty("format").GetString() != "mpa-bootstrap-sources" || root.GetProperty("major").GetInt32() != 1 ||
                root.GetProperty("libraryId").GetString() != pin.LibraryId.ToString("D") || root.GetProperty("controllerId").GetString() != pin.ActorId.ToString("D") ||
                root.GetProperty("controllerEpoch").GetString() != pin.Epoch.ToString(CultureInfo.InvariantCulture) || root.GetProperty("controllerKeyId").GetString() != pin.KeyId ||
                root.GetProperty("workspace").GetProperty("accountId").GetString() != pin.AccountId || root.GetProperty("workspace").GetProperty("folderId").GetString() != pin.FolderId)
                throw new InvalidDataException("The initial source approval does not match its pinned controller/workspace.");
            var revision = Counter(root.GetProperty("configurationRevision").GetString()!); var shared = root.GetProperty("allowSharedMetadata").GetBoolean();
            var accounts = root.GetProperty("accounts"); var sources = root.GetProperty("sources");
            if (accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() is < 1 or > 128 || sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > 256)
                throw new InvalidDataException("The initial source approval exceeds its bounds.");
            var captured = new List<LibrarySourceRecord>();
            foreach (var source in sources.EnumerateArray())
            {
                var raw = source.GetProperty("id").GetString();
                if (!Guid.TryParseExact(raw, "D", out var id) || id == Guid.Empty || id.ToString("D") != raw) throw new InvalidDataException("Invalid approved source identity.");
                var account = source.GetProperty("accountId").GetString()!; var folder = source.GetProperty("folderId").GetString()!; _ = new CatalogWorkspace(account, folder);
                if (folder == "root" || captured.Any(item => item.Id == id || item.AccountId == account && item.FolderId == folder)) throw new InvalidDataException("Invalid duplicate/alias source in approval.");
                captured.Add(new(id, account, folder, "", source.GetProperty("includeDescendants").GetBoolean(), source.GetProperty("enabled").GetBoolean(), Counter(source.GetProperty("revision").GetString()!), false));
            }
            if (CaptureBootstrapSources(pin, new(revision, captured), shared) != text) throw new InvalidDataException("The initial source approval changed its associations, flags or ordering.");
            return new(revision, shared, text, hash);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("The initial source approval cannot be decoded; never replace it from current settings.", error); }
    }

    private bool HasBootstrapSources(SqliteTransaction? transaction) => ReadMeta(transaction, BootstrapSourcesKey, ProtocolDocumentReader.MaximumDocumentBytes) is not null ||
        ReadMeta(transaction, BootstrapSourcesKey + ":sha256", 64) is not null;
}
