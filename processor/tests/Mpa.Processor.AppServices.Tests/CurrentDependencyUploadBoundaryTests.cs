using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDependencyUploadRefusesReceiptAfterHumanSourceOrPublisherChangesDuringTheEnteredCreate(bool revoke)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {
            var value=await create(folder,file,mime,bytes,sha,token).ConfigureAwait(false);
            if(revoke) _=await SignHeaderAllocationRevocationAsync(fixture).ConfigureAwait(false);
            else await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            {
                var sources=new LibrarySourceRepository(database);var human=sources.Snapshot();var source=human.Sources.Single();
                sources.Update(source.Id,false,source.IncludeDescendants,source.Revision,human.Revision);return true;
            },CancellationToken.None).ConfigureAwait(false);
            return value;
        };
        var head=await LineageHeadAsync(fixture).ConfigureAwait(false);var allocations=fixture.Data.Allocations;
        await using var owner=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader);
        Task<WorkspacePublicationRecord> Run()=>owner.UploadAsync(admission,CancellationToken.None);
        if(revoke) await Assert.ThrowsExactlyAsync<CatalogPublicationNotAuthorizedException>(Run).ConfigureAwait(false);
        else await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(Run).ConfigureAwait(false);
        var pending=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
        Assert.AreEqual(admission.Original.Dependency.FileId,pending.FileId);Assert.IsNull(pending.VerifiedVersion);CollectionAssert.AreEqual(owned.Payload,pending.Payload.ToArray());
        Assert.IsTrue(fixture.Data.Cloud.ContainsKey(pending.FileId!));Assert.AreEqual(allocations,fixture.Data.Allocations);
        Assert.AreEqual(head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Path));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CurrentDependencyUploadReceiptRollsBackSqlFailureReceiptRewriteHumanDriftAndUnrelatedObjectMutation(int mode)
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;var scope=owned.Basis.Publication.Capture.Scope;
        var index=new CatalogIndexDocumentWriter(new(scope.AccountId,scope.FolderId)).Encode("",[]);
        var other=await fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"facts-index","application/json",index,CancellationToken.None).ConfigureAwait(false);
        var admission=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            var target=admission.Original.Dependency.Id.ToString("D");
            CurrentStageSql(database,mode switch
            {
                0=>"CREATE TRIGGER reject_current_dependency_receipt BEFORE UPDATE OF verified_version ON workspace_publication WHEN NEW.id='"+target+"' BEGIN SELECT RAISE(ABORT,'dependency receipt fixture'); END",
                1=>"CREATE TRIGGER reject_current_dependency_receipt AFTER UPDATE OF verified_version ON workspace_publication WHEN NEW.id='"+target+"' BEGIN UPDATE workspace_publication SET verified_version=NEW.verified_version+1 WHERE id=NEW.id; END",
                2=>"CREATE TRIGGER reject_current_dependency_receipt AFTER UPDATE OF verified_version ON workspace_publication WHEN NEW.id='"+target+"' BEGIN UPDATE library_configuration SET revision=revision+1 WHERE id=1; END",
                3=>"CREATE TRIGGER reject_current_dependency_receipt AFTER UPDATE OF verified_version ON workspace_publication WHEN NEW.id='"+target+"' BEGIN UPDATE workspace_publication SET file_id='unexpected-unrelated-receipt-object' WHERE id='"+other.Id.ToString("D")+"'; END",
                _=>throw new ArgumentOutOfRangeException(nameof(mode))
            });return true;
        },CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var allocations=fixture.Data.Allocations;
        await using var owner=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,fixture.Reader);
        try
        {
            Task<WorkspacePublicationRecord> Run()=>owner.UploadAsync(admission,CancellationToken.None);
            if(mode==0) await Assert.ThrowsExactlyAsync<SqliteException>(Run).ConfigureAwait(false);
            else if(mode==1) await Assert.ThrowsExactlyAsync<WorkspacePublicationConflictException>(Run).ConfigureAwait(false);
            else if(mode==2) await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(Run).ConfigureAwait(false);
            else await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(Run).ConfigureAwait(false);
            Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(allocations,fixture.Data.Allocations);
            var pending=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
            Assert.IsNull(pending.VerifiedVersion);Assert.AreEqual(admission.Original.Dependency.FileId,pending.FileId);Assert.IsNotNull(owner.Failure);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER reject_current_dependency_receipt");return true;},CancellationToken.None).ConfigureAwait(false);}
        var fresh=await FreshDependencyUploadAdmissionAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
        var saved=await owner.UploadAsync(fresh,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(admission.Original.Dependency.Id,saved.Id);Assert.AreEqual(admission.Original.Dependency.FileId,saved.FileId);
        Assert.AreEqual(owned.Basis.Publication.Capture.Scope,saved.Scope);Assert.IsNotNull(saved.VerifiedVersion);CollectionAssert.AreEqual(owned.Payload,saved.Payload.ToArray());
        Assert.IsNull(owner.Failure);Assert.AreEqual(allocations,fixture.Data.Allocations);
        var preserved=await ReadAllocatedDependencyAsync(owned,other.Id).ConfigureAwait(false);Assert.IsNull(preserved.FileId);Assert.IsNull(preserved.VerifiedVersion);
        CollectionAssert.AreEqual(other.Payload.ToArray(),preserved.Payload.ToArray());Assert.IsTrue(File.Exists(owned.Path));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentDependencyUploadLowStorageLeavesOnlyAnAssignedUnverifiedRetryAndRecoversFromActualFreshInventory()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var allocated=await PrepareDependencyUploadAdmissionAsync(owned).ConfigureAwait(false);
        var space=new CurrentAllocationSpace();var budget=new StorageBudgetManager(space);
        await using var reader=new LocalCurrentControlReader(fixture.Data.Setup.Accounts.State,fixture.Data.Setup.Accounts.Service,fixture.Data.Setup.Keys,budget);
        var capture=await reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var inventory=await reader.ObserveWorkspaceInventoryAsync(capture,CancellationToken.None).ConfigureAwait(false);
        var admission=await reader.InspectCatalogDependencyCloudAsync(inventory,allocated.Original.Dependency.Id,CancellationToken.None).ConfigureAwait(false);
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;var allocations=fixture.Data.Allocations;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,bytes,sha,token)=>
        {var value=await create(folder,file,mime,bytes,sha,token).ConfigureAwait(false);space.Available=1;return value;};
        await using var owner=new CurrentCatalogDependencyUploadService(fixture.Data.Setup.Accounts.Service,reader);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);
        try
        {
            await Assert.ThrowsExactlyAsync<InsufficientStorageBudgetException>(()=>owner.UploadAsync(admission,CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(0L,budget.Status().ReservedBytes);
            var pending=await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false);
            Assert.AreEqual(admission.Original.Dependency.FileId,pending.FileId);Assert.IsNull(pending.VerifiedVersion);
        }
        finally {space.Available=20*StorageBudgetManager.Gibibyte;fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=create;}
        var nextCapture=await reader.CaptureAsync(CancellationToken.None).ConfigureAwait(false);var nextInventory=await reader.ObserveWorkspaceInventoryAsync(nextCapture,CancellationToken.None).ConfigureAwait(false);
        var next=await reader.InspectCatalogDependencyCloudAsync(nextInventory,admission.Original.Dependency.Id,CancellationToken.None).ConfigureAwait(false);
        var saved=await owner.UploadAsync(next,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(admission.Original.Dependency.FileId,saved.FileId);Assert.IsNotNull(saved.VerifiedVersion);Assert.IsNull(owner.Failure);
        Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.AreEqual(0L,budget.Status().ReservedBytes);Assert.IsTrue(File.Exists(owned.Path));
    }
}
