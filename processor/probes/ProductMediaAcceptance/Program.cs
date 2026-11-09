using System.Text.Json;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

// Read-only product integration using an already authorized, isolated account state. No
// browser/OAuth prompt, Drive writes, media IDs/names/URLs or credentials in the report.
if (args.Length != 3) throw new ArgumentException("Expected packaged desktop OAuth configuration, existing isolated account state and sanitized report path.");
var root = Path.GetFullPath(args[1]);
if (!Path.GetFileName(root).StartsWith("desktop-account-", StringComparison.Ordinal) || Path.GetFileName(Path.GetDirectoryName(root)) != "build")
    throw new ArgumentException("An existing isolated product acceptance state is required.");
if (!File.Exists(Path.Combine(root, "account-acceptance.json"))) throw new InvalidDataException("The approved product account acceptance marker is missing.");
var configuration = DesktopOAuthConfiguration.Load(args[0]);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
using var database = new ProcessorStateDatabase(root);
await using var state = new ProcessorStateCoordinator(database);
await using var vault = new NativeCredentialVault(database.InstallationId, Path.Combine(root, "credentials"));
var accounts = await state.ExecuteAsync(db => new DriveAccountRepository(db).Accounts().Where(account => account.Connected).ToArray(), deadline.Token).ConfigureAwait(false);
if (accounts.Length != 1) throw new InvalidOperationException("This read-only acceptance fixture requires exactly one connected account.");
await using var connection = new GoogleAccountConnection(configuration, vault, accounts[0].ConnectionId, accounts[0].PermissionId);
var page = await connection.ReadMediaPageAsync(null, deadline.Token).ConfigureAwait(false);
if (page.Files.Count == 0) throw new InvalidOperationException("No actual media is available for product thumbnail acceptance; this is not a passing empty fixture.");
var preview = await connection.ReadMediaPreviewAsync(page.Files[0].Id, deadline.Token).ConfigureAwait(false)
    ?? throw new InvalidOperationException("The selected media does not have an available Drive thumbnail.");
await File.WriteAllBytesAsync(args[2], JsonSerializer.SerializeToUtf8Bytes(new
{
    observedAtUtc = DateTimeOffset.UtcNow, productReadOnlyPagePassed = true, pageCount = page.Files.Count,
    thumbnailPassed = true, thumbnailBytes = preview.Bytes.Length, mimeType = preview.MimeType,
    originalDownloaded = false, driveWritesPerformed = false, newConsentOccurred = false,
    scope = "Intermediate product read-only media adapter acceptance; not full catalog, viewer-original or final release acceptance"
}), deadline.Token).ConfigureAwait(false);
Console.WriteLine("Actual read-only Drive page and thumbnail passed. No media identities, names, URLs or Google credentials were logged.");
