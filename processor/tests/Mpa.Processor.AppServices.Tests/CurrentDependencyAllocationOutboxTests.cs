using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    public async Task CurrentDependencyAllocationRollsBackUnrelatedOutboxRetargetingAndRetainsOnlyItsOriginalKnownId()
    {
        await using var owned=await CurrentDependencyFixture.CreateAsync().ConfigureAwait(false);var fixture=owned.Fixture;
        var scope=owned.Basis.Publication.Capture.Scope;
        var bytes=new CatalogIndexDocumentWriter(new(scope.AccountId,scope.FolderId)).Encode("",[]);
        var other=await fixture.Reader.PrepareCurrentDependencyAsync(owned.Generation,"facts-index","application/json",bytes,CancellationToken.None).ConfigureAwait(false);
        var admission=await PrepareDependencyAllocationAdmissionAsync(owned).ConfigureAwait(false);
        Assert.IsNull(other.FileId);Assert.AreNotEqual(other.Id,admission.Original.Dependency.Id);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,"CREATE TRIGGER retarget_unrelated_dependency AFTER UPDATE OF file_id ON workspace_publication WHEN NEW.id='"+admission.Original.Dependency.Id.ToString("D")+
                "' BEGIN UPDATE workspace_publication SET file_id='unexpected-unrelated-source-id' WHERE id='"+other.Id.ToString("D")+"'; END");return true;
        },CancellationToken.None).ConfigureAwait(false);
        var before=await CurrentBirthStateAsync(fixture).ConfigureAwait(false);var calls=0;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("dependency-known-with-unrelated-rollback");};
        await using var owner=DependencyAllocator(owned);
        try
        {
            await Assert.ThrowsExactlyAsync<WorkspacePublicationInventoryChangedException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(before,await CurrentBirthStateAsync(fixture).ConfigureAwait(false));Assert.AreEqual(1,calls);Assert.IsNotNull(owner.Failure);
            Assert.IsNull((await ReadAllocatedDependencyAsync(owned,admission.Original.Dependency.Id).ConfigureAwait(false)).FileId);
        }
        finally {await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER retarget_unrelated_dependency");return true;},CancellationToken.None).ConfigureAwait(false);}
        var saved=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        AssertDependencyAllocation(owned,admission.Original.Dependency,saved,"dependency-known-with-unrelated-rollback");Assert.AreEqual(1,calls);Assert.IsNull(owner.Failure);
        var retained=await ReadAllocatedDependencyAsync(owned,other.Id).ConfigureAwait(false);
        Assert.AreEqual(other.Id,retained.Id);Assert.AreEqual(other.Scope,retained.Scope);Assert.AreEqual(other.FileId,retained.FileId);Assert.AreEqual(other.VerifiedVersion,retained.VerifiedVersion);
        Assert.AreEqual(other.Sha256,retained.Sha256);CollectionAssert.AreEqual(other.Payload.ToArray(),retained.Payload.ToArray());
        Assert.IsTrue(File.Exists(owned.Path));Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
