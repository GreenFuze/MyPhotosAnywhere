using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task SelectionAdmittedDuringAWorkerPassCannotBeHiddenByThatPassCompletionMarker()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));await using var fixture=new Fixture();
        var input=await PrepareOriginalJobAsync(fixture,deadline.Token,metadata:true).ConfigureAwait(false);
        var completed=await fixture.State.ExecuteAsync(db=>{var imports=new TakeoutImportInventoryRepository(db);return imports.ReadCompleted(imports.Identities().Single())!;},deadline.Token).ConfigureAwait(false);
        using var storage=await fixture.State.ExecuteAsync(db=>new TakeoutMetadataStageStorage(db,input.Budget),deadline.Token).ConfigureAwait(false);await using var native=new ProcessorWorkSupervisor();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var launches=0;
        var runner=new TakeoutImportMetadataRunner(fixture.State,input.Budget,storage,native,async token=>
        {if(++launches==1){entered.TrySetResult();await release.Task.WaitAsync(token).ConfigureAwait(false);}return await TakeoutWorkerInstallation.OpenInstalledAsync(Path.Combine(AppContext.BaseDirectory,"workers","takeout-metadata"),token).ConfigureAwait(false);});
        var first=await runner.RunAsync(completed,deadline.Token).ConfigureAwait(false);Assert.AreEqual(1L,first.WaitingForOriginal);Assert.AreEqual(1L,first.Unmatched);Assert.AreEqual(0,launches);
        var cloud=new OriginalCloud(input.Payload);WireOriginal(fixture,cloud);await using(var upload=new TakeoutOriginalUploadService(fixture.State,fixture.Service,input.Budget,new OriginalAuthority(input.Approval)))await upload.UploadAsync(input.Job,deadline.Token).ConfigureAwait(false);
        var pass=runner.RunAsync(completed,deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            await fixture.State.ExecuteAsync(db=>
            {
                var issues=new TakeoutMetadataIssueRepository(db);var unmatched=issues.ReadPage(completed.ImportId).Issues.Single(value=>value.Code=="name-mismatch");var original=new TakeoutOriginalJobRepository(db).ReadChoices(completed.ImportId).Originals.Single();
                Assert.IsTrue(unmatched.RawAvailable);
                issues.SelectOriginal(completed.ImportId,unmatched.Key,original.Key);return true;
            },deadline.Token).ConfigureAwait(false);
            File.Delete(Path.Combine(Path.GetDirectoryName(input.Path)!, "other.json"));
        }
        finally{release.TrySetResult();await pass.ConfigureAwait(false);}
        await fixture.State.ExecuteAsync(db=>
        {Assert.IsFalse(new TakeoutImportInventoryRepository(db).Activity(completed.ImportId).Metadata!.Finished);Assert.IsFalse(new TakeoutImportedPhotoFactsRepository(db).HasObservedImport(completed.ImportId));return true;},deadline.Token).ConfigureAwait(false);
        await runner.RunAsync(completed,deadline.Token).ConfigureAwait(false);
        await fixture.State.ExecuteAsync(db=>
        {
            Assert.AreEqual(new TakeoutMetadataProgress(4,2,2,0,true),new TakeoutImportInventoryRepository(db).Activity(completed.ImportId).Metadata);
            Assert.IsTrue(new TakeoutImportedPhotoFactsRepository(db).HasObservedImport(completed.ImportId));Assert.AreEqual(2,new TakeoutMetadataIssueRepository(db).ReadPage(completed.ImportId).Issues.Count);return true;
        },deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(2,launches);Assert.AreEqual(1,cloud.Allocations);CollectionAssert.AreEqual(input.Payload,cloud.Uploaded);Assert.IsNull(native.Failure);Assert.AreEqual(0L,input.Budget.Status().ReservedBytes);
    }
}
