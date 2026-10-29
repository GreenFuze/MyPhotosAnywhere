using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task AutomaticRequestIsDurableBeforeNetworkAndRunsVerificationThenBoundedFolderWork()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false); var verifications = 0;
        fixture.Readers.FolderRead = (folder, _) => { verifications++; return Task.FromResult(new DriveFolder(folder, "Synthetic", true, [], null)); };
        fixture.Readers.SourceRead = (_, _, _, _) => Task.FromResult(new DriveSourcePage([Photo("healthy")], [], null, false));
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service);
        var request = await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(request.Created); Assert.AreEqual(0, verifications);
        var replay = await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(replay.Created); Assert.AreEqual(request.JobId, replay.JobId);
        var verified = await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("verify-source", verified!.Stage); Assert.IsTrue(verified.Complete); Assert.AreEqual(1, verifications);
        Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        var listed = await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("list-metadata", listed!.Stage); Assert.IsTrue(listed.Complete);
        Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        Assert.IsFalse((await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false)).Created);
        Assert.IsNull(await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FailedRootAuthorizationRemainsRetryableWithoutReschedulingOrLosingItsScope()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.FolderRead = (_, _) => throw new HttpRequestException("Synthetic denied root", null, HttpStatusCode.Forbidden);
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service);
        var request = await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        var failed = await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false); Assert.IsTrue(failed!.Failed); Assert.AreEqual("verify-source", failed.Stage);
        Assert.IsFalse((await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false)).Created);
        Assert.IsNull(await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false)); Assert.IsNull(runner.Failure);
        fixture.Readers.FolderRead = (id, _) => Task.FromResult(new DriveFolder(id, "Synthetic", true, [], "drive-A"));
        await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(request.JobId), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        await fixture.State.ExecuteAsync(database =>
        {
            var root = new SourceDiscoveryRepository(database).ClaimNext()!;
            Assert.AreEqual("folder-A", root.Folder.FolderId); Assert.AreEqual("drive-A", root.Folder.SharedDriveId);
            new DurableJobRepository(database).ReturnJoinedRead(root.Lease); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task SavedRootVerificationCompletesWithoutAnotherGoogleRequest()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service);
        await runner.EnsureScheduledAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(database =>
        {
            var repository = new SourceDiscoveryRepository(database); var accepted = repository.SaveVerifiedRoot(repository.ClaimVerification()!, "folder-A", "drive-A");
            new DurableJobRepository(database).ReturnJoinedRead(accepted.Lease); return true;
        }, CancellationToken.None).ConfigureAwait(false);
        fixture.Readers.FolderRead = (_, _) => throw new InvalidOperationException("A saved verification must not be replaced by a new network result.");
        var verified = await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("verify-source", verified!.Stage); Assert.IsTrue(verified.Complete);
    }
}
