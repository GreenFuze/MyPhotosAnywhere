using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task WorkspaceInspectionDoesNotPinTrustAndExplicitApprovalRetainsNativeActors()
    {
        await using var fixture = await SetupFixture.CreateAsync().ConfigureAwait(false);
        var preview = await fixture.Setup.InspectAsync("permission-A", "metadata", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(preview.Controller); Assert.IsNull(await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
        var approval = Approval(preview); var pin = await fixture.Setup.ApproveAsync(approval, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(preview.Identity.ControllerId, pin.ActorId); Assert.AreEqual(preview.Identity.ControllerKeyId, pin.KeyId);
        Assert.AreEqual(pin, await fixture.Setup.ApproveAsync(approval, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(pin, await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
        var approvedSources = await fixture.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, new(new SetupSpace())).AssertBootstrapSourcesCurrent(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(preview.Sources.Revision, approvedSources.ConfigurationRevision); Assert.IsFalse(approvedSources.AllowSharedMetadata);
        await Assert.ThrowsExactlyAsync<ControllerAuthorityConflictException>(() => fixture.Setup.ApproveAsync(approval with { FolderId = "another-workspace" }, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(pin, await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FreshBootstrapRequiresVerifiedEmptyWorkspaceButExistingPinReplaysDoNotRequireAnEmptyPublishedLibrary()
    {
        await using var fixture = await SetupFixture.CreateAsync().ConfigureAwait(false);
        var preview = await fixture.Setup.InspectAsync("permission-A", "metadata", CancellationToken.None).ConfigureAwait(false); var approval = Approval(preview);
        fixture.Empty = false;
        await Assert.ThrowsExactlyAsync<LibraryWorkspaceApprovalException>(() => fixture.Setup.ApproveAsync(approval, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
        fixture.Empty = true; var pin = await fixture.Setup.ApproveAsync(approval, CancellationToken.None).ConfigureAwait(false);
        fixture.Empty = false; Assert.AreEqual(pin, await fixture.Setup.ApproveAsync(approval, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SharedWorkspaceRequiresAcknowledgmentAndSourcesRequireExplicitAssociation()
    {
        await using var fixture = await SetupFixture.CreateAsync().ConfigureAwait(false); fixture.Shared = true;
        var preview = await fixture.Setup.InspectAsync("permission-A", "metadata", CancellationToken.None).ConfigureAwait(false); Assert.IsTrue(preview.Workspace.Shared);
        var approval = Approval(preview);
        await Assert.ThrowsExactlyAsync<LibraryWorkspaceApprovalException>(() => fixture.Setup.ApproveAsync(approval, CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<LibraryWorkspaceApprovalException>(() => fixture.Setup.ApproveAsync(approval with { AcceptSharedWorkspace = true, AssociateSelectedSources = false }, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await fixture.Setup.ApproveAsync(approval with { AcceptSharedWorkspace = true }, CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue((await fixture.Accounts.State.ExecuteAsync(database => new LibraryAuthorityRepository(database, new(new SetupSpace())).AssertBootstrapSourcesCurrent(), CancellationToken.None).ConfigureAwait(false)).AllowSharedMetadata);
    }

    [TestMethod]
    public async Task ChangedFolderAccountConfigurationOrNativeFingerprintCannotApproveAnOldPreview()
    {
        foreach (var change in new[] { "folder", "configuration", "key", "account" })
        {
            await using var fixture = await SetupFixture.CreateAsync().ConfigureAwait(false);
            var preview = await fixture.Setup.InspectAsync("permission-A", "metadata", CancellationToken.None).ConfigureAwait(false); var approval = Approval(preview);
            switch (change)
            {
                case "folder": fixture.Version++; break;
                case "configuration": await fixture.AddSourceAsync("photos").ConfigureAwait(false); break;
                case "key": approval = approval with { ControllerKeyId = new string('0', 64) }; break;
                case "account": await fixture.Accounts.Service.ConnectAsync("permission-A", new NoBrowser(), CancellationToken.None).ConfigureAwait(false); break;
            }
            await Assert.ThrowsExactlyAsync<LibraryWorkspaceApprovalException>(() => fixture.Setup.ApproveAsync(approval, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsNull(await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
        }
    }

    [TestMethod]
    public async Task ASelectedPhotoSourceCannotBecomeItsOwnManagedMetadataWorkspace()
    {
        await using var fixture = await SetupFixture.CreateAsync().ConfigureAwait(false); await fixture.AddSourceAsync("metadata").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<LibraryWorkspaceApprovalException>(() => fixture.Setup.InspectAsync("permission-A", "metadata", CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DisconnectDuringRealInspectionRejectsItsLateResultWithoutPinningTrust()
    {
        await using var fixture = await SetupFixture.CreateAsync().ConfigureAwait(false);
        fixture.Accounts.Readers.WorkspaceInspect = async (folder, _) =>
        {
            await fixture.Accounts.Service.DisconnectAsync("permission-A", 1, CancellationToken.None).ConfigureAwait(false);
            return new("permission-A", folder, "Metadata", 7, false, true);
        };
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(() => fixture.Setup.InspectAsync("permission-A", "metadata", CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CancellationJoinsTheActualInspectionAndNeverAcknowledgesApproval()
    {
        await using var fixture = await SetupFixture.CreateAsync().ConfigureAwait(false);
        using var caller = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Accounts.Readers.WorkspaceInspect = async (folder, _) =>
        { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return new("permission-A", folder, "Metadata", 7, false, true); };
        var reading = fixture.Setup.InspectAsync("permission-A", "metadata", caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); await caller.CancelAsync().ConfigureAwait(false);
        try { Assert.IsFalse(reading.IsCompleted); } finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false);
        Assert.IsNull(await fixture.Setup.CurrentAsync(CancellationToken.None).ConfigureAwait(false));
    }

    private static ApproveLibraryWorkspace Approval(LibraryWorkspaceInspection preview) => new(preview.Workspace.AccountId, preview.Workspace.FolderId,
        preview.AccountRevision, preview.Workspace.DriveVersion, preview.Sources.Revision, preview.Identity.ControllerKeyId, false, true);
    private sealed class SetupFixture : IAsyncDisposable
    {
        public Fixture Accounts { get; } = new();
        private readonly SetupSigningVault _vault = new();
        public LibrarySigningIdentityOwner Keys { get; private set; } = null!;
        public LibraryWorkspaceSetup Setup { get; private set; } = null!;
        public long Version { get; set; } = 7;
        public bool Shared { get; set; }
        public bool Empty { get; set; } = true;
        public static async Task<SetupFixture> CreateAsync()
        {
            var fixture = new SetupFixture();
            try
            {
                await fixture.Accounts.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
                var identity = await fixture.Accounts.State.ExecuteAsync(database => (Library: new ProcessorLibraryRepository(database).GetOrCreateIdentity(), database.InstallationId), CancellationToken.None).ConfigureAwait(false);
                fixture.Keys = new(fixture._vault, identity.Library, identity.InstallationId, null); await fixture.Keys.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                fixture.Setup = new(fixture.Accounts.State, fixture.Accounts.Service, fixture.Keys, new(new SetupSpace()));
                fixture.Accounts.Readers.WorkspaceInspect = (folder, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new DriveWorkspaceAccess("permission-A", folder, "Metadata", fixture.Version, fixture.Shared, fixture.Empty)); };
                return fixture;
            }
            catch { await fixture.DisposeAsync().ConfigureAwait(false); throw; }
        }
        public Task<LibrarySourceSnapshot> AddSourceAsync(string folder) => Accounts.State.ExecuteAsync(database =>
        {
            var repository = new LibrarySourceRepository(database); return repository.Add(Guid.NewGuid(), "permission-A", 1, folder, "Photos", true, repository.Snapshot().Revision);
        }, CancellationToken.None);
        public async ValueTask DisposeAsync()
        { await Accounts.DisposeAsync().ConfigureAwait(false); if (Keys is not null) await Keys.DisposeAsync().ConfigureAwait(false); await _vault.DisposeAsync().ConfigureAwait(false); }
    }
    private sealed class SetupSpace : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
    private sealed class SetupSigningVault : ICredentialVault
    {
        private byte[]? _bytes;
        public Task<SecretLease?> ReadAsync(string key, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(_bytes is null ? null : SecretLease.CopyFrom(_bytes)); }
        public Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (_bytes is not null) CryptographicOperations.ZeroMemory(_bytes); _bytes = bytes.ToArray(); return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken token) => throw new InvalidOperationException("Workspace approval cannot remove signing keys.");
        public ValueTask DisposeAsync() { if (_bytes is not null) CryptographicOperations.ZeroMemory(_bytes); return ValueTask.CompletedTask; }
    }
}
