using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mpa.Processor.GoogleDrive.Tests;

[TestClass]
public sealed class GoogleCredentialTests
{
    internal static TokenResponse Grant() => new() { AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh",
        Scope = GoogleCredentialDataStore.DriveScope, TokenType = "Bearer", ExpiresInSeconds = 3600, IssuedUtc = DateTime.UtcNow };

    [TestMethod]
    public async Task ExpiredAccessTokenKeepsItsRefreshCredentialAndIssueTimeAcrossRestart()
    {
        await using var vault = new TestCredentialVault();
        var id = Guid.NewGuid();
        var store = new GoogleCredentialDataStore(vault, id, () => CancellationToken.None);
        var grant = Grant(); grant.IssuedUtc = DateTime.UtcNow.AddDays(-8);
        await store.StoreAsync(store.UserId, grant).ConfigureAwait(false);
        var reopened = new GoogleCredentialDataStore(vault, id, () => CancellationToken.None);
        var read = await reopened.GetAsync<TokenResponse>(store.UserId).ConfigureAwait(false);
        Assert.AreEqual(grant.IssuedUtc, read.IssuedUtc);
        Assert.AreEqual(grant.RefreshToken, read.RefreshToken);
        Assert.AreEqual(grant.Scope, read.Scope);
    }

    [TestMethod]
    public async Task RefreshResponseMayOmitUnchangedScopeAndRefreshToken()
    {
        await using var vault = new TestCredentialVault();
        var store = new GoogleCredentialDataStore(vault, Guid.NewGuid(), () => CancellationToken.None);
        await store.StoreAsync(store.UserId, Grant()).ConfigureAwait(false);
        var refreshed = Grant(); refreshed.AccessToken = "new-synthetic"; refreshed.RefreshToken = null; refreshed.Scope = null; refreshed.TokenType = null;
        await store.StoreAsync(store.UserId, refreshed).ConfigureAwait(false);
        var read = await store.GetAsync<TokenResponse>(store.UserId).ConfigureAwait(false);
        Assert.AreEqual("new-synthetic", read.AccessToken);
        Assert.AreEqual("synthetic-refresh", read.RefreshToken);
        Assert.AreEqual(GoogleCredentialDataStore.DriveScope, read.Scope);
    }

    [TestMethod]
    public async Task InvalidResponseDoesNotReplaceOldGrantAndClearAffectsOnlyItsConnection()
    {
        await using var vault = new TestCredentialVault();
        var first = new GoogleCredentialDataStore(vault, Guid.NewGuid(), () => CancellationToken.None);
        var other = new GoogleCredentialDataStore(vault, Guid.NewGuid(), () => CancellationToken.None);
        await first.StoreAsync(first.UserId, Grant()).ConfigureAwait(false);
        await other.StoreAsync(other.UserId, Grant()).ConfigureAwait(false);
        var invalid = Grant(); invalid.Scope = "https://www.googleapis.com/auth/drive.file";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => first.StoreAsync(first.UserId, invalid)).ConfigureAwait(false);
        Assert.AreEqual(GoogleCredentialDataStore.DriveScope, (await first.GetAsync<TokenResponse>(first.UserId).ConfigureAwait(false)).Scope);
        await first.ClearAsync().ConfigureAwait(false);
        Assert.IsNull(await first.GetAsync<TokenResponse>(first.UserId).ConfigureAwait(false));
        Assert.IsNotNull(await other.GetAsync<TokenResponse>(other.UserId).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => other.GetAsync<TokenResponse>(first.UserId)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => other.GetAsync<string>(other.UserId)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CorruptDuplicateAndUnknownRecordFieldsAreNotAnEmptyAccount()
    {
        await using var vault = new TestCredentialVault();
        var store = new GoogleCredentialDataStore(vault, Guid.NewGuid(), () => CancellationToken.None);
        await store.StoreAsync(store.UserId, Grant()).ConfigureAwait(false);
        var key = vault.Records.Keys.Single();
        var valid = Encoding.UTF8.GetString(vault.Records[key]);
        vault.Records[key] = Encoding.UTF8.GetBytes(valid.Replace("{", "{\"Version\":1,", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.GetAsync<TokenResponse>(store.UserId)).ConfigureAwait(false);
        vault.Records[key] = Encoding.UTF8.GetBytes(valid.Replace("\"Version\":1", "\"Unknown\":1", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<JsonException>(() => store.GetAsync<TokenResponse>(store.UserId)).ConfigureAwait(false);
        Assert.IsTrue(vault.Records.ContainsKey(key));
    }
}
