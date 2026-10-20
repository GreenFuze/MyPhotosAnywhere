using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Credentials;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

[TestClass]
public sealed class AccountServiceTests
{
    [TestMethod]
    public async Task PromotionAndReconnectDisposeEveryOwnerAndCleanOnlyRetiredCredential()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Factory.Disposals);
        var replacement = await fixture.Service.ConnectAsync(first.PermissionId, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(first.ConnectionId, replacement.ConnectionId);
        Assert.AreEqual(first.PermissionId, replacement.PermissionId);
        Assert.AreEqual(2, fixture.Factory.Disposals);
        Assert.AreEqual(1, await fixture.Service.CleanCredentialsAsync(CancellationToken.None).ConfigureAwait(false));
        CollectionAssert.AreEqual(new[] { "google-oauth-v1:" + first.ConnectionId.ToString("N") }, fixture.Vault.Deleted.ToArray());
        var snapshot = await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, snapshot.Cleanup.Count);
        Assert.AreEqual(replacement, snapshot.Accounts.Single());
    }

    [TestMethod]
    public async Task FailedSetupRemainsPendingAndRetryUsesSameCapturedAttempt()
    {
        await using var fixture = new Fixture();
        fixture.Factory.Failure = new HttpRequestException("Synthetic unavailable network");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None)).ConfigureAwait(false);
        var failed = await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, failed.Accounts.Count);
        Assert.AreEqual(1, failed.Pending.Count);
        Assert.AreEqual(1, fixture.Factory.Disposals);
        fixture.Factory.Failure = null;
        var restored = await fixture.Service.ResumePendingAsync(failed.Pending.Single().Id, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(failed.Pending.Single().Id, restored.ConnectionId);
        Assert.AreEqual(0, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Pending.Count);
    }

    [TestMethod]
    public async Task WrongAccountDoesNotPromoteOrScheduleExistingCredentialDeletion()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        fixture.Factory.PermissionId = "permission-wrong";
        await Assert.ThrowsExactlyAsync<StaleDriveConnectionException>(() => fixture.Service.ConnectAsync(first.PermissionId, new NoBrowser(), CancellationToken.None)).ConfigureAwait(false);
        var snapshot = await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first, snapshot.Accounts.Single());
        Assert.AreEqual(0, snapshot.Cleanup.Count);
        Assert.AreEqual("permission-A", snapshot.Pending.Single().ExpectedPermissionId);
    }

    [TestMethod]
    public async Task LockedCredentialCleanupKeepsItsJournalUntilARealSuccessfulRetry()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(account.PermissionId, account.Revision, CancellationToken.None).ConfigureAwait(false);
        fixture.Vault.FailDelete = true;
        await Assert.ThrowsExactlyAsync<CredentialVaultException>(() => fixture.Service.CleanCredentialsAsync(CancellationToken.None)).ConfigureAwait(false);
        var snapshot = await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(snapshot.Accounts.Single().Connected);
        Assert.AreEqual(1, snapshot.Cleanup.Count);
        fixture.Vault.FailDelete = false;
        Assert.AreEqual(1, await fixture.Service.CleanCredentialsAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Cleanup.Count);
    }

    [TestMethod]
    public async Task ExitCancelsButStillJoinsTheActualConnectionAndItsDisposal()
    {
        await using var fixture = new Fixture();
        fixture.Factory.Held = true;
        var operation = fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None);
        await fixture.Factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stop = fixture.Service.DisposeAsync().AsTask();
        await fixture.Factory.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try { Assert.IsFalse(stop.IsCompleted); Assert.AreEqual(0, fixture.Factory.Disposals); }
        finally { fixture.Factory.Release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation).ConfigureAwait(false);
        await stop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Factory.Disposals);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => fixture.Service.SnapshotAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mpa-account-service-" + Guid.NewGuid().ToString("N"));
        private readonly ProcessorStateDatabase _database;
        private readonly ProcessorStateCoordinator _state;
        public FakeVault Vault { get; } = new();
        public Factory Factory { get; } = new();
        public ProcessorAccountService Service { get; }
        public Fixture()
        {
            _database = new ProcessorStateDatabase(_root);
            _state = new ProcessorStateCoordinator(_database);
            Service = new(_state, Vault, Factory);
        }
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync().ConfigureAwait(false);
            await _state.DisposeAsync().ConfigureAwait(false);
            _database.Dispose();
            // No recursive deletion: fixture contains only synthetic local state; retain evidence.
        }
    }
    private sealed class FakeVault : ICredentialVault
    {
        public List<string> Deleted { get; } = [];
        public bool FailDelete { get; set; }
        public Task<SecretLease?> ReadAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("Not used by this fixture.");
        public Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new InvalidOperationException("Not used by this fixture.");
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailDelete) throw new CredentialVaultException("Synthetic locked keyring");
            Deleted.Add(key); return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Factory : IGoogleAccountConnectionFactory
    {
        public string PermissionId { get; set; } = "permission-A";
        public Exception? Failure { get; set; }
        public int Disposals { get; private set; }
        public bool Held { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IGoogleAccountConnection Create(Guid connectionId, string? expectedPermissionId) => new Connection(this, connectionId);
        private sealed class Connection(Factory owner, Guid id) : IGoogleAccountConnection
        {
            public async Task<ConnectedDriveAccount> AuthorizeAsync(IAuthorizationBrowser browser, CancellationToken cancellationToken)
            {
                if (owner.Held)
                {
                    owner.Started.TrySetResult();
                    using var registration = cancellationToken.Register(() => owner.Canceled.TrySetResult());
                    await owner.Release.Task.ConfigureAwait(false); // Emulate a native owner that must actually join.
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (owner.Failure is not null) throw owner.Failure;
                return new(id, owner.PermissionId, "Synthetic", null);
            }
            public async Task<ConnectedDriveAccount?> CheckAsync(CancellationToken cancellationToken) => await AuthorizeAsync(new NoBrowser(), cancellationToken).ConfigureAwait(false);
            public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }
    private sealed class NoBrowser : IAuthorizationBrowser
    {
        public void Open(Uri authorizationUri) => throw new InvalidOperationException("No browser/network call is allowed in these service fixtures.");
    }
}
