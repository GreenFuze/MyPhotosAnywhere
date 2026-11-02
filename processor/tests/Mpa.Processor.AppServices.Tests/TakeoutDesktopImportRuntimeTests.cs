using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.GoogleDrive;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task DesktopFolderImportReviewsActualInputBeforeExplicitStartAndBrowserCancellationDoesNotOwnTheCopy()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token,metadata:true).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var originalAllocator=fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate;var cloud=new OriginalCloud(selection.Payload);WireOriginal(fixture.Data.Setup.Accounts,cloud);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        using var metadataStorage=await state.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,fixture.Data.Budget),deadline.Token).ConfigureAwait(false);
        await using var native=new ProcessorWorkSupervisor();var metadataLaunches=0;
        var metadata=new TakeoutImportMetadataRunner(state,fixture.Data.Budget,metadataStorage,native,token=>
        {metadataLaunches++;return TakeoutWorkerInstallation.OpenInstalledAsync(Path.Combine(AppContext.BaseDirectory,"workers","takeout-metadata"),token);});
        var scanQueued=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var scanNotifications=0;
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)),()=>{Interlocked.Increment(ref scanNotifications);scanQueued.TrySetResult();},metadata);
        await imports.ResumeAsync().ConfigureAwait(false);var review=await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false);Assert.IsNotNull(review);
        var ready=await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);Assert.AreEqual(1L,ready.Media);Assert.IsFalse(ready.Requested);Assert.AreEqual(0,cloud.Allocations);
        Assert.IsFalse(ready.InputName.Contains(Path.DirectorySeparatorChar));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>state.ExecuteAsync(db=>new SourceDiscoveryRepository(db).EnsureImportedOriginalsScheduled(review.Id),deadline.Token)).ConfigureAwait(false);
        using var browser=new CancellationTokenSource();var admitted=await imports.StartAsync(review.Id,browser.Token).ConfigureAwait(false);Assert.IsTrue(admitted.Requested);await browser.CancelAsync().ConfigureAwait(false);
        var verified=await WaitImportAsync(imports,review.Id,"originals-verified",deadline.Token).ConfigureAwait(false);Assert.AreEqual(1L,verified.Verified);Assert.AreEqual(1,cloud.Allocations);
        CollectionAssert.AreEqual(selection.Payload,cloud.Uploaded);CollectionAssert.AreEqual(selection.Payload,await File.ReadAllBytesAsync(selection.Path,deadline.Token).ConfigureAwait(false));
        await scanQueued.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        var processed=await WaitImportMetadataAsync(imports,review.Id,deadline.Token).ConfigureAwait(false);Assert.AreEqual(new TakeoutMetadataProgress(1,1,0,0,true),processed.Metadata);Assert.AreEqual(1,metadataLaunches);
        File.Delete(selection.Path+".json");await imports.PauseAsync().ConfigureAwait(false);
        var scan=await state.ExecuteAsync(db=>
        {
            var repository=new SourceDiscoveryRepository(db);var retained=repository.EnsureImportedOriginalsScheduled(review.Id);Assert.IsFalse(retained.Created);
            var activity=repository.ReadActivity().Jobs.Single(value=>value.JobId==retained.JobId);
            Assert.AreEqual(review.Target.AccountId,activity.Scope.AccountId);Assert.AreEqual(review.Target.FolderId,activity.Scope.RootFolderId);Assert.AreEqual(setup.Source,activity.Scope.SourceId);
            Assert.AreEqual(1,new DurableJobRepository(db).Summary(retained.JobId).Queued);return retained;
        },deadline.Token).ConfigureAwait(false);
        var sourceReads=0;var originalFolderReader=fixture.Data.Setup.Accounts.Readers.FolderRead;fixture.Data.Setup.Accounts.Readers.FolderRead=(folder,token)=>
        {token.ThrowIfCancellationRequested();Assert.AreEqual(review.Target.FolderId,folder);return Task.FromResult(new DriveFolder(folder,"Imported photos",true,[],null));};
        fixture.Data.Setup.Accounts.Readers.SourceRead=(folder,_,cursor,token)=>
        {
            token.ThrowIfCancellationRequested();Assert.AreEqual(review.Target.FolderId,folder);Assert.IsNull(cursor);sourceReads++;
            return Task.FromResult(new DriveSourcePage([new("first-original-id",7,"Human renamed.jpg","image/jpeg",selection.Payload.Length,null,
                new("first-original-id","Human renamed.jpg","image/jpeg",400,300,"2026:10:20 15:30:00","2026-10-20T15:30:00Z"))],[],null,false));
        };
        await using(var discovery=new SourceDiscoveryRunner(state,fixture.Data.Setup.Accounts.Service,fixture.Data.Budget))
        {
            var steps=0;while(await discovery.RunNextAsync(deadline.Token).ConfigureAwait(false) is not null)Assert.IsTrue(++steps<=4,"A single imported source must have bounded verification/listing work.");
            Assert.IsNull(discovery.Failure);
        }
        var discovered=await state.ExecuteAsync(db=>new DiscoveredCatalogRepository(db).ReadPage(),deadline.Token).ConfigureAwait(false);
        var imported=discovered.Assets.Single(value=>value.Facts.FileId=="first-original-id");Assert.AreEqual("Human renamed.jpg",imported.Facts.Name);
        Assert.AreEqual(new DriveAssetIdentity(review.Target.LibraryId).Derive(review.Target.AccountId,"first-original-id"),imported.Id);Assert.AreEqual(1,sourceReads);
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=originalAllocator;
        fixture.Data.Setup.Accounts.Readers.FolderRead=originalFolderReader;
        ConfigureCurrentPublication(fixture.Data);InstallCurrentInventoryTransport(fixture);InstallImportedCatalogFixture(fixture);
        var published=await current.RunUntilCurrentAsync(deadline.Token).ConfigureAwait(false);
        var activated=current.Catalog.Selection;Assert.IsNotNull(activated);Assert.AreEqual(published.Commit,activated.Catalog);
        var page=await current.Catalog.ReadPageAsync(activated.StageId,null,60,deadline.Token).ConfigureAwait(false);Assert.IsNotNull(page);
        var portable=page.Page.Assets.Single(value=>value.Id==imported.Id);Assert.AreEqual("Human renamed.jpg",portable.Name);
        var replay=await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);Assert.AreEqual("originals-verified",replay.State);Assert.AreEqual(1,cloud.Allocations);
        await imports.ResumeAsync().ConfigureAwait(false);await imports.PauseAsync().ConfigureAwait(false);Assert.AreEqual(1,scanNotifications);Assert.AreEqual(1,metadataLaunches);
        var before=await state.ExecuteAsync(db=>new SourceDiscoveryRepository(db).ReadActivity().Jobs.Single(value=>value.JobId==scan.JobId).Scope,deadline.Token).ConfigureAwait(false);
        await state.ExecuteAsync(db=>
        {
            var sources=new LibrarySourceRepository(db);var snapshot=sources.Snapshot();var selected=snapshot.Sources.Single();sources.Update(selected.Id,false,selected.IncludeDescendants,selected.Revision,snapshot.Revision);
            var retained=new SourceDiscoveryRepository(db).EnsureImportedOriginalsScheduled(review.Id);Assert.AreEqual(scan.JobId,retained.JobId);Assert.IsFalse(retained.Created);
            Assert.AreEqual(before,new SourceDiscoveryRepository(db).ReadActivity().Jobs.Single(value=>value.JobId==scan.JobId).Scope);return true;
        },deadline.Token).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DesktopFolderImportCannotRetargetDestinationWhileTheActualNativeChooserIsPending()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single()},deadline.Token).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,async token=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);token.ThrowIfCancellationRequested();return Path.GetDirectoryName(selection.Path);});
        var count=(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count;
        var choosing=imports.ChooseFolderAsync(setup.Source.Id,deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await state.ExecuteAsync(db=>{var sources=new LibrarySourceRepository(db);var snapshot=sources.Snapshot();sources.Update(setup.Source.Id,false,setup.Source.IncludeDescendants,setup.Source.Revision,snapshot.Revision);return true;},deadline.Token).ConfigureAwait(false);
        }
        finally {release.TrySetResult();}
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>choosing).ConfigureAwait(false);Assert.AreEqual(count,(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count);
    }

    [TestMethod]
    public async Task DesktopImportPauseAndExitJoinActualOriginalRequestAndResumeOnlyTheSameAssignedObject()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var cloud=new OriginalCloud(selection.Payload){Hold=true};WireOriginal(fixture.Data.Setup.Accounts,cloud);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)));
        await imports.ResumeAsync().ConfigureAwait(false);var review=(await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false))!;await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);
        await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);Task? pause=null;
        try
        {
            await cloud.Entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);pause=imports.PauseAsync();await cloud.Canceled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Assert.IsFalse(pause.IsCompleted);Assert.AreEqual(ProcessorWorkState.Pausing,imports.State);Assert.AreEqual(0L,(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Single(row=>row.Id==review.Id).Verified);
        }
        finally {cloud.Release.TrySetResult();}
        await pause!.ConfigureAwait(false);Assert.AreEqual(ProcessorWorkState.Paused,imports.State);cloud.Hold=false;
        await imports.ResumeAsync().ConfigureAwait(false);await WaitImportAsync(imports,review.Id,"originals-verified",deadline.Token).ConfigureAwait(false);Assert.AreEqual(1,cloud.Allocations);
        await imports.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(imports.ExitConfirmed);
    }

    [TestMethod]
    public async Task DesktopImportExitCannotAcknowledgeBeforeAnUncooperativeNativeChooserActuallyJoins()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var state=fixture.Data.Setup.Accounts.State;var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,async token=>{using var registration=token.Register(()=>canceled.TrySetResult());entered.TrySetResult();await release.Task.ConfigureAwait(false);return null;});
        var picking=imports.ChooseFolderAsync(setup.Source,deadline.Token);Task? closing=null;
        try {await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);closing=imports.DisposeAsync().AsTask();await canceled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.IsFalse(closing.IsCompleted);Assert.IsFalse(imports.ExitConfirmed);}
        finally {release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>picking).ConfigureAwait(false);await closing!.ConfigureAwait(false);Assert.IsTrue(imports.ExitConfirmed);
    }

    [TestMethod]
    public async Task DesktopImportMissingInputIsRetainedPerImportAndRetryUsesTheSameReviewWithoutStoppingTheEngine()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var cloud=new OriginalCloud(selection.Payload);WireOriginal(fixture.Data.Setup.Accounts,cloud);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)));
        await imports.ResumeAsync().ConfigureAwait(false);var review=(await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false))!;
        await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);var stamp=File.GetLastWriteTimeUtc(selection.Path);File.Delete(selection.Path);
        await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);var attention=await WaitImportAsync(imports,review.Id,"needs-attention",deadline.Token).ConfigureAwait(false);
        Assert.AreEqual("input-unavailable",attention.Issue);Assert.IsTrue(attention.Requested);Assert.AreEqual(review.Target,attention.Target);Assert.AreEqual(0,cloud.Allocations);Assert.IsNull(imports.Failure);Assert.IsNull(inventory.Failure);
        await File.WriteAllBytesAsync(selection.Path,selection.Payload,deadline.Token).ConfigureAwait(false);File.SetLastWriteTimeUtc(selection.Path,stamp);
        await imports.RetryAsync(review.Id,deadline.Token).ConfigureAwait(false);var verified=await WaitImportAsync(imports,review.Id,"originals-verified",deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(review.Id,verified.Id);Assert.AreEqual(review.Target,verified.Target);Assert.AreEqual(1,cloud.Allocations);Assert.IsNull(verified.Issue);
    }

    private static async Task<TakeoutImportActivity> WaitImportMetadataAsync(TakeoutDesktopImportRuntime runtime,Guid id,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();if(runtime.Failure is{}failure)throw new InvalidOperationException("The actual import metadata owner failed.",failure);
            var item=(await runtime.ListAsync(token).ConfigureAwait(false)).Single(value=>value.Id==id);if(item.Metadata is{Finished:true})return item;
            await Task.Delay(20,token).ConfigureAwait(false);
        }
    }
    private static async Task<TakeoutImportActivity> WaitImportAsync(TakeoutDesktopImportRuntime runtime,Guid id,string expected,CancellationToken token)
    {
        var elapsed=Stopwatch.StartNew();
        while(true)
        {
            token.ThrowIfCancellationRequested();if(runtime.Failure is{}failure)throw new InvalidOperationException("The actual import owner failed.",failure);
            var item=(await runtime.ListAsync(token).ConfigureAwait(false)).Single(value=>value.Id==id);if(item.State==expected)return item;
            if(elapsed.Elapsed>TimeSpan.FromSeconds(30))throw new TimeoutException("Actual import did not reach "+expected+"; current state="+item.State);
            await Task.Delay(20,token).ConfigureAwait(false);
        }
    }
}
