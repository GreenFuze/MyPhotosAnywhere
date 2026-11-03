using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task DesktopArchiveChooserRejectsDuplicateAndExcessPartsBeforeDurableAdmissionAndRetainsCanceledSelection()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var state=fixture.Data.Setup.Accounts.State;var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        IReadOnlyList<string>? paths=null;
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,null,archivePicker:_=>Task.FromResult(paths));
        Assert.IsTrue(imports.CanChooseArchives);Assert.IsFalse(imports.CanChooseFolder);
        var count=(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count;
        Assert.IsNull(await imports.ChooseArchivesAsync(setup.Source,deadline.Token).ConfigureAwait(false));
        paths=Array.Empty<string>();Assert.IsNull(await imports.ChooseArchivesAsync(setup.Source,deadline.Token).ConfigureAwait(false));
        var path=Path.Combine(setup.StateDirectory,"not-opened.zip");paths=Enumerable.Repeat(path,65).ToArray();
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>imports.ChooseArchivesAsync(setup.Source,deadline.Token)).ConfigureAwait(false);
        paths=[path,Path.Combine(setup.StateDirectory,".","not-opened.zip")];
        await Assert.ThrowsExactlyAsync<ArgumentException>(()=>imports.ChooseArchivesAsync(setup.Source,deadline.Token)).ConfigureAwait(false);
        paths=[" "];await Assert.ThrowsExactlyAsync<ArgumentException>(()=>imports.ChooseArchivesAsync(setup.Source,deadline.Token)).ConfigureAwait(false);
        var good=Path.Combine(setup.StateDirectory,"good.zip");using(var archive=ZipFile.Open(good,ZipArchiveMode.Create)){using var writer=new StreamWriter(archive.CreateEntry("a.jpg").Open());writer.Write("synthetic original");}
        var bad=Path.Combine(setup.StateDirectory,"bad.zip");await File.WriteAllBytesAsync(bad,new byte[22],deadline.Token).ConfigureAwait(false);paths=[good,bad];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>imports.ChooseArchivesAsync(setup.Source,deadline.Token)).ConfigureAwait(false);
        using(var exclusive=new FileStream(good,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert.IsTrue(exclusive.Length>0,"A rejected later part must release the first native ZIP owner.");
        using var canceled=new CancellationTokenSource();await canceled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(()=>imports.ChooseArchivesAsync(setup.Source,canceled.Token)).ConfigureAwait(false);
        Assert.AreEqual(count,(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count);Assert.IsNull(imports.Failure);Assert.IsNull(inventory.Failure);
    }

    [TestMethod]
    public async Task DesktopArchiveChooserRechecksCapturedDestinationAndSharesExclusiveFolderAdmission()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var state=fixture.Data.Setup.Accounts.State;var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single()},deadline.Token).ConfigureAwait(false);
        var archivePath=Path.Combine(setup.StateDirectory,"photos.zip");using(var archive=ZipFile.Open(archivePath,ZipArchiveMode.Create)){using var writer=new StreamWriter(archive.CreateEntry("a.jpg").Open());writer.Write("synthetic original");}
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(null),archivePicker:async token=>{entered.TrySetResult();await release.Task.ConfigureAwait(false);token.ThrowIfCancellationRequested();return new[]{archivePath};});
        var count=(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count;var choosing=imports.ChooseArchivesAsync(setup.Source.Id,deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessorWorkStateConflictException>(()=>imports.ChooseFolderAsync(setup.Source.Id,deadline.Token)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessorWorkStateConflictException>(()=>imports.ChooseArchivesAsync(setup.Source.Id,deadline.Token)).ConfigureAwait(false);
            await state.ExecuteAsync(db=>{var sources=new LibrarySourceRepository(db);var snapshot=sources.Snapshot();sources.Update(setup.Source.Id,false,setup.Source.IncludeDescendants,setup.Source.Revision,snapshot.Revision);return true;},deadline.Token).ConfigureAwait(false);
        }
        finally{release.TrySetResult();}
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>choosing).ConfigureAwait(false);
        Assert.AreEqual(count,(await imports.ListAsync(deadline.Token).ConfigureAwait(false)).Count);Assert.IsNull(imports.Failure);
        using var exclusive=new FileStream(archivePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.IsTrue(exclusive.Length>0,"Rejected admission must close the ZIP owner.");
    }

    [TestMethod]
    public async Task DesktopArchiveExitWaitsForUncooperativeNativeDialogBeforeAcknowledgingRetirement()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var state=fixture.Data.Setup.Accounts.State;var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var canceled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,null,archivePicker:async token=>{using var registration=token.Register(()=>canceled.TrySetResult());entered.TrySetResult();await release.Task.ConfigureAwait(false);return null;});
        var picking=imports.ChooseArchivesAsync(setup.Source,deadline.Token);Task? closing=null;
        try{await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);closing=imports.DisposeAsync().AsTask();await canceled.Task.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.IsFalse(closing.IsCompleted);Assert.IsFalse(imports.ExitConfirmed);}
        finally{release.TrySetResult();}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>picking).ConfigureAwait(false);await closing!.ConfigureAwait(false);Assert.IsTrue(imports.ExitConfirmed);
    }
}
