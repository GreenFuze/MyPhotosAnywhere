using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task PublicationJournalsItsIdBeforeCreationAndLostReplyRetriesOnlyThatObject()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var authority = new PublicationAuthority();
        var work = await PreparePublication(fixture, budget).ConfigureAwait(false); var allocations = 0; var creates = new List<string>();
        fixture.Readers.WorkspaceAllocate = _ => { allocations++; return Task.FromResult("allocated-object"); };
        fixture.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var durable = await fixture.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, budget).Read(work.Id)!, token).ConfigureAwait(false);
            Assert.AreEqual(file, durable.FileId); Assert.AreEqual("allocated-object", file); Assert.AreEqual(work.Scope.FolderId, folder);
            Assert.AreEqual(work.MimeType, mime); Assert.AreEqual(work.Sha256, hash); CollectionAssert.AreEqual(work.Payload.ToArray(), bytes.ToArray());
            creates.Add(file); if (creates.Count == 1) throw new HttpRequestException("Synthetic lost completion reply");
            return new(work.Scope.AccountId, file, folder, 7, bytes);
        };
        var uploader = new WorkspacePublicationUploader(fixture.State, fixture.Service, budget, authority);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => uploader.UploadAsync(work.Id, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await PublicationRead(fixture, budget, work.Id).ConfigureAwait(false)).VerifiedVersion);
        var resumed = await new WorkspacePublicationUploader(fixture.State, fixture.Service, budget, authority).UploadAsync(work.Id, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(7L, resumed.VerifiedVersion); Assert.AreEqual(1, allocations); Assert.AreEqual(2, creates.Count); Assert.AreEqual(1, fixture.Readers.Opened.Count);
        var repeated = await uploader.UploadAsync(work.Id, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(7L, repeated.VerifiedVersion); Assert.AreEqual(2, creates.Count);
    }

    [TestMethod]
    public async Task ConcurrentCallersShareOneAllocationAndNeverDuplicateTheUpload()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var authority = new PublicationAuthority();
        var work = await PreparePublication(fixture, budget).ConfigureAwait(false); var allocations = 0; var creates = 0;
        fixture.Readers.WorkspaceAllocate = _ => { allocations++; return Task.FromResult("single-object"); };
        fixture.Readers.WorkspaceCreate = async (folder, file, _, bytes, _, _) =>
        {
            creates++; fixture.Readers.Started.TrySetResult(); await fixture.Readers.ReleaseRead.Task.ConfigureAwait(false);
            return new(work.Scope.AccountId, file, folder, 7, bytes);
        };
        var uploader = new WorkspacePublicationUploader(fixture.State, fixture.Service, budget, authority);
        var first = uploader.UploadAsync(work.Id, CancellationToken.None);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var second = uploader.UploadAsync(work.Id, CancellationToken.None);
        try { Assert.IsFalse(first.IsCompleted); Assert.IsFalse(second.IsCompleted); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(1, allocations); Assert.AreEqual(1, creates); Assert.IsTrue(results.All(result => result.VerifiedVersion == 7));
    }

    [TestMethod]
    public async Task MissingAuthorityAndStaleConfigurationFailBeforeAnyAccountOrCreateWork()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var authority = new PublicationAuthority { Allowed = false };
        var work = await PreparePublication(fixture, budget).ConfigureAwait(false);
        var uploader = new WorkspacePublicationUploader(fixture.State, fixture.Service, budget, authority);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => uploader.UploadAsync(work.Id, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Readers.Opened.Count);
        authority.Allowed = true;
        await fixture.Service.ConnectAsync(work.Scope.AccountId, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => uploader.UploadAsync(work.Id, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.Readers.Opened.Count); Assert.IsNull((await PublicationRead(fixture, budget, work.Id).ConfigureAwait(false)).FileId);
    }

    [TestMethod]
    public async Task LateAuthorityRevocationOrChangedReturnedBytesNeverAcknowledgesPublication()
    {
        foreach (var revoke in new[] { false, true })
        {
            await using var fixture = new Fixture(); var budget = PublicationBudget(); var authority = new PublicationAuthority();
            var work = await PreparePublication(fixture, budget).ConfigureAwait(false);
            fixture.Readers.WorkspaceAllocate = _ => Task.FromResult("allocated-object");
            fixture.Readers.WorkspaceCreate = (folder, file, _, bytes, _, _) =>
            {
                if (revoke) authority.Allowed = false;
                return Task.FromResult(new DriveWorkspaceObject(work.Scope.AccountId, file, folder, 7, revoke ? bytes : "xx"u8.ToArray()));
            };
            var uploader = new WorkspacePublicationUploader(fixture.State, fixture.Service, budget, authority);
            if (revoke) await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => uploader.UploadAsync(work.Id, CancellationToken.None)).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => uploader.UploadAsync(work.Id, CancellationToken.None)).ConfigureAwait(false);
            var retained = await PublicationRead(fixture, budget, work.Id).ConfigureAwait(false);
            Assert.IsNull(retained.VerifiedVersion); Assert.AreEqual("allocated-object", retained.FileId);
        }
    }

    [TestMethod]
    public async Task DisconnectDuringUploadRetainsRetryAndJoinsResponseBeforeCredentialCleanup()
    {
        await using var fixture = new Fixture(); var budget = PublicationBudget(); var work = await PreparePublication(fixture, budget).ConfigureAwait(false);
        HoldPublication(fixture, work); var uploader = new WorkspacePublicationUploader(fixture.State, fixture.Service, budget, new PublicationAuthority());
        var uploading = uploader.UploadAsync(work.Id, CancellationToken.None);
        await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await fixture.Service.DisconnectAsync(work.Scope.AccountId, work.Scope.AccountRevision, CancellationToken.None).ConfigureAwait(false);
        var cleanup = fixture.Service.CleanCredentialsAsync(CancellationToken.None);
        try { Assert.IsFalse(cleanup.IsCompleted); Assert.AreEqual(0, fixture.Vault.Deleted.Count); }
        finally { fixture.Readers.ReleaseRead.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(() => uploading).ConfigureAwait(false);
        Assert.AreEqual(1, await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        Assert.IsNull((await PublicationRead(fixture, budget, work.Id).ConfigureAwait(false)).VerifiedVersion);
    }

    [TestMethod]
    public async Task CallerCancellationAndShutdownJoinLateUploadWithoutAcceptingItsReceipt()
    {
        foreach (var exit in new[] { false, true })
        {
            await using var fixture = new Fixture(); var budget = PublicationBudget(); var work = await PreparePublication(fixture, budget).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource(); HoldPublication(fixture, work);
            var uploader = new WorkspacePublicationUploader(fixture.State, fixture.Service, budget, new PublicationAuthority());
            var uploading = uploader.UploadAsync(work.Id, cancellation.Token);
            await fixture.Readers.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Task disposal = Task.CompletedTask;
            if (exit) disposal = fixture.Service.DisposeAsync().AsTask(); else await cancellation.CancelAsync().ConfigureAwait(false);
            await fixture.Readers.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try { Assert.IsFalse(uploading.IsCompleted); if (exit) Assert.IsFalse(disposal.IsCompleted); }
            finally { fixture.Readers.ReleaseRead.TrySetResult(); }
            await Assert.ThrowsAsync<OperationCanceledException>(() => uploading).ConfigureAwait(false); await disposal.ConfigureAwait(false);
            var retained = await PublicationRead(fixture, budget, work.Id).ConfigureAwait(false);
            Assert.AreEqual("allocated-object", retained.FileId); Assert.IsNull(retained.VerifiedVersion);
        }
    }

    private static void HoldPublication(Fixture fixture, WorkspacePublicationRecord work)
    {
        fixture.Readers.WorkspaceAllocate = _ => Task.FromResult("allocated-object");
        fixture.Readers.WorkspaceCreate = async (folder, file, _, bytes, _, token) =>
        {
            fixture.Readers.Started.TrySetResult(); using var registration = token.Register(() => fixture.Readers.Canceled.TrySetResult());
            await fixture.Readers.ReleaseRead.Task.ConfigureAwait(false); // The real owner must join even when a transport ignores cancellation.
            return new(work.Scope.AccountId, file, folder, 7, bytes);
        };
    }
    private static async Task<WorkspacePublicationRecord> PreparePublication(Fixture fixture, StorageBudgetManager budget)
    {
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        return await fixture.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, budget).Prepare(Guid.NewGuid(),
            new(new ProcessorLibraryRepository(database).GetOrCreateIdentity(), Guid.NewGuid(), Guid.NewGuid(), account.PermissionId, account.Revision, "workspace-A", 1, new string('a', 64)),
            "facts-index", "application/json", "{}"u8), CancellationToken.None).ConfigureAwait(false);
    }
    private static Task<WorkspacePublicationRecord> PublicationRead(Fixture fixture, StorageBudgetManager budget, Guid id) =>
        fixture.State.ExecuteAsync(database => new WorkspacePublicationRepository(database, budget).Read(id)!, CancellationToken.None);
    private static StorageBudgetManager PublicationBudget() => new(new PublicationSpace());
    private sealed class PublicationSpace : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
    private sealed class PublicationAuthority : IWorkspacePublicationAuthority
    {
        public bool Allowed { get; set; } = true;
        public Task ValidateAsync(WorkspacePublicationScope scope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual("workspace-A", scope.FolderId);
            if (!Allowed) throw new UnauthorizedAccessException("Synthetic revoked publisher authority"); return Task.CompletedTask;
        }
    }
}
