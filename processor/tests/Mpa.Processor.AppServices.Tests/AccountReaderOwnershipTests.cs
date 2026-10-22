using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly string[] RetirementEvents = ["refresh-saved", "reader-disposed", "credential-deleted"];
    [TestMethod]
    public async Task DisconnectJoinsRefreshAndReaderDisposalBeforeDeletingRetiredCredential()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.HoldRead = true; fixture.Readers.HoldDisposal = true;
        var reading = ReadAsync(fixture);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(account.PermissionId, account.Revision, CancellationToken.None).ConfigureAwait(false);
        var cleanup = fixture.Service.CleanCredentialsAsync(CancellationToken.None);
        try { Assert.IsFalse(cleanup.IsCompleted); Assert.AreEqual(0, fixture.Vault.Deleted.Count); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await fixture.Readers.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try { Assert.IsFalse(cleanup.IsCompleted); Assert.AreEqual(0, fixture.Vault.Deleted.Count); }
        finally { fixture.Readers.ReleaseDisposal.TrySetResult(); }
        Assert.AreEqual(1, await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        CollectionAssert.AreEqual(RetirementEvents, fixture.Readers.Events.ToArray());
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => ReadAsync(fixture)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Readers.Opened.Count);
    }

    [TestMethod]
    public async Task ReconnectionRetiresOldReaderAndUsesOnlyTheVerifiedReplacementForNewReads()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1L, await ReadAsync(fixture).ConfigureAwait(false));
        var replacement = await fixture.Service.ConnectAsync(first.PermissionId, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, await fixture.Service.CleanCredentialsAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(2L, await ReadAsync(fixture).ConfigureAwait(false));
        CollectionAssert.AreEqual(new[] { first.ConnectionId, replacement.ConnectionId }, fixture.Readers.Opened.ToArray());
        CollectionAssert.AreEqual(new[] { "google-oauth-v1:" + first.ConnectionId.ToString("N") }, fixture.Vault.Deleted.ToArray());
        Assert.AreEqual(0, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Cleanup.Count);
    }

    [TestMethod]
    public async Task CanceledCleanupRetainsJournalAndRetriesAfterTheActiveReaderFinishes()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.HoldRead = true;
        var reading = ReadAsync(fixture);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(account.PermissionId, account.Revision, CancellationToken.None).ConfigureAwait(false);
        using var canceled = new CancellationTokenSource();
        var cleanup = fixture.Service.CleanCredentialsAsync(canceled.Token);
        try
        {
            await canceled.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(() => cleanup).ConfigureAwait(false);
            Assert.AreEqual(0, fixture.Vault.Deleted.Count);
            Assert.AreEqual(1, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Cleanup.Count);
        }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, await fixture.Service.CleanCredentialsAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FailedReaderDisposalKeepsBothOwnershipAndCleanupJournalForRetry()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        await ReadAsync(fixture).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(account.PermissionId, account.Revision, CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.FailDisposal = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Service.CleanCredentialsAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Vault.Deleted.Count);
        Assert.AreEqual(1, (await fixture.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Cleanup.Count);
        fixture.Readers.FailDisposal = false;
        Assert.AreEqual(1, await fixture.Service.CleanCredentialsAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(2, fixture.Readers.DisposalAttempts);
    }

    [TestMethod]
    public async Task ExitCancelsAndJoinsActiveReadAndItsActualDisposal()
    {
        await using var fixture = new Fixture();
        await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.HoldRead = true; fixture.Readers.HoldDisposal = true;
        var reading = ReadAsync(fixture);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var stopping = fixture.Service.DisposeAsync().AsTask();
        await fixture.Readers.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try { Assert.IsFalse(stopping.IsCompleted); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false);
        await fixture.Readers.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try { Assert.IsFalse(stopping.IsCompleted); }
        finally { fixture.Readers.ReleaseDisposal.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, fixture.Readers.DisposalAttempts);
        Assert.ThrowsExactly<ObjectDisposedException>(() => ReadAsync(fixture));
    }

    private static Task<long> ReadAsync(Fixture fixture) => fixture.Service.ReadDriveAsync("permission-A", async (reader, revision, token) =>
    {
        await reader.ReadMediaPageAsync(null, token).ConfigureAwait(false); return revision;
    }, CancellationToken.None);

    private sealed class LibraryFactory : IGoogleLibraryConnectionFactory
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public List<Guid> Opened { get; } = [];
        public bool HoldRead { get; set; }
        public bool HoldDisposal { get; set; }
        public bool FailDisposal { get; set; }
        public Func<string, CancellationToken, Task<DriveFolder>>? FolderRead { get; set; }
        public Func<string, string?, string?, CancellationToken, Task<DriveSourcePage>>? SourceRead { get; set; }
        public Func<string, string?, string, CancellationToken, Task<DriveSourceItem>>? RepairRead { get; set; }
        public Func<string, string, long, string, CancellationToken, Task<DriveWorkspaceObject>>? WorkspaceRead { get; set; }
        public Func<CancellationToken, Task<string>>? WorkspaceAllocate { get; set; }
        public Func<string, CancellationToken, Task<DriveWorkspaceAccess>>? WorkspaceInspect { get; set; }
        public Func<string, string, string, ReadOnlyMemory<byte>, string, CancellationToken, Task<DriveWorkspaceObject>>? WorkspaceCreate { get; set; }
        public int DisposalAttempts { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IGoogleLibraryConnection Create(Guid connectionId, string permissionId)
        {
            Assert.AreEqual("permission-A", permissionId); Opened.Add(connectionId); return new Connection(this);
        }
        private sealed class Connection(LibraryFactory owner) : IGoogleLibraryConnection, IGoogleWorkspacePublication, IGoogleWorkspaceSetup
        {
            public async Task<DriveMediaPage> ReadMediaPageAsync(string? pageToken, CancellationToken cancellationToken)
            {
                if (owner.HoldRead)
                {
                    owner.Started.TrySetResult();
                    using var registration = cancellationToken.Register(() => owner.Canceled.TrySetResult());
                    await owner.ReleaseRead.Task.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                owner.Events.Enqueue("refresh-saved"); return new([], null, false);
            }
            public Task<DriveMediaPreview?> ReadMediaPreviewAsync(string fileId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected preview request.");
            public Task<DriveFolder> ReadFolderAsync(string folderId, CancellationToken cancellationToken) => owner.FolderRead?.Invoke(folderId, cancellationToken) ?? throw new InvalidOperationException("Unexpected folder request.");
            public Task<DriveFolderPage> ReadFolderPageAsync(string parentId, string? pageToken, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected folder page request.");
            public Task<DriveSourcePage> ReadSourcePageAsync(string parentId, string? sharedDriveId, string? pageToken, CancellationToken cancellationToken) => owner.SourceRead?.Invoke(parentId, sharedDriveId, pageToken, cancellationToken) ?? throw new InvalidOperationException("Unexpected source request.");
            public Task<DriveSourceItem> ReadSourceItemAsync(string parentId, string? sharedDriveId, string fileId, CancellationToken cancellationToken) => owner.RepairRead?.Invoke(parentId, sharedDriveId, fileId, cancellationToken) ?? throw new InvalidOperationException("Unexpected source repair request.");
            public Task<DriveWorkspaceObject> ReadWorkspaceObjectAsync(string folderId, string fileId, long expectedBytes, string expectedSha256, CancellationToken cancellationToken) => owner.WorkspaceRead?.Invoke(folderId, fileId, expectedBytes, expectedSha256, cancellationToken) ?? throw new InvalidOperationException("Unexpected metadata workspace read.");
            public Task<string> AllocateWorkspaceObjectIdAsync(CancellationToken cancellationToken) => owner.WorkspaceAllocate?.Invoke(cancellationToken) ?? throw new InvalidOperationException("Unexpected metadata workspace allocation.");
            public Task<DriveWorkspaceAccess> InspectWorkspaceAsync(string folderId, CancellationToken cancellationToken) => owner.WorkspaceInspect?.Invoke(folderId, cancellationToken) ?? throw new InvalidOperationException("Unexpected metadata workspace inspection.");
            public Task<DriveWorkspaceObject> CreateWorkspaceObjectAsync(string folderId, string allocatedId, string mimeType, ReadOnlyMemory<byte> bytes, string sha256, CancellationToken cancellationToken) => owner.WorkspaceCreate?.Invoke(folderId, allocatedId, mimeType, bytes, sha256, cancellationToken) ?? throw new InvalidOperationException("Unexpected metadata workspace creation.");
            public async ValueTask DisposeAsync()
            {
                owner.DisposalAttempts++; owner.DisposalStarted.TrySetResult();
                if (owner.HoldDisposal) await owner.ReleaseDisposal.Task.ConfigureAwait(false);
                if (owner.FailDisposal) throw new IOException("Synthetic unsuccessful reader disposal");
                owner.Events.Enqueue("reader-disposed");
            }
        }
    }
}
