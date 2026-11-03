using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly string?[] FailedPageSequence = [null, "page-two", "page-two"];

    [TestMethod]
    public async Task DiscoveryContinuesHealthyFoldersAndRetryReadsOnlyTheFailedFile()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var pages = 0; var repaired = 0;
        fixture.Readers.SourceRead = (folder, _, cursor, _) =>
        {
            pages++;
            return Task.FromResult(folder == "child-B" ? new DriveSourcePage([Photo("child-photo")], [], null, false) :
                cursor is null ? new DriveSourcePage([Photo("photo-A"), Child("child-B"), new("sidecar", 1, "photo.json", "application/json", 42, null, null)],
                    [new("bad-photo", 1, "invalid-source-metadata")], "page-two", false) : new([Photo("photo-C")], [], null, false));
        };
        fixture.Readers.RepairRead = (folder, _, file, _) =>
        { Assert.AreEqual("folder-A", folder); Assert.AreEqual("bad-photo", file); repaired++; return Task.FromResult(Photo(file)); };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service);
        var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        for (var step = 0; step < 5; step++) if (await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false) is null) break;
        var summary = await SummaryAsync(fixture, job).ConfigureAwait(false);
        Assert.AreEqual(2, summary.Ready); Assert.AreEqual(1, summary.Failed); Assert.AreEqual(3, pages); Assert.AreEqual(0, repaired);
        var catalog = await CatalogAsync(fixture).ConfigureAwait(false); Assert.AreEqual(3, catalog.Assets.Count);
        Assert.IsTrue(catalog.Assets.All(asset => asset.Facts.CapturedUtc is null && asset.DateProvenance == CatalogDateProvenance.DriveModification));
        Assert.AreEqual(1, await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(job), CancellationToken.None).ConfigureAwait(false));
        var repair = await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(repair!.Repair); Assert.IsTrue(repair.Complete); Assert.IsFalse(repair.Failed);
        Assert.AreEqual(3, pages); Assert.AreEqual(1, repaired); Assert.AreEqual(4, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        Assert.IsNull(await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false)); Assert.IsNull(runner.Failure);
    }

    [TestMethod]
    public async Task AuthorizationFailureRetainsHealthyContentAndRetriesOnlyTheUnfinishedPage()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var cursors = new List<string?>(); var fail = true;
        fixture.Readers.SourceRead = (_, _, cursor, _) =>
        {
            cursors.Add(cursor);
            if (cursor is not null && fail) throw new HttpRequestException("Synthetic expired access", null, HttpStatusCode.Unauthorized);
            return Task.FromResult(cursor is null ? new DriveSourcePage([Photo("healthy")], [], "page-two", false) : new([Photo("later")], [], null, false));
        };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Failed);
        Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count); Assert.IsNull(runner.Failure);
        fail = false; Assert.AreEqual(1, await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(job), CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        CollectionAssert.AreEqual(FailedPageSequence, cursors.ToArray()); Assert.AreEqual(2, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
    }

    [TestMethod]
    public async Task PauseDuringAReadJoinsItAndKeepsItsCheckpointBeforeResume()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Readers.SourceRead = async (_, _, _, _) => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return new([Photo("healthy")], [], null, false); };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        var reading = runner.RunNextAsync(CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            var leases = await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).Pause(job), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, leases.Count); Assert.IsFalse(reading.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.IsFalse((await reading.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))!.Complete);
        var paused = await SummaryAsync(fixture, job).ConfigureAwait(false); Assert.IsTrue(paused.Paused); Assert.AreEqual(0, paused.Running); Assert.AreEqual(1, paused.Queued);
        Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        await fixture.State.ExecuteAsync(database => { new DurableJobRepository(database).Resume(job); return true; }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
    }

    [TestMethod]
    public async Task ExitWaitsForTheCanceledReadBeforeReturningItsDurableLease()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Readers.SourceRead = async (_, _, _, token) =>
        {
            entered.TrySetResult(); using var registration = token.Register(() => canceled.TrySetResult());
            await release.Task.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return new([], [], null, false);
        };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        var reading = runner.RunNextAsync(CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var exit = runner.DisposeAsync().AsTask();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); Assert.IsFalse(exit.IsCompleted);
            Assert.AreEqual(1, (await SummaryAsync(fixture, job).ConfigureAwait(false)).Running);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading).ConfigureAwait(false); await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, (await SummaryAsync(fixture, job).ConfigureAwait(false)).Queued);
        Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
    }

    [TestMethod]
    public async Task SourceChangesDuringNetworkReadCannotPublishIntoTheOldScope()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = async (_, _, _, _) =>
        {
            await fixture.State.ExecuteAsync(database =>
            {
                var sources = new LibrarySourceRepository(database); sources.Update(source, false, true, 1, sources.Snapshot().Revision); return true;
            }, CancellationToken.None).ConfigureAwait(false);
            return new([Photo("late-result")], [], null, false);
        };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Failed);
        Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        Assert.AreEqual(0, await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(job), CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task IncompleteSearchKeepsUsableFactsAndAnExplicitRetryablePageObservation()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false); var incomplete = true;
        fixture.Readers.SourceRead = (_, _, _, _) => Task.FromResult(new DriveSourcePage([Photo("healthy")], [], null, incomplete));
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        var summary = await SummaryAsync(fixture, job).ConfigureAwait(false); Assert.AreEqual(1, summary.Ready); Assert.AreEqual(1, summary.Failed);
        Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        incomplete = false; await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(job), CancellationToken.None).ConfigureAwait(false);
        var repaired = await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false); Assert.IsTrue(repaired!.Repair && repaired.Complete);
        Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
    }

    [TestMethod]
    public async Task InternalIdentityConflictFailsFastAndCannotMasqueradeAsAnEmptyFolder()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = (_, _, _, _) => Task.FromResult(new DriveSourcePage([Photo("same"), Photo("same")], [], null, false));
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => runner.RunNextAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNotNull(runner.Failure); Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunNextAsync(CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TerminalCheckpointResumesCompletionWithoutReadingTheFolderAgain()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(database =>
        {
            var repository = new SourceDiscoveryRepository(database); var work = repository.ClaimNext()!;
            var terminal = repository.Advance(work, null, 0, 0, false);
            new DurableJobRepository(database).ReturnJoinedRead(terminal.Lease); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        // No SourceRead handler exists. Any re-read fails rather than quietly passing.
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        Assert.AreEqual(1, (await SummaryAsync(fixture, job).ConfigureAwait(false)).Ready);
    }

    [TestMethod]
    public async Task RetryingAnInaccessibleChildAdmitsItsOwnFolderWithoutRescanningSuccessfulParent()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false); var parentReads = 0;
        fixture.Readers.SourceRead = (folder, _, _, _) =>
        {
            if (folder == "folder-A")
            {
                parentReads++; var child = Child("child-B");
                return Task.FromResult(new DriveSourcePage([child with { Folder = child.Folder! with { CanListChildren = false } }], [], null, false));
            }
            Assert.AreEqual("child-B", folder); return Task.FromResult(new DriveSourcePage([Photo("nested-photo")], [], null, false));
        };
        fixture.Readers.RepairRead = (_, _, file, _) => Task.FromResult(Child(file));
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, (await SummaryAsync(fixture, job).ConfigureAwait(false)).Failed);
        await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(job), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Repair);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        Assert.AreEqual(1, parentReads); Assert.AreEqual("nested-photo", (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Single().Facts.FileId);
    }

    private static async Task<Guid> SourceAsync(Fixture fixture)
    {
        var account = await fixture.Service.ConnectAsync(null, new NoBrowser(), CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.FolderRead = (id, _) => Task.FromResult(new DriveFolder(id, "Synthetic folder", true, [], null));
        var source = Guid.NewGuid();
        await fixture.State.ExecuteAsync(database => new LibrarySourceRepository(database).Add(source, account.PermissionId, account.Revision, "folder-A", "Synthetic source", true, 1), CancellationToken.None).ConfigureAwait(false);
        return source;
    }
    private static Task<JobSummary> SummaryAsync(Fixture fixture, Guid job) => fixture.State.ExecuteAsync(database => new DurableJobRepository(database).Summary(job), CancellationToken.None);
    private static Task<CatalogPage> CatalogAsync(Fixture fixture) => fixture.State.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ReadPage(), CancellationToken.None);
    private static DriveSourceItem Photo(string id) => new(id, 1, id + ".jpg", "image/jpeg", 123, null,
        new(id, id + ".jpg", "image/jpeg", 400, 300, "2026:10:20 15:30:00", "2026-10-20T15:30:00Z"));
    private static DriveSourceItem Child(string id) => new(id, 1, "Nested folder", "application/vnd.google-apps.folder", null, new(id, "Nested folder", true, [], null), null);
}
