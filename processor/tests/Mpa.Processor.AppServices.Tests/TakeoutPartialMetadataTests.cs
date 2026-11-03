using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CommittedMetadataWakesPublicationAfterALaterCapacityFailureOrJoinedPauseAndRetryDoesNotRedoThePrefix(bool capacity)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token,metadata:true).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var firstPath=selection.Path+".json";var secondPath=selection.Path+".supplemental-metadata.json";var raw=await File.ReadAllBytesAsync(firstPath,deadline.Token).ConfigureAwait(false);
        await File.WriteAllBytesAsync(secondPath,raw,deadline.Token).ConfigureAwait(false);
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var cloud=new OriginalCloud(selection.Payload);WireOriginal(fixture.Data.Setup.Accounts,cloud);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);using var storage=await state.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,fixture.Data.Budget),deadline.Token).ConfigureAwait(false);
        await using var native=new ProcessorWorkSupervisor();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var attempts=0;var notifications=0;
        var metadata=new TakeoutImportMetadataRunner(state,fixture.Data.Budget,storage,native,async token=>
        {
            if(++attempts==2)
            {
                if(capacity)throw new TakeoutMetadataCapacityException("Synthetic capacity failure after the actual first worker and observation committed.");
                entered.TrySetResult();await release.Task.ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();return await TakeoutWorkerInstallation.OpenInstalledAsync(Path.Combine(AppContext.BaseDirectory,"workers","takeout-metadata"),token).ConfigureAwait(false);
        });
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)),metadata:metadata,metadataReady:()=>Interlocked.Increment(ref notifications));
        await imports.ResumeAsync().ConfigureAwait(false);await using var children=new ProcessorWorkGroup(inventory,imports);await using var controls=new ProcessorWorkGroup(native,children);
        var review=(await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false))!;await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);
        if(capacity)
        {
            var attention=await WaitImportAsync(imports,review.Id,"needs-attention",deadline.Token).ConfigureAwait(false);Assert.AreEqual("metadata-capacity",attention.Issue);await imports.PauseAsync().ConfigureAwait(false);
        }
        else
        {
            Task? pause=null;
            try{await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);Assert.AreEqual(0,Volatile.Read(ref notifications));pause=controls.PauseAsync();Assert.IsFalse(pause.IsCompleted);}
            finally{release.TrySetResult();}
            await pause!.ConfigureAwait(false);Assert.AreEqual(ProcessorWorkState.Paused,controls.State);
        }
        Assert.AreEqual(1,Volatile.Read(ref notifications),"Already committed facts need their engine wake-up even when the pass cannot finish.");Assert.AreEqual(2,attempts);Assert.AreEqual(1,cloud.Allocations);Assert.IsNull(imports.Failure);
        var completed=await state.ExecuteAsync(db=>new TakeoutImportInventoryRepository(db).ReadCompleted(review.Id)!,deadline.Token).ConfigureAwait(false);
        string[] keys;
        using(var catalog=await TakeoutAssociationCatalog.OpenCompletedAsync(completed,fixture.Data.Budget,deadline.Token).ConfigureAwait(false))
            keys=catalog.ReadPage(TakeoutEntryKind.JsonSidecar).Entries.Select(entry=>entry.Key).ToArray();
        Assert.AreEqual(2,keys.Length);
        var first=await state.ExecuteAsync(db=>
        {
            var rows=keys.Select(key=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(review.Id,key)).Where(row=>row is not null).ToArray();Assert.AreEqual(1,rows.Length);
            var progress=new TakeoutImportInventoryRepository(db).Activity(review.Id).Metadata!;Assert.AreEqual(2L,progress.Total);Assert.AreEqual(1L,progress.Ready);Assert.IsFalse(progress.Finished);Assert.IsFalse(new TakeoutImportedPhotoFactsRepository(db).HasObservedImport(review.Id));return rows.Single()!;
        },deadline.Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(raw,(await storage.ReadVerifiedAsync(first,deadline.Token).ConfigureAwait(false)).CopyRawSource());
        Assert.AreEqual(2,Directory.EnumerateFiles(Path.Combine(setup.StateDirectory,"takeout-metadata-stages"),"source.bin",SearchOption.AllDirectories).Count());
        File.Delete(firstPath);File.Delete(secondPath);
        if(capacity){await imports.RetryAsync(review.Id,deadline.Token).ConfigureAwait(false);await imports.ResumeAsync().ConfigureAwait(false);}
        else await controls.ResumeAsync().ConfigureAwait(false);
        var processed=await WaitImportMetadataAsync(imports,review.Id,deadline.Token).ConfigureAwait(false);Assert.AreEqual(new TakeoutMetadataProgress(2,2,0,0,true),processed.Metadata);
        await controls.PauseAsync().ConfigureAwait(false);Assert.AreEqual(3,attempts);Assert.AreEqual(2,notifications);Assert.AreEqual(1,cloud.Allocations);
        var retained=await state.ExecuteAsync(db=>new TakeoutMetadataJobRepository(db).ReadCompletedImported(review.Id,first.Imported.Sidecar.Key)!,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(first.JobId,retained.JobId);
        Assert.AreEqual((first.Artifact.Bytes,first.Artifact.Sha256,first.Artifact.RelativePath),(retained.Artifact.Bytes,retained.Artifact.Sha256,retained.Artifact.RelativePath));
        Assert.AreEqual((first.Source.Bytes,first.Source.Sha256,first.Source.RelativePath),(retained.Source.Bytes,retained.Source.Sha256,retained.Source.RelativePath));
        CollectionAssert.AreEqual(raw,(await storage.ReadVerifiedAsync(retained,deadline.Token).ConfigureAwait(false)).CopyRawSource());
        CollectionAssert.AreEqual(selection.Payload,await File.ReadAllBytesAsync(selection.Path,deadline.Token).ConfigureAwait(false));Assert.IsNull(controls.Failure);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
