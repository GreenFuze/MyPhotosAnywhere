using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentDependencyUploadReceiptsItsExactReadbackThenNeedsActualFreshInventoryBeforeReturningVerifiedProgress()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var snapshot=await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false);
        var creates=fixture.Data.Creates;var allocations=fixture.Data.Allocations;var puts=fixture.Http.Puts;
        await using var owner=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader);
        var saved=await owner.UploadAsync(admission,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(admission.Original.Dependency.Id,saved.Id);Assert.AreEqual(admission.Original.Dependency.FileId,saved.FileId);
        Assert.AreEqual(admission.Original.Dependency.Scope,saved.Scope);Assert.AreEqual(owned.Payload.Length,saved.Payload.Length);
        CollectionAssert.AreEqual(owned.Payload,saved.Payload.ToArray());Assert.IsNotNull(saved.VerifiedVersion);
        Assert.AreEqual(creates+1,fixture.Data.Creates);Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.AreEqual(puts,fixture.Http.Puts);
        await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>fixture.Reader.AssertCatalogDependencyCloudAdmissionAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        var fresh=await FreshDependencyUploadAdmissionAsync(owned,saved.Id).ConfigureAwait(false);
        var resumed=await owner.UploadAsync(fresh,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(saved.Id,resumed.Id);Assert.AreEqual(saved.FileId,resumed.FileId);Assert.AreEqual(saved.VerifiedVersion,resumed.VerifiedVersion);
        Assert.AreEqual(creates+1,fixture.Data.Creates);Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.IsNull(owner.Failure);
        var after=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);Assert.AreEqual(before.Signing,after.Signing);Assert.AreEqual(before.Captures,after.Captures);
        Assert.AreEqual(before.Human,after.Human);Assert.AreEqual(before.Head,after.Head);CollectionAssert.AreEqual(snapshot,await File.ReadAllBytesAsync(owned.Path).ConfigureAwait(false));
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("current-dependency-original"));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDependencyUploadLostReplyRetainsOriginalBytesAndAllocationAcrossActualNewObservationAndOwner()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        var allocations=fixture.Data.Allocations;var lost=false;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {
            var value=await create(folder,file,mime,bytes,sha,token).ConfigureAwait(false);
            if(!lost){lost=true;throw new HttpRequestException("Lost dependency create completion after remote storage.");}return value;
        };
        await using(var first=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader))
            await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>first.UploadAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        var pending=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
        Assert.AreEqual(admission.Original.Dependency.FileId,pending.FileId);Assert.IsNull(pending.VerifiedVersion);CollectionAssert.AreEqual(owned.Payload,pending.Payload.ToArray());
        var next=await FreshDependencyUploadAdmissionAsync(owned,pending.Id).ConfigureAwait(false);
        await using var second=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader);
        var saved=await second.UploadAsync(next,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pending.Id,saved.Id);Assert.AreEqual(pending.Scope,saved.Scope);Assert.AreEqual(pending.FileId,saved.FileId);
        CollectionAssert.AreEqual(pending.Payload.ToArray(),saved.Payload.ToArray());Assert.IsNotNull(saved.VerifiedVersion);Assert.IsNull(second.Failure);
        Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.IsTrue(File.Exists(owned.Path));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDependencyUploadRefusesEveryWrongReadbackFieldWithoutChangingItsOriginalPendingRow()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;var mode=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {
            var value=await create(folder,file,mime,bytes,sha,token).ConfigureAwait(false);var changed=value.Bytes.ToArray();changed[0]^=1;
            return mode switch
            {
                0=>value with{AccountId="foreign-account"},1=>value with{FolderId="foreign-folder"},2=>value with{FileId="foreign-file"},
                3=>value with{MimeType="image/jpeg"},4=>value with{Bytes=value.Bytes[..^1]},5=>value with{Bytes=changed},6=>value with{DriveVersion=0},
                _=>throw new InvalidOperationException("Unknown dependency readback case.")
            };
        };
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var allocations=fixture.Data.Allocations;
        await using var owner=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader);
        for(mode=0;mode<7;mode++)
        {
            await Assert.ThrowsExactlyAsync<WorkspacePublicationConflictException>(()=>owner.UploadAsync(admission,CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(allocations,fixture.Data.Allocations);
            var row=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
            Assert.AreEqual(admission.Original.Dependency.FileId,row.FileId);Assert.IsNull(row.VerifiedVersion);CollectionAssert.AreEqual(owned.Payload,row.Payload.ToArray());
            Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyUploadJoinsUncooperativeActualCreateBeforeCancellationOrExitWithoutAdmittingLateReceipt(bool exit)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {token.ThrowIfCancellationRequested();entered.TrySetResult();await release.Task.ConfigureAwait(false);return await create(folder,file,mime,bytes,sha,CancellationToken.None).ConfigureAwait(false);};
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var disposals=fixture.Data.Setup.Accounts.Readers.DisposalAttempts;
        await using var owner=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader);using var caller=new CancellationTokenSource();
        var running=owner.UploadAsync(admission,caller.Token);var stopping=Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if(exit) stopping=owner.DisposeAsync().AsTask();else await caller.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted);Assert.IsFalse(owner.ExitConfirmed);if(exit) Assert.IsFalse(stopping.IsCompleted);
            Assert.AreEqual(disposals,fixture.Data.Setup.Accounts.Readers.DisposalAttempts);
        }
        finally {release.TrySetResult();try {await running.ConfigureAwait(false);} catch(OperationCanceledException) { }}
        await Assert.ThrowsAsync<OperationCanceledException>(()=>running).ConfigureAwait(false);await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));
        var pending=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
        Assert.AreEqual(admission.Original.Dependency.FileId,pending.FileId);Assert.IsNull(pending.VerifiedVersion);CollectionAssert.AreEqual(owned.Payload,pending.Payload.ToArray());
        if(exit) Assert.IsTrue(owner.ExitConfirmed);Assert.IsTrue(File.Exists(owned.Path));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyUploadRejectsForeignReaderOrNewCaptureBeforeAnyPhysicalCreate(bool stale)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);var creates=fixture.Data.Creates;
        await using var other=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,fixture.Data.Budget);
        if(stale) _=await fixture.Reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
        await using var owner=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,stale?fixture.Reader:other);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CurrentControlObservationConflictException>(()=>owner.UploadAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(creates,fixture.Data.Creates);
    }

    private static async Task<LocalCurrentCatalogDependencyCloudAdmission> PrepareDependencyUploadAdmissionAsync(CurrentDependencyFixture owned)
    {
        var initial=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);
        WorkspacePublicationRecord allocated;await using(var allocator=DependencyAllocator(owned))
            allocated=await allocator.AllocateAsync(initial,CancellationToken.None).ConfigureAwait(false);
        return await FreshDependencyUploadAdmissionAsync(owned,allocated.Id).ConfigureAwait(false);
    }

    private static async Task<LocalCurrentCatalogDependencyCloudAdmission> FreshDependencyUploadAdmissionAsync(CurrentDependencyFixture owned,Guid id)=>
        await owned.Fixture.Reader.InspectCatalogDependencyCloudAsync(await FreshLineageInventoryAsync(owned.Fixture).ConfigureAwait(false),id,CancellationToken.None).ConfigureAwait(false);
}
