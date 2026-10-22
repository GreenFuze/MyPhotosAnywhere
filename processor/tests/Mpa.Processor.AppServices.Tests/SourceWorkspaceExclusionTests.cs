using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    private static readonly string[] WorkspaceExpectedReadOrder = ["folder-A", "another-folder"];
    private static readonly string[] WorkspaceExpectedRepairOrder = ["failed-original"];
    [TestMethod]
    public async Task DiscoverySkipsPinnedWorkspaceIncludingDeniedMetadataWithoutHidingOrdinaryFolders()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false); await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false);
        var reads = new List<string>();
        fixture.Readers.SourceRead = (folder, _, _, _) =>
        {
            reads.Add(folder); Assert.AreNotEqual("metadata-workspace", folder);
            var metadata = Child("metadata-workspace"); metadata = metadata with { Folder = metadata.Folder! with { CanListChildren = false } };
            return Task.FromResult(folder == "folder-A" ? new DriveSourcePage([Photo("ordinary"), metadata, Child("another-folder")], [new("metadata-workspace", 1, "invalid-source-metadata")], null, false) : new([Photo("nested-original")], [], null, false));
        };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        while (await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false) is not null) { }
        CollectionAssert.AreEqual(WorkspaceExpectedReadOrder, reads);
        Assert.AreEqual(2, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count); Assert.AreEqual(0, (await SummaryAsync(fixture, job).ConfigureAwait(false)).Failed);
    }

    [TestMethod]
    public async Task WorkspaceQueuedBeforeSetupIsExcludedWithoutReadingIt()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = (folder, _, _, _) =>
        { Assert.AreEqual("folder-A", folder); return Task.FromResult(new DriveSourcePage([Child("metadata-workspace")], [], null, false)); };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false);
        Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        Assert.IsNull(await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count); Assert.AreEqual(0, (await SummaryAsync(fixture, job).ConfigureAwait(false)).Failed);
    }

    [TestMethod]
    public async Task WorkspacePinnedDuringActualReadCannotAdmitItsLateGeneratedPreview()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false); var metadataReads = 0;
        fixture.Readers.SourceRead = async (folder, _, _, _) =>
        {
            if (folder == "folder-A") return new DriveSourcePage([Child("metadata-workspace")], [], null, false);
            Assert.AreEqual("metadata-workspace", folder); metadataReads++;
            await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false); return new([Photo("generated-preview")], [], null, false);
        };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false); Assert.IsTrue((await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false))!.Complete);
        Assert.AreEqual(1, metadataReads); Assert.AreEqual(0, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        Assert.IsNull(await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false)); Assert.IsNull(runner.Failure);
    }

    [TestMethod]
    public async Task ExcludedWorkspaceRepairDoesNotDiscardOrRescanOtherFailedOriginals()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false); var repaired = new List<string>();
        fixture.Readers.SourceRead = (_, _, _, _) => Task.FromResult(new DriveSourcePage([], [new("metadata-workspace", 1, "invalid-source-metadata"), new("failed-original", 2, "invalid-source-metadata")], null, false));
        fixture.Readers.RepairRead = (_, _, file, _) =>
        { repaired.Add(file); Assert.AreEqual("failed-original", file); return Task.FromResult(Photo(file)); };
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false); await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false);
        Assert.AreEqual(2, await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(job), CancellationToken.None).ConfigureAwait(false));
        while (await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false) is not null) { }
        CollectionAssert.AreEqual(WorkspaceExpectedRepairOrder, repaired); Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count);
        Assert.AreEqual(0, (await SummaryAsync(fixture, job).ConfigureAwait(false)).Failed);
    }

    [TestMethod]
    public async Task UnexpectedPriorPhotoObservationsFailInsteadOfBecomingAnExcludedEmptyFolder()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = (folder, _, _, _) => Task.FromResult(folder == "folder-A" ? new DriveSourcePage([Child("metadata-workspace")], [], null, false) : new([Photo("retained-original")], [], "more", false));
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false); await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false);
        await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false); // Deliberately bypasses setup's fresh-empty gate to test the invariant.
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.RunNextAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count); Assert.IsNotNull(runner.Failure);
    }

    [TestMethod]
    public async Task WorkspaceRepairWithPriorOriginalsIsPreservedForReconciliation()
    {
        await using var fixture = new Fixture(); var source = await SourceAsync(fixture).ConfigureAwait(false);
        fixture.Readers.SourceRead = (folder, _, _, _) => Task.FromResult(folder == "folder-A" ? new DriveSourcePage([Child("metadata-workspace")], [], null, false) : new([Photo("retained-original")], [new("failed-original", 1, "invalid-source-metadata")], null, false));
        await using var runner = new SourceDiscoveryRunner(fixture.State, fixture.Service); var job = await runner.ScheduleAsync(source, 1, CancellationToken.None).ConfigureAwait(false);
        await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false); await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false); await PinDiscoveryWorkspaceAsync(fixture).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(database => new DurableJobRepository(database).RetryFailed(job), CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.RunNextAsync(CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(1, (await CatalogAsync(fixture).ConfigureAwait(false)).Assets.Count); Assert.IsNotNull(runner.Failure);
    }

    private static Task<LibraryControllerPin> PinDiscoveryWorkspaceAsync(Fixture fixture) => fixture.State.ExecuteAsync(database =>
    {
        _ = new ProcessorLibraryRepository(database).GetOrCreateIdentity(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new LibraryAuthorityRepository(database, new(new DiscoveryWorkspaceSpace())).Bootstrap(Guid.NewGuid(), 1, new("permission-A", "metadata-workspace"), 1, key.ExportSubjectPublicKeyInfo());
    }, CancellationToken.None);
    private sealed class DiscoveryWorkspaceSpace : IStorageSpaceReader
    { public StorageSpaceSnapshot Read() => new(100 * StorageBudgetManager.Gibibyte, 20 * StorageBudgetManager.Gibibyte, 0); }
}
