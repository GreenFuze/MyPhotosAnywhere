using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

// Private Windows acceptance only. Reuses the already approved probe grant for the same
// Desktop client/full-Drive scope; no browser consent, raw-secret export or Drive mutation.
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This acceptance bridge reads the earlier Windows-only DPAPI probe grant.");
if (args.Length != 3) throw new ArgumentException("Expected private desktop client, approved private probe directory, and a new isolated processor/build/desktop-account-<uuid> state directory.");
var target = Path.GetFullPath(args[2]);
if (Directory.Exists(target) || Path.GetFileName(Path.GetDirectoryName(target)) != "build" ||
    !Path.GetFileName(target).StartsWith("desktop-account-", StringComparison.Ordinal) ||
    !Guid.TryParseExact(Path.GetFileName(target)[16..], "N", out _))
    throw new ArgumentException("Acceptance requires a new isolated build/desktop-account-<uuid> directory.");
var source = Path.GetFullPath(args[1]);
using var probeLease = new FileStream(Path.Combine(source, "probe.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
var recordName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(TokenResponse).FullName + ":stage-zero-account"))) + ".dpapi";
var sourceRecord = Path.Combine(source, "vault-drive", recordName);
if ((File.GetAttributes(sourceRecord) & FileAttributes.ReparsePoint) != 0) throw new IOException("Private probe record cannot be a link.");
using var encryptedInput = File.OpenRead(sourceRecord);
if (encryptedInput.Length is <= 0 or > 65536) throw new InvalidDataException("Private probe record exceeds its established bound.");
var encrypted = new byte[(int)encryptedInput.Length];
encryptedInput.ReadExactly(encrypted);
var entropy = SHA256.HashData("MPA Stage 0 credential probe v1"u8);
var plaintext = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
TokenResponse grant;
try { grant = JsonSerializer.Deserialize<TokenResponse>(plaintext) ?? throw new InvalidDataException("Approved probe grant is missing."); }
finally { CryptographicOperations.ZeroMemory(plaintext); CryptographicOperations.ZeroMemory(entropy); }
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var configuration = DesktopOAuthConfiguration.Load(args[0]);
using var database = new ProcessorStateDatabase(target);
await using var state = new ProcessorStateCoordinator(database);
await using var vault = new NativeCredentialVault(database.InstallationId, Path.Combine(target, "credentials"));
var pending = await state.ExecuteAsync(db => new DriveAccountRepository(db).Begin(Guid.NewGuid()), deadline.Token).ConfigureAwait(false);
var store = new GoogleCredentialDataStore(vault, pending.Id, () => deadline.Token);
await store.StoreAsync(store.UserId, grant).ConfigureAwait(false);
await using var connection = new GoogleAccountConnection(configuration, vault, pending.Id);
var account = await connection.CheckAsync(deadline.Token).ConfigureAwait(false) ?? throw new InvalidDataException("Product did not restore the protected grant.");
await state.ExecuteAsync(db => new DriveAccountRepository(db).Promote(pending.Id, account.PermissionId, account.DisplayName, account.EmailAddress), deadline.Token).ConfigureAwait(false);
await File.WriteAllBytesAsync(Path.Combine(target, "account-acceptance.json"), JsonSerializer.SerializeToUtf8Bytes(new
{
    observedAtUtc = DateTimeOffset.UtcNow,
    approvedGrantReused = true,
    productNativeCredentialStored = true,
    stableAccountIdentityVerified = true,
    interactiveConsentOccurred = false,
    driveWritesPerformed = false,
    previousAccessTokenExpired = grant.ExpiresInSeconds is { } lifetime && grant.IssuedUtc.AddSeconds(lifetime) < DateTime.UtcNow,
    scope = "Intermediate product Windows account-check acceptance only; not final installer or elapsed-seven-day acceptance"
}), deadline.Token).ConfigureAwait(false);
Console.WriteLine("Product protected-store/account check passed. No token or account identity was logged; no Drive file was changed.");
