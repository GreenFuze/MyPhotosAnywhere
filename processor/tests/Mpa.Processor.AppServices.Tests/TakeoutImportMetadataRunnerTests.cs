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
    }
}
