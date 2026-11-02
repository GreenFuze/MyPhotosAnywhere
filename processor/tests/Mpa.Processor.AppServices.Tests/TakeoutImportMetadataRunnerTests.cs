using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;
using Mpa.Processor.Protocol;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task ImportedMetadataRunsInstalledNativeWorkerOnlyAfterActualOriginalVerificationAndBadSidecarsDoNotBlockItsCopy()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=new Fixture();
        var input=await PrepareOriginalJobAsync(fixture,deadline.Token,metadata:true).ConfigureAwait(false);
        var completed=await fixture.State.ExecuteAsync(db=>{var imports=new TakeoutImportInventoryRepository(db);return imports.ReadCompleted(imports.Identities().Single())!;},deadline.Token).ConfigureAwait(false);
        using var storage=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,input.Budget),deadline.Token).ConfigureAwait(false);
        await using var native=new ProcessorWorkSupervisor();var launches=0;
        var metadata=new TakeoutImportMetadataRunner(fixture.State,input.Budget,storage,native,token=>
        {launches++;return TakeoutWorkerInstallation.OpenInstalledAsync(Path.Combine(AppContext.BaseDirectory,"workers","takeout-metadata"),token);});
        var before=await metadata.RunAsync(completed,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(new TakeoutImportMetadataResult(0,0,2,1,1),before);Assert.AreEqual(0,launches);
        var cloud=new OriginalCloud(input.Payload);WireOriginal(fixture,cloud);
        await using(var original=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,new OriginalAuthority(input.Approval)))
            await original.UploadAsync(input.Job,deadline.Token).ConfigureAwait(false);
        var result=await metadata.RunAsync(completed,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(new TakeoutImportMetadataResult(1,0,2,1,0),result);Assert.AreEqual(1,launches);
        File.Delete(input.Path+".json");
        Assert.AreEqual(new TakeoutImportMetadataResult(0,1,2,1,0),await metadata.RunAsync(completed,deadline.Token).ConfigureAwait(false));Assert.AreEqual(1,launches);
        await fixture.State.ExecuteAsync(db=>
        {
            var receipts=new DurableJobRepository(db).ReadPendingReceipts();
            var metadataReceipts=receipts.Where(receipt=>receipt.Stage==TakeoutMetadataJobRepository.Stage).ToArray();Assert.AreEqual(1,metadataReceipts.Length);
            using var body=JsonDocument.Parse(metadataReceipts[0].Receipt);
            Assert.AreEqual(new DriveAssetIdentity(completed.Target.LibraryId).Derive(completed.Target.AccountId,"first-original-id").ToString("D"),body.RootElement.GetProperty("mediaId").GetString());
            Assert.AreEqual(1,new DurableJobRepository(db).Summary(input.Job).Ready);return true;
        },deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(1,cloud.Allocations);CollectionAssert.AreEqual(input.Payload,cloud.Uploaded);
        CollectionAssert.AreEqual(input.Payload,await File.ReadAllBytesAsync(input.Path,deadline.Token).ConfigureAwait(false));
        Assert.AreEqual(ProcessorWorkState.Ready,native.State);Assert.IsNull(native.Failure);Assert.AreEqual(0L,input.Budget.Status().ReservedBytes);
        var progress=await fixture.State.ExecuteAsync(db=>new TakeoutImportInventoryRepository(db).Activity(completed.ImportId).Metadata,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(new TakeoutMetadataProgress(4,1,3,0,true),progress);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DesktopMetadataPauseOrCapacityRetainsCapturedWorkAndRetriesWithoutAnotherOriginalUpload(bool capacity)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=await CurrentReaderFixture.CreateAsync(addSource:true).ConfigureAwait(false);
        var selection=await NativeTakeoutSelectionAsync(fixture,deadline.Token,metadata:true).ConfigureAwait(false);var state=fixture.Data.Setup.Accounts.State;
        var setup=await state.ExecuteAsync(db=>new{db.StateDirectory,db.InstallationId,Source=new LibrarySourceRepository(db).Snapshot().Sources.Single().Id},deadline.Token).ConfigureAwait(false);
        var cloud=new OriginalCloud(selection.Payload);WireOriginal(fixture.Data.Setup.Accounts,cloud);
        await using var current=new CurrentLibraryCatalogRuntime(state,setup.StateDirectory,setup.InstallationId,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        await using var inventory=new TakeoutInventoryService(state,fixture.Data.Budget);
        using var storage=await state.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,fixture.Data.Budget),deadline.Token).ConfigureAwait(false);
        await using var native=new ProcessorWorkSupervisor();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var released=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var attempts=0;
        var metadata=new TakeoutImportMetadataRunner(state,fixture.Data.Budget,storage,native,async token=>
        {
            if(++attempts==1)
            {
                if(capacity)throw new TakeoutMetadataCapacityException("Synthetic bounded metadata capacity reached before child admission.");
                entered.TrySetResult();await released.Task.ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();return await TakeoutWorkerInstallation.OpenInstalledAsync(Path.Combine(AppContext.BaseDirectory,"workers","takeout-metadata"),token).ConfigureAwait(false);
        });
        await using var imports=new TakeoutDesktopImportRuntime(state,fixture.Data.Budget,inventory,current,_=>Task.FromResult<string?>(Path.GetDirectoryName(selection.Path)),metadata:metadata);
        await imports.ResumeAsync().ConfigureAwait(false);await using var children=new ProcessorWorkGroup(inventory,imports);await using var controls=new ProcessorWorkGroup(native,children);
        var review=(await imports.ChooseFolderAsync(setup.Source,deadline.Token).ConfigureAwait(false))!;await WaitImportAsync(imports,review.Id,"ready",deadline.Token).ConfigureAwait(false);await imports.StartAsync(review.Id,deadline.Token).ConfigureAwait(false);
        if(capacity)
        {
            var attention=await WaitImportAsync(imports,review.Id,"needs-attention",deadline.Token).ConfigureAwait(false);
            Assert.AreEqual("metadata-capacity",attention.Issue);Assert.AreEqual(1L,attention.Verified);Assert.IsNull(imports.Failure);Assert.IsNull(controls.Failure);
            File.Delete(selection.Path+".json");await imports.RetryAsync(review.Id,deadline.Token).ConfigureAwait(false);
        }
        else
        {
            Task? pause=null;
            try
            {
                await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);pause=controls.PauseAsync();
                Assert.IsFalse(pause.IsCompleted);Assert.AreEqual(ProcessorWorkState.Pausing,controls.State);
            }
            finally{released.TrySetResult();}
            await pause!.ConfigureAwait(false);Assert.AreEqual(ProcessorWorkState.Paused,controls.State);File.Delete(selection.Path+".json");
            await controls.ResumeAsync().ConfigureAwait(false);
        }
        var processed=await WaitImportMetadataAsync(imports,review.Id,deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(new TakeoutMetadataProgress(1,1,0,0,true),processed.Metadata);Assert.AreEqual(2,attempts);Assert.AreEqual(1,cloud.Allocations);
        CollectionAssert.AreEqual(selection.Payload,cloud.Uploaded);CollectionAssert.AreEqual(selection.Payload,await File.ReadAllBytesAsync(selection.Path,deadline.Token).ConfigureAwait(false));
        await controls.PauseAsync().ConfigureAwait(false);Assert.IsNull(controls.Failure);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
