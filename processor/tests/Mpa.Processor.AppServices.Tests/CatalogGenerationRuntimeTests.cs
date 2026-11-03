using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task EngineGenerationRuntimePublishesOnceAndUnchangedInputsStillRequireActualCloudRead()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); var reads = ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        await using var current = CatalogReconciler(fixture);
        await using var runtime = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var head = await runtime.RunUntilCurrentAsync(caller.Token).ConfigureAwait(false);
        Assert.AreEqual(1L, head.Sequence); Assert.AreEqual(1L, head.Assets); await AssertRetiredCompletionAsync(fixture, head).ConfigureAwait(false);
        var before = (fixture.Creates, fixture.Allocations, Reads: reads.Count);
        Assert.AreEqual(head, await runtime.RunUntilCurrentAsync(caller.Token).ConfigureAwait(false));
        Assert.AreEqual(before.Creates, fixture.Creates); Assert.AreEqual(before.Allocations, fixture.Allocations); Assert.IsTrue(reads.Count > before.Reads);
        fixture.Setup.Accounts.Readers.MetadataInventory = (_, _, _, _) => throw new HttpRequestException("Synthetic actual workspace outage after unchanged inputs.");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => runtime.RunUntilCurrentAsync(caller.Token)).ConfigureAwait(false);
        Assert.IsNull(current.Current); Assert.AreEqual(before.Allocations, fixture.Allocations);
        await runtime.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(runtime.ExitConfirmed); Assert.IsFalse(current.ExitConfirmed);
    }

    [TestMethod]
    public async Task EngineSetupOwnsActualPublicationAndSourceRequestsAfterBrowserCancellation()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        var current = CatalogReconciler(fixture); var generations = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false);
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, current, generations);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            if (!held && mime == "application/json" && System.Text.Encoding.UTF8.GetString(bytes.Span).Contains("\"kind\":\"catalog-commit\"", StringComparison.Ordinal))
            { held = true; entered.TrySetResult(); await release.Task.ConfigureAwait(false); }
            return result;
        };
        using var browser = new CancellationTokenSource(); runtime.Request(browser.Token); await browser.CancelAsync().ConfigureAwait(false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false);
            runtime.NotifyLocalCatalogChanged(); runtime.NotifyLocalCatalogChanged();
        }
        finally { release.TrySetResult(); }
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        Assert.IsNull(runtime.LastAttemptFailure); Assert.IsNull(runtime.Failure); Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished, runtime.Activity.Stage);
        Assert.AreEqual(2L, runtime.CurrentCatalog!.Sequence); Assert.AreEqual(2L, runtime.CurrentCatalog.Assets);
        await runtime.DisposeAsync().ConfigureAwait(false); Assert.IsTrue(runtime.ExitConfirmed); Assert.IsTrue(generations.ExitConfirmed); Assert.IsTrue(current.ExitConfirmed);
    }

    [TestMethod]
    public async Task ActualCoverageOnlySourceStepWakesAnOwnedGenerationWithoutAChangedPhotoRevision()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        fixture.Setup.Accounts.Readers.FolderRead = (folder, token) =>
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new Mpa.Processor.GoogleDrive.DriveFolder(folder, "Synthetic source", true, [], null)); };
        fixture.Setup.Accounts.Readers.SourceRead = (_, _, _, token) =>
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new Mpa.Processor.GoogleDrive.DriveSourcePage([], [], null, false)); };
        var current = CatalogReconciler(fixture); var generations = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false);
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, current, generations);
        runtime.Request(CancellationToken.None);
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        Assert.IsNull(runtime.LastAttemptFailure); Assert.AreEqual(1L, runtime.CurrentCatalog!.Sequence);
        var source = await fixture.Setup.Accounts.State.ExecuteAsync(database => new LibrarySourceRepository(database).Snapshot().Sources.Single(), CancellationToken.None).ConfigureAwait(false);
        var before = await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationInputReader(database, fixture.Budget).Current(CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        await using var runner = new SourceDiscoveryRunner(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service, fixture.Budget);
        runner.CatalogChanged += runtime.NotifyLocalCatalogChanged;
        await runner.ScheduleAsync(source.Id, source.Revision, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(await runner.RunNextAsync(CancellationToken.None).ConfigureAwait(false));
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        Assert.IsNull(runtime.LastAttemptFailure); Assert.AreEqual(2L, runtime.CurrentCatalog!.Sequence); Assert.AreEqual(0L, runtime.CurrentCatalog.Assets);
        var after = await fixture.Setup.Accounts.State.ExecuteAsync(database => new CatalogGenerationInputReader(database, fixture.Budget).Current(CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before.DiscoveryRevision, after.DiscoveryRevision); Assert.AreNotEqual(before.Sha256, after.Sha256);
        runner.CatalogChanged -= runtime.NotifyLocalCatalogChanged;
    }

    [TestMethod]
    public async Task EngineExitJoinsGenerationBorrowersBeforeItsCurrentReconcilerAndNativeKeys()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        var current = CatalogReconciler(fixture); var generations = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false);
        await using var runtime = new InitialLibraryPublicationRuntime(fixture.Setup.Accounts.State, fixture.Setup.Accounts.Service,
            fixture.Setup.Keys, fixture.Budget, current, generations);
        var read = fixture.Setup.Accounts.Readers.WorkspaceRead!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Setup.Accounts.Readers.WorkspaceRead = async (folder, file, bytes, hash, token) =>
        { var actual = await read(folder, file, bytes, hash, token).ConfigureAwait(false); entered.TrySetResult(); await release.Task.ConfigureAwait(false); return actual; };
        runtime.Request(CancellationToken.None); Task stopping;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); stopping = runtime.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted); Assert.IsFalse(runtime.ExitConfirmed); Assert.IsFalse(generations.ExitConfirmed); Assert.IsFalse(current.ExitConfirmed);
            Assert.IsNotNull(fixture.Setup.Keys.PublicIdentity);
        }
        finally { release.TrySetResult(); }
        await stopping.ConfigureAwait(false); Assert.IsTrue(runtime.ExitConfirmed); Assert.IsTrue(generations.ExitConfirmed); Assert.IsTrue(current.ExitConfirmed);
        Assert.AreEqual(InitialLibraryPublicationStage.Stopped, runtime.Activity.Stage);
    }

    [TestMethod]
    public async Task ANewEngineGenerationOwnerResumesPendingFrozenWorkBeforeCapturingLaterDiscovery()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!; var failed = false;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            if (!failed && mime == "application/gzip") { failed = true; throw new HttpRequestException("Synthetic interrupted engine publication."); }
            return result;
        };
        await using (var current = CatalogReconciler(fixture))
        await using (var runtime = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false))
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => runtime.RunUntilCurrentAsync(CancellationToken.None)).ConfigureAwait(false);
        var retained = await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var capture = new CatalogGenerationCaptureRepository(database, fixture.Budget).Active().Single();
            var outbox = new WorkspacePublicationRepository(database, fixture.Budget); return (Capture: capture, Pending: outbox.Read(outbox.Pending().Single())!);
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("publication-planned", retained.Capture.Phase); Assert.IsNotNull(retained.Pending.FileId);
        await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false); fixture.Setup.Accounts.Readers.WorkspaceCreate = create;
        await using var resumedCurrent = CatalogReconciler(fixture);
        await using var resumed = await GenerationRuntimeAsync(fixture, resumedCurrent).ConfigureAwait(false);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var head = await resumed.RunUntilCurrentAsync(caller.Token).ConfigureAwait(false);
        Assert.AreEqual(2L, head.Sequence); Assert.AreEqual(2L, head.Assets); Assert.AreNotEqual(retained.Capture.Scope.GenerationId, head.GenerationId);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var journal = new CatalogGenerationCaptureRepository(database, fixture.Budget); var original = journal.Read(retained.Capture.Scope.GenerationId)!;
            Assert.AreEqual("retired", original.Phase); Assert.AreEqual(retained.Capture.Scope, original.Scope); Assert.AreEqual(retained.Capture.SnapshotId, original.SnapshotId);
            Assert.AreEqual(1L, journal.Verification(original.Scope.GenerationId)!.Assets);
            var work = new WorkspacePublicationRepository(database, fixture.Budget).Read(retained.Pending.Id)!;
            Assert.AreEqual(retained.Pending.FileId, work.FileId); Assert.IsNotNull(work.VerifiedVersion); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ImportDuringTheFirstActualCommitFinishesItsFrozenHeadThenPublishesTheNextObservation()
    {
        await using var fixture = await InitialPublicationFixture.CreateAsync(addSource: true).ConfigureAwait(false);
        await fixture.Owner.PublishAsync(CancellationToken.None).ConfigureAwait(false); ConfigureGenerationWorkspace(fixture);
        await AddPreparationPhotoAsync(fixture, "original-photo").ConfigureAwait(false);
        var create = fixture.Setup.Accounts.Readers.WorkspaceCreate!; var imported = false;
        fixture.Setup.Accounts.Readers.WorkspaceCreate = async (folder, file, mime, bytes, hash, token) =>
        {
            var result = await create(folder, file, mime, bytes, hash, token).ConfigureAwait(false);
            if (!imported && mime == "application/json" && System.Text.Encoding.UTF8.GetString(bytes.Span).Contains("catalog-commit", StringComparison.Ordinal))
            { imported = true; await AddPreparationPhotoAsync(fixture, "later-photo").ConfigureAwait(false); }
            return result;
        };
        await using var current = CatalogReconciler(fixture);
        await using var runtime = await GenerationRuntimeAsync(fixture, current).ConfigureAwait(false);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var head = await runtime.RunUntilCurrentAsync(caller.Token).ConfigureAwait(false);
        Assert.IsTrue(imported); Assert.AreEqual(2L, head.Sequence); Assert.AreEqual(2L, head.Assets);
        await AssertRetiredCompletionAsync(fixture, head).ConfigureAwait(false);
        await fixture.Setup.Accounts.State.ExecuteAsync(database =>
        {
            var inputs = new CatalogGenerationInputReader(database, fixture.Budget); Assert.AreEqual(inputs.Current(CancellationToken.None), inputs.Published(CancellationToken.None));
            Assert.AreEqual("permission-A", head.Commit.AccountId);
            var inventory = new ProtocolSigningJournal(database, fixture.Budget).Inventory(fixture.Setup.Keys.PublicIdentity.LibraryId);
            Assert.AreEqual(2, inventory.Intents.Count(item => item.Kind == "catalog-commit")); return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<CatalogGenerationRuntime> GenerationRuntimeAsync(InitialPublicationFixture fixture, LocalCatalogReconciliationService current)
    {
        var context = await fixture.Setup.Accounts.State.ExecuteAsync(database => (database.StateDirectory, database.InstallationId), CancellationToken.None).ConfigureAwait(false);
        return new(fixture.Setup.Accounts.State, context.StateDirectory, context.InstallationId, fixture.Budget, current, fixture.Setup.Keys);
    }
}
