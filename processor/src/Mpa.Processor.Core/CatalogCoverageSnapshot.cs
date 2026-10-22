using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.Core;

public sealed partial class CatalogFactsSnapshot
{
    /// <summary>Coverage and photo facts use the same owned WAL observation. No writable
    /// repository, network request, raw page token or inferred absence enters this result.
    /// Step counts describe accepted observations and outstanding work, not unique photos.</summary>
    public ReadOnlyCollection<CatalogSourceCoverage> ReadCoverageSources(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<CoverageBinding>();
        using (var command = Command("""
            SELECT s.id,s.account_id,s.folder_id,s.revision,s.descendants,s.enabled,a.revision,a.connected,
              c.job_id,c.source_revision,c.account_revision,j.kind,j.captured_context,j.context_hash,
              j.paused,j.canceled,length(CAST(j.captured_context AS BLOB))
            FROM source_binding s LEFT JOIN drive_account a ON a.permission_id=s.account_id
            LEFT JOIN current_source_scan c ON c.source_id=s.id LEFT JOIN job j ON j.id=c.job_id
            ORDER BY s.id LIMIT 257
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sources.Count == CatalogPublicationReader.MaximumCoverageSources)
                    throw new InvalidDataException("Captured coverage exceeds the supported source count.");
                sources.Add(CoverageBinding.Read(reader));
            }
        }
        var result = new List<CatalogSourceCoverage>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = source.Job is null ? null : ReadScan(source, cancellationToken);
            var state = !source.Enabled ? "disabled" : !source.Connected ? "disconnected" :
                scan is null || source.Scope!.SourceRevision != source.Revision || source.Scope.AccountRevision != source.AccountRevision ? "pending" : scan.State;
            result.Add(new(source.Id, source.Account, source.Folder, source.Revision, source.AccountRevision,
                source.Descendants, source.Enabled, source.Connected, state, scan?.Checkpoint));
        }
        cancellationToken.ThrowIfCancellationRequested(); return result.AsReadOnly();
    }

    private CoverageScan ReadScan(CoverageBinding source, CancellationToken cancellationToken)
    {
        var scope = source.Scope!;
        var identity = new DriveAssetIdentity(LibraryId);
        var journal = new SourceDiscoveryJournalCodec();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashFrame(digest, "mpa-source-coverage-observation-1");
        HashFrame(digest, JsonSerializer.Serialize(new { source.Job, source.ContextHash, source.Paused, source.Canceled }));
        long observed = 0, pending = 0, failed = 0;
        var incomplete = false; var attempted = false; var authorization = false; var offline = false;
        long stages = 0, verification = 0, folders = 0, repairs = 0;
        var rootFolder = false; var verified = false; var historicalFailures = false;
        using var command = Command("""
            SELECT s.item_id,s.name,s.state,s.attempt,s.checkpoint,s.result_receipt,s.error_code,s.retryable,
              i.captured_context,i.input_fingerprint,length(CAST(i.captured_context AS BLOB)),
              length(CAST(s.checkpoint AS BLOB)),length(CAST(s.result_receipt AS BLOB)),s.recipe_hash
            FROM item_stage s LEFT JOIN job_item i ON i.job_id=s.job_id AND i.id=s.item_id
            WHERE s.job_id=$job ORDER BY s.item_id,s.name
            """);
        command.Parameters.AddWithValue("$job", source.Job!.Value.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.IsDBNull(8) || reader.IsDBNull(9) || reader.GetInt64(10) > 32768 ||
                !reader.IsDBNull(11) && reader.GetInt64(11) > 32768 || !reader.IsDBNull(12) && reader.GetInt64(12) > 32768)
                throw new InvalidDataException("Captured scan lost its bounded durable item/checkpoint/receipt.");
            var item = CoverageGuid(reader.GetString(0)); var stage = reader.GetString(1); var state = reader.GetString(2);
            var attempt = reader.GetInt64(3); var checkpoint = reader.IsDBNull(4) ? null : reader.GetString(4);
            var receipt = reader.IsDBNull(5) ? null : reader.GetString(5); var code = reader.IsDBNull(6) ? null : reader.GetString(6);
            var context = reader.GetString(8);
            if (attempt < 0 || reader.GetString(9) != Digest(context) || state == "ready" && receipt is null || state != "ready" && receipt is not null)
                throw new InvalidDataException("Captured scan item integrity/state is inconsistent.");
            var recipe = stage switch { "list-metadata" => "mpa-source-metadata:1", "verify-source" => "mpa-source-verification:1",
                "repair-metadata" => "mpa-source-metadata-repair:1", _ => throw new InvalidDataException("Unknown captured scan recipe.") };
            if (reader.GetString(13) != Digest(recipe)) throw new InvalidDataException("Captured scan recipe identity changed.");
            switch (stage)
            {
                case "list-metadata":
                    var folder = journal.Folder(context);
                    CheckFolder(scope, folder);
                    if (item != identity.Derive(scope.AccountId, folder.FolderId)) throw new InvalidDataException("Captured folder identity changed.");
                    var progress = journal.Checkpoint(checkpoint);
                    if (state == "ready") ValidateFolderReceipt(journal, receipt!, scope, folder, progress);
                    observed = checked(observed + progress.ObservedItems); incomplete |= progress.IncompleteSearch;
                    historicalFailures |= progress.FailedItems > 0;
                    rootFolder |= folder.Depth == 0; folders = checked(folders + 1);
                    break;
                case "verify-source":
                    ValidateVerification(journal, context, checkpoint, receipt, scope, item, identity, state == "ready");
                    if (++verification > 1) throw new InvalidDataException("Captured scan repeats source verification.");
                    verified = state == "ready";
                    break;
                case "repair-metadata":
                    ValidateRepair(journal, context, receipt, scope, item, identity, state == "ready");
                    if (checkpoint is not null) throw new InvalidDataException("Metadata repair has an unsupported checkpoint.");
                    // A repaired item is an accepted observation; historical folder failure
                    // counters remain history and cannot keep a successful retry failed.
                    if (state == "ready") observed = checked(observed + 1);
                    repairs = checked(repairs + 1);
                    break;
                default: throw new InvalidDataException("Unknown scan stage cannot claim source coverage.");
            }
            if (state is "failed" or "unsupported")
            {
                if (string.IsNullOrEmpty(code)) throw new InvalidDataException("Failed scan work lost its diagnostic code.");
                failed = checked(failed + 1); authorization |= code == "drive-authorization";
                offline |= code is "drive-unavailable" or "drive-timeout";
            }
            else if (state is "queued" or "waiting-dependency" or "running" or "canceling" or "canceled") pending = checked(pending + 1);
            else if (state != "ready") throw new InvalidDataException("Unknown durable scan state.");
            attempted |= attempt > 0; stages = checked(stages + 1);
            HashFrame(digest, JsonSerializer.Serialize(new { item, stage, state, attempt, code, retryable = reader.GetBoolean(7),
                context = Digest(context), checkpoint = checkpoint is null ? null : Digest(checkpoint), receipt = receipt is null ? null : Digest(receipt) }));
        }
        if (stages == 0 || folders > 0 && !rootFolder || verified && !rootFolder || pending == 0 && failed == 0 && !rootFolder || historicalFailures && repairs == 0)
            throw new InvalidDataException("Captured scan lost its required folder work.");
        var status = authorization ? "access-lost" : offline ? "offline" : failed > 0 || incomplete ? "partial" :
            pending > 0 ? attempted ? "scanning" : "pending" : "complete";
        return new(status, new(source.Job.Value, scope.SourceRevision, scope.AccountRevision, observed, pending, failed,
            incomplete, Convert.ToHexStringLower(digest.GetHashAndReset())));
    }

    private static void ValidateFolderReceipt(SourceDiscoveryJournalCodec journal, string receipt, SourceDiscoveryScope scope,
        SourceDiscoveryFolder folder, SourceDiscoveryCheckpoint progress)
    {
        using var document = journal.Object(receipt, 6, "mpa-source-folder-receipt-1"); var root = document.RootElement;
        if (!progress.Started || progress.NextPageToken is not null || root.GetProperty("sourceId").GetString() != scope.SourceId.ToString("D") ||
            root.GetProperty("folderId").GetString() != folder.FolderId ||
            SourceDiscoveryJournalCodec.Decimal(root, "observedItems", true) != progress.ObservedItems ||
            SourceDiscoveryJournalCodec.Decimal(root, "failedItems", true) != progress.FailedItems ||
            root.GetProperty("incompleteSearch").GetBoolean() != progress.IncompleteSearch)
            throw new InvalidDataException("Completed folder receipt contradicts its terminal checkpoint.");
    }

    private static void ValidateVerification(SourceDiscoveryJournalCodec journal, string context, string? checkpoint, string? receipt,
        SourceDiscoveryScope scope, Guid item, DriveAssetIdentity identity, bool ready)
    {
        using var document = journal.Object(context, 2, "mpa-source-verification-1");
        if (document.RootElement.GetProperty("folderId").GetString() != scope.RootFolderId || item != identity.Derive(scope.AccountId, "verify-" + Digest(context)))
            throw new InvalidDataException("Captured verification changed its root/identity.");
        string? shared = null;
        if (checkpoint is not null)
        {
            using var saved = journal.Object(checkpoint, 3, "mpa-source-verified-root-1");
            shared = saved.RootElement.GetProperty("sharedDriveId").GetString();
            if (saved.RootElement.GetProperty("folderId").GetString() != scope.RootFolderId || shared is not null && !SourceDiscoveryJournalCodec.Identity(shared))
                throw new InvalidDataException("Captured verification checkpoint changed its root.");
        }
        if (!ready) return;
        using var completed = journal.Object(receipt!, 4, "mpa-source-verification-receipt-1"); var root = completed.RootElement;
        if (checkpoint is null || root.GetProperty("sourceId").GetString() != scope.SourceId.ToString("D") ||
            root.GetProperty("folderId").GetString() != scope.RootFolderId || root.GetProperty("sharedDriveId").GetString() != shared)
            throw new InvalidDataException("Completed verification lost its accepted root.");
    }

    private static void ValidateRepair(SourceDiscoveryJournalCodec journal, string context, string? receipt, SourceDiscoveryScope scope,
        Guid item, DriveAssetIdentity identity, bool ready)
    {
        using var document = journal.Object(context, 4, "mpa-source-repair-1"); var root = document.RootElement;
        var folder = journal.Folder(root.GetProperty("folder").GetRawText()); CheckFolder(scope, folder);
        var file = root.GetProperty("fileId").GetString(); var token = root.GetProperty("pageToken").GetString();
        if (file is not null && (!SourceDiscoveryJournalCodec.Identity(file) || token is not null) ||
            token is not null && (token.Length is < 1 or > 4096 || token.Any(char.IsControl)) || item != identity.Derive(scope.AccountId, "repair-" + Digest(context)))
            throw new InvalidDataException("Captured repair changed its file/page identity.");
        if (!ready) return;
        using var completed = journal.Object(receipt!, 5, "mpa-source-repair-receipt-1"); var accepted = completed.RootElement;
        if (accepted.GetProperty("sourceId").GetString() != scope.SourceId.ToString("D") || accepted.GetProperty("folderId").GetString() != folder.FolderId ||
            accepted.GetProperty("fileId").GetString() != file || accepted.GetProperty("pageToken").GetString() != token)
            throw new InvalidDataException("Completed repair changed its captured file/page.");
    }
    private static void CheckFolder(SourceDiscoveryScope scope, SourceDiscoveryFolder folder)
    {
        if (folder.Depth == 0 && folder.FolderId != scope.RootFolderId || folder.Depth > 0 && (!scope.IncludeDescendants || folder.FolderId == scope.RootFolderId))
            throw new InvalidDataException("Captured folder escaped its source policy.");
    }
    private static void HashFrame(IncrementalHash digest, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        digest.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":")); digest.AppendData(bytes);
    }
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Guid CoverageGuid(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value ? id :
        throw new InvalidDataException("Captured coverage identity is not canonical.");
    private sealed record CoverageScan(string State, CatalogScanCheckpoint Checkpoint);
    private sealed record CoverageBinding(Guid Id, string Account, string Folder, long Revision, bool Descendants, bool Enabled,
        long AccountRevision, bool Connected, Guid? Job, SourceDiscoveryScope? Scope, string? ContextHash, bool Paused, bool Canceled)
    {
        public static CoverageBinding Read(SqliteDataReader reader)
        {
            var id = CoverageGuid(reader.GetString(0)); var account = reader.GetString(1); var folder = reader.GetString(2);
            if (!SourceDiscoveryJournalCodec.Identity(account) || !SourceDiscoveryJournalCodec.Identity(folder) || folder == "root" ||
                reader.GetInt64(3) < 1 || reader.IsDBNull(6) || reader.GetInt64(6) < 1)
                throw new InvalidDataException("Captured source lost its canonical account binding/revisions.");
            Guid? job = null; SourceDiscoveryScope? scope = null; string? hash = null; var paused = false; var canceled = false;
            if (!reader.IsDBNull(8))
            {
                job = CoverageGuid(reader.GetString(8));
                if (reader.IsDBNull(11) || reader.GetString(11) != SourceDiscoveryRepository.JobKind || reader.IsDBNull(12) ||
                    reader.IsDBNull(13) || reader.GetInt64(16) > 32768)
                    throw new InvalidDataException("Selected scan lost its bounded durable job.");
                var context = reader.GetString(12); hash = reader.GetString(13);
                if (Digest(context) != hash) throw new InvalidDataException("Selected scan scope integrity changed.");
                scope = new SourceDiscoveryJournalCodec().Scope(context);
                if (scope.SourceId != id || scope.AccountId != account || scope.RootFolderId != folder ||
                    scope.SourceRevision != reader.GetInt64(9) || scope.AccountRevision != reader.GetInt64(10) ||
                    scope.SourceRevision > reader.GetInt64(3) || scope.AccountRevision > reader.GetInt64(6) ||
                    scope.SourceRevision == reader.GetInt64(3) && scope.IncludeDescendants != reader.GetBoolean(4))
                    throw new InvalidDataException("Selected scan contradicts its captured source binding/revisions.");
                paused = reader.GetBoolean(14); canceled = reader.GetBoolean(15);
            }
            return new(id, account, folder, reader.GetInt64(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt64(6), reader.GetBoolean(7), job, scope, hash, paused, canceled);
        }
    }
}
