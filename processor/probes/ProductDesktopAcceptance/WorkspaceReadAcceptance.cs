using System.Security.Cryptography;
using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.DesktopAcceptance;

/// <summary>Manual read-only acceptance using an already authorized isolated state and
/// the exact pre-existing synthetic JSON. No listener, new consent or Drive mutation.</summary>
internal static class WorkspaceReadAcceptance
{
    private const string FixtureSha256 = "9c8463ec6845a94966f1fcbe0081eedd19029b76d187b228e8021ec9565f87f1";
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("Expected packaged OAuth configuration, isolated account state, authorized disposable folder, pinned local JSON fixture and sanitized report.");
        var root = Path.GetFullPath(args[1]);
        if (!Path.GetFileName(root).StartsWith("desktop-account-", StringComparison.Ordinal) || Path.GetFileName(Path.GetDirectoryName(root)) != "build" ||
            !File.Exists(Path.Combine(root, "account-acceptance.json"))) throw new ArgumentException("Only the existing isolated product acceptance state is authorized.");
        var expected = new byte[92];
        using (var input = new FileStream(args[3], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length != expected.Length || Path.GetFileName(args[3]) != "manual-source-fixture.json") throw new InvalidDataException("Only the pinned synthetic fixture is authorized.");
            input.ReadExactly(expected);
            if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(expected)) != FixtureSha256) throw new InvalidDataException("The pinned synthetic fixture changed.");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        {
            using var database = new ProcessorStateDatabase(root);
            await using var state = new ProcessorStateCoordinator(database);
            var scope = await state.ExecuteAsync(db =>
            {
                var sources = new LibrarySourceRepository(db).Snapshot().Sources;
                if (sources.Count != 1 || !sources[0].Enabled || sources[0].FolderId != args[2]) throw new InvalidOperationException("Only the explicitly authorized disposable source may be used.");
                var account = new DriveAccountRepository(db).Accounts().Single(item => item.Connected && item.PermissionId == sources[0].AccountId);
                return account;
            }, deadline.Token).ConfigureAwait(false);
            await using var vault = new NativeCredentialVault(database.InstallationId, Path.Combine(root, "credentials"));
            await using var connection = new GoogleAccountConnection(DesktopOAuthConfiguration.Load(args[0]), vault, scope.ConnectionId, scope.PermissionId);
            var folder = await connection.ReadFolderAsync(args[2], deadline.Token).ConfigureAwait(false);
            var page = await connection.ReadSourcePageAsync(folder.Id, folder.SharedDriveId, null, deadline.Token).ConfigureAwait(false);
            if (page.Failures.Count != 0 || page.NextPageToken is not null || page.IncompleteSearch) throw new InvalidDataException("The bounded synthetic source fixture is incomplete.");
            var fixture = page.Items.Single(item => item.Name == "manual-source-fixture.json" && item.MimeType is "application/json" or "application/octet-stream" && item.Size == expected.Length);
            var verified = await connection.ReadWorkspaceObjectAsync(folder.Id, fixture.Id, expected.Length, FixtureSha256, deadline.Token).ConfigureAwait(false);
            if (!verified.Bytes.Span.SequenceEqual(expected) || verified.AccountId != scope.PermissionId || verified.FileId != fixture.Id || verified.FolderId != folder.Id)
                throw new InvalidDataException("The actual workspace read changed its captured identity or fixture bytes.");
            if (folder.Parents.Count != 1) throw new InvalidDataException("The synthetic source needs its established distinct parent for a wrong-workspace check.");
            await ExpectAsync<DriveWorkspaceObjectChangedException>(() => connection.ReadWorkspaceObjectAsync(folder.Parents[0], fixture.Id, expected.Length, FixtureSha256, deadline.Token)).ConfigureAwait(false);
            await ExpectAsync<DriveWorkspaceObjectChangedException>(() => connection.ReadWorkspaceObjectAsync(folder.Id, fixture.Id, expected.Length + 1, FixtureSha256, deadline.Token)).ConfigureAwait(false);
            await ExpectAsync<InvalidDataException>(() => connection.ReadWorkspaceObjectAsync(folder.Id, fixture.Id, expected.Length, new string('0', 64), deadline.Token)).ConfigureAwait(false);
            var image = page.Items.Single(item => item.Name == "ocr-latin.png" && item.MimeType == "image/png" && item.Size is > 0 and <= GoogleAccountConnection.MaximumWorkspaceObjectBytes);
            await ExpectAsync<InvalidDataException>(() => connection.ReadWorkspaceObjectAsync(folder.Id, image.Id, image.Size!.Value, new string('0', 64), deadline.Token)).ConfigureAwait(false);
        }
        // All actual connection/vault/state owners above have completed disposal before
        // this evidence is written. No pending refresh or local instance lease remains.
        await File.WriteAllBytesAsync(args[4], JsonSerializer.SerializeToUtf8Bytes(new
        {
            observedAtUtc = DateTimeOffset.UtcNow, actualWorkspaceReadVerified = true, fixtureBytes = expected.Length, fixtureSha256 = FixtureSha256,
            wrongWorkspaceRejected = true, changedSizeRejected = true, changedDigestRejected = true, originalMediaReferenceRejected = true,
            nativeOwnersJoined = true, listenerStarted = false, driveWritesPerformed = false, originalDownloaded = false, newConsentOccurred = false,
            scope = "Actual protected-account metadata transport acceptance on a pinned synthetic source only; signed generation and final release acceptance remain separate"
        }), deadline.Token).ConfigureAwait(false);
        Console.WriteLine("Actual pinned workspace read and scope/integrity rejection passed; protected owners joined. No media identities, names, URLs or credentials were logged.");
    }
    private static async Task ExpectAsync<T>(Func<Task<DriveWorkspaceObject>> operation) where T : Exception
    {
        try { _ = await operation().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException("The actual workspace adapter accepted a reference that must be rejected.");
    }
}
