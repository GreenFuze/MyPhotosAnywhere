using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentDesktopCompositionOwnsBackgroundPublicationBeyondBrowserCancellationAndReopensActualHistoricalPhotos()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);
        var first=await NewCurrentDesktopCompositionAsync(fixture).ConfigureAwait(false);await using var runtime=first.Runtime;
        Assert.IsNull(await runtime.InitializeCurrentCatalogAsync(CancellationToken.None).ConfigureAwait(false));
        using(var browser=new CancellationTokenSource())
        {
            Assert.AreEqual(InitialLibraryPublicationStage.Running,runtime.Request(browser.Token).Stage);
            await browser.CancelAsync().ConfigureAwait(false);
        }
        await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished,runtime.Activity.Stage);Assert.IsNull(runtime.LastAttemptFailure);Assert.IsNull(runtime.Failure);
        Assert.AreSame(first.Catalog.Catalog,runtime.CurrentCanonicalCatalog);Assert.IsNull(runtime.CurrentCatalog);
        var head=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new CatalogGenerationCaptureRepository(database,fixture.Data.Budget).PublishedHead()!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Generation,head.GenerationId);Assert.AreEqual(1L,head.Assets);Assert.AreEqual(1,fixture.Http.Puts);Assert.IsFalse(File.Exists(owned.Path));
        var selected=first.Catalog.Catalog.Selection;Assert.IsNotNull(selected);
        var page=await first.Catalog.Catalog.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);Assert.AreEqual(1,page.Page.Assets.Count);
        await runtime.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(runtime.ExitConfirmed);Assert.IsTrue(first.Catalog.ExitConfirmed);Assert.IsTrue(first.Catalog.Catalog.ExitConfirmed);
        var accounts=await fixture.Data.Setup.Accounts.Service.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);Assert.IsTrue(accounts.Accounts.Single().Connected);
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        var reopened=await NewCurrentDesktopCompositionAsync(fixture).ConfigureAwait(false);await using var second=reopened.Runtime;
        Assert.AreEqual(selected,await second.InitializeCurrentCatalogAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));
        var historical=await reopened.Catalog.Catalog.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(historical);Assert.AreEqual(page.Page.Assets.Single().Id,historical.Page.Assets.Single().Id);
        second.Request(CancellationToken.None);await second.WaitForCurrentAttemptAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished,second.Activity.Stage);Assert.IsNull(second.Failure);
        Assert.AreNotEqual(selected.FencedAttempt,reopened.Catalog.Catalog.Selection!.FencedAttempt);Assert.AreEqual(head.Commit,reopened.Catalog.Catalog.Selection.Catalog);
        Assert.AreEqual((calls.Creates,calls.Allocations,calls.Puts),(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDesktopCompositionColdApprovalPublishesBothPoliciesThenOneFixedRootBeforeTheFirstRealCatalog()
    {
        await using var fixture=await CurrentReaderFixture.CreateBeforeInitialPoliciesAsync(addSource:true).ConfigureAwait(false);
        ConfigureCurrentPublication(fixture.Data);InstallCurrentInventoryTransport(fixture);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,payload,hash,token)=>
        {
            if(mime=="application/gzip")
            {
                // The old initial-policy fake deliberately accepts JSON only.
                // Model this additional real facts-chunk endpoint separately;
                // retain every old transport assertion for initial JSON writes.
                token.ThrowIfCancellationRequested();
                var captured=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
                {
                    var outbox=new WorkspacePublicationRepository(database,fixture.Data.Budget);
                    return outbox.Identities().Select(id=>outbox.Read(id) ?? throw new InvalidDataException("A captured cold-start outbox row disappeared."))
                        .Single(value=>value.FileId==file);
                },token).ConfigureAwait(false);
                Assert.AreEqual("facts-chunk",captured.Role);Assert.AreEqual(mime,captured.MimeType);
                Assert.AreEqual(folder,captured.Scope.FolderId);Assert.AreEqual(hash,captured.Sha256);Assert.AreEqual(CurrentHash(payload.ToArray()),hash);
                CollectionAssert.AreEqual(captured.Payload.ToArray(),payload.ToArray());fixture.Data.Creates++;
                if(!fixture.Data.Cloud.TryGetValue(file,out var stored))
                {stored=new(captured.Scope.AccountId,file,folder,7,payload.ToArray()){MimeType=mime};fixture.Data.Cloud.Add(file,stored);}
                CollectionAssert.AreEqual(payload.ToArray(),stored.Bytes.ToArray());fixture.Data.MetadataMimeTypes[file]=mime;return stored;
            }
            var actual=await create(folder,file,mime,payload,hash,token).ConfigureAwait(false);
            actual=actual with {MimeType=mime};fixture.Data.Cloud[file]=actual;fixture.Data.MetadataMimeTypes[file]=mime;return actual;
        };
        await AddCurrentSnapshotFactAsync(fixture,"actual-cold-composition-photo").ConfigureAwait(false);
        Assert.AreEqual(0,fixture.Data.Creates);Assert.AreEqual(0,fixture.Data.Allocations);
        Assert.IsNull(await CurrentReceiptAsync(fixture.Data).ConfigureAwait(false));
        var configured=await NewCurrentDesktopCompositionAsync(fixture).ConfigureAwait(false);await using var runtime=configured.Runtime;
        Assert.IsNull(await runtime.InitializeCurrentCatalogAsync(CancellationToken.None).ConfigureAwait(false));Assert.AreEqual(0,fixture.Data.Creates);
        runtime.Request(CancellationToken.None);await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished,runtime.Activity.Stage,runtime.LastAttemptFailure?.ToString() ?? runtime.Activity.Detail);Assert.IsNull(runtime.LastAttemptFailure);Assert.IsNull(runtime.Failure);
        var bootstrap=await CurrentReceiptAsync(fixture.Data).ConfigureAwait(false);Assert.IsNotNull(bootstrap);
        var slot=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibraryCurrentControlSlotRepository(database,fixture.Data.Budget).Current()!,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("captured-3",slot.FileId);Assert.AreEqual(8,fixture.Data.Allocations);Assert.AreEqual(8,fixture.Data.Creates);Assert.AreEqual(1,fixture.Http.Puts);
        var selected=configured.Catalog.Catalog.Selection;Assert.IsNotNull(selected);Assert.IsNotNull(selected.Catalog);Assert.IsTrue(selected.RootSequence>bootstrap.RootSequence);
        var page=await configured.Catalog.Catalog.ReadPageAsync(selected.StageId,null,60,CancellationToken.None).ConfigureAwait(false);Assert.IsNotNull(page);Assert.AreEqual(1,page.Page.Assets.Count);
        var writes=(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts);runtime.Request(CancellationToken.None);await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished,runtime.Activity.Stage);Assert.IsNull(runtime.Failure);Assert.AreEqual(bootstrap,await CurrentReceiptAsync(fixture.Data).ConfigureAwait(false));
        Assert.AreEqual(writes,(fixture.Data.Allocations,fixture.Data.Creates,fixture.Http.Puts));Assert.IsFalse(fixture.Http.BodyFiles.Contains("actual-cold-composition-photo"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDesktopCompositionShowsSourceAttentionWithoutDiscardingOrRetargetingOriginalFrozenWork()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);var original=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{var sources=new LibrarySourceRepository(database);var source=sources.Snapshot().Sources.Single();sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,2);return true;},CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var calls=(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        var configured=await NewCurrentDesktopCompositionAsync(fixture).ConfigureAwait(false);await using var runtime=configured.Runtime;var fatal=false;runtime.Failed+=_=>fatal=true;
        runtime.Request(CancellationToken.None);await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.NeedsAttention,runtime.Activity.Stage);Assert.IsTrue(runtime.Activity.CanRetry);
        Assert.IsInstanceOfType<StaleLibraryConfigurationException>(runtime.LastAttemptFailure);Assert.IsNull(runtime.Failure);Assert.IsFalse(fatal);
        runtime.NotifyLocalCatalogChanged();await runtime.WaitForCurrentAttemptAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(calls,(fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Captures,after.Captures);Assert.AreEqual(before.Publications,after.Publications);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Head,after.Head);Assert.AreEqual(before.Human,after.Human);
    }

    [TestMethod]
    public async Task CurrentDesktopCompositionExitJoinsItsActualRootWriteBeforeAllOwnedReadersAndPreservesRecovery()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        InstallPreparedGraphCreates(fixture,owned.Basis.Publication.Capture.Scope);var plan=await PrepareCurrentPublicationRootForTestAsync(owned).ConfigureAwait(false);
        var bytes=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var configured=await NewCurrentDesktopCompositionAsync(fixture).ConfigureAwait(false);await using var runtime=configured.Runtime;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var releaseOnExit=new TestCallbackReleaseOwner(release);
        fixture.Http.AfterPut=async()=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);};runtime.Request(CancellationToken.None);
        var attempt=runtime.WaitForCurrentAttemptAsync(CancellationToken.None);Task? stopping=null;
        try
        {
            _=await Task.WhenAny(entered.Task,attempt).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(attempt.IsCompleted) {await attempt.ConfigureAwait(false);Assert.IsTrue(entered.Task.IsCompleted,runtime.Activity.Detail);}
            stopping=runtime.DisposeAsync().AsTask();Assert.IsFalse(stopping.IsCompleted);Assert.IsFalse(runtime.ExitConfirmed);Assert.IsFalse(configured.Catalog.ExitConfirmed);Assert.IsFalse(configured.Catalog.Catalog.ExitConfirmed);
            Assert.IsTrue(File.Exists(owned.Path));Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));Assert.AreEqual(1,fixture.Http.Puts);Assert.IsFalse(fixture.Http.Disposed);
        }
        finally {release.TrySetResult();if(stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);}
        await attempt.ConfigureAwait(false);Assert.IsTrue(runtime.ExitConfirmed);Assert.IsTrue(configured.Catalog.ExitConfirmed);Assert.IsTrue(configured.Catalog.Catalog.ExitConfirmed);
        CollectionAssert.AreEqual(bytes,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));Assert.IsNull(await ActiveSelectionAsync(fixture).ConfigureAwait(false));
        Assert.AreEqual(plan,await CurrentPublicationPlanAsync(fixture,owned.Generation).ConfigureAwait(false));fixture.Http.AfterPut=null;
        var resumed=await NewCurrentDesktopCompositionAsync(fixture).ConfigureAwait(false);await using var second=resumed.Runtime;
        second.Request(CancellationToken.None);await second.WaitForCurrentAttemptAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(InitialLibraryPublicationStage.PoliciesPublished,second.Activity.Stage);Assert.IsNull(second.Failure);Assert.AreEqual(1,fixture.Http.Puts);Assert.IsFalse(File.Exists(owned.Path));
        Assert.AreEqual(1,(await resumed.Catalog.Catalog.ReadPageAsync(null,null,60,CancellationToken.None).ConfigureAwait(false))!.Page.Assets.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDesktopCompositionRejectsForeignBudgetOrParallelLegacyOwnershipBeforeAnyProviderIo(bool parallelLegacy)
    {
        await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        await using var current=new CurrentLibraryCatalogRuntime(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var legacy=new LocalCatalogReconciliationService(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        var budget=parallelLegacy?fixture.Data.Budget:new StorageBudgetManager(new ProcessorStateStorageReader(input.StateDirectory));
        var calls=(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts);
        Assert.ThrowsExactly<ArgumentException>(()=>new InitialLibraryPublicationRuntime(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,budget,catalog:parallelLegacy?legacy:null,currentCatalog:current));
        Assert.AreEqual(calls,(fixture.Http.BodyReads,fixture.Http.MetadataReads,fixture.Data.Creates,fixture.Data.Allocations,fixture.Http.Puts));Assert.IsFalse(current.ExitConfirmed);
    }

    private static async Task<(InitialLibraryPublicationRuntime Runtime,CurrentLibraryCatalogRuntime Catalog)> NewCurrentDesktopCompositionAsync(CurrentReaderFixture fixture)
    {
        var input=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>(database.StateDirectory,database.InstallationId),CancellationToken.None).ConfigureAwait(false);
        var current=new CurrentLibraryCatalogRuntime(fixture.Data.Setup.Accounts.State,input.StateDirectory,input.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        try {return (new(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget,currentCatalog:current),current);}
        catch {await current.DisposeAsync().ConfigureAwait(false);throw;}
    }
}
