using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderAllocationRetainsAnEnteredKnownIdAfterSourceOrPublisherAuthorityChanges(bool revoked)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);var calls=0;
        var creates=fixture.Data.Creates;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=async token=>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            if(revoked) _=await SignHeaderAllocationRevocationAsync(fixture).ConfigureAwait(false);
            else _=await SignLineageCurrentSourcesAsync(fixture).ConfigureAwait(false);
            return "known-header-before-authority-retired";
        };
        await using var owner=HeaderAllocator(owned);
        var saved=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Id,saved.Id);Assert.AreEqual(owned.Pending.Capture.Scope,saved.Scope);
        Assert.AreEqual("known-header-before-authority-retired",saved.FileId);Assert.IsNull(saved.VerifiedVersion);
        CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),saved.Payload.ToArray());
        Assert.AreEqual(1,calls);Assert.AreEqual(creates,fixture.Data.Creates);
        var inventory=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<StaleLibraryConfigurationException>(()=>fixture.Reader.InspectCatalogHeaderCloudAsync(
            inventory,owned.Pending.Capture.Scope.GenerationId,CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("metadata")]
    [DataRow("not a canonical id")]
    [DataRow("selected-anchor")]
    public async Task CurrentHeaderAllocationRejectsMalformedAndRoleAliasedReceiptsWithoutPoisoningExit(string returned)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);var calls=0;
        var creates=fixture.Data.Creates;var cloud=fixture.Data.Cloud.Count;
        var id=returned=="selected-anchor"?admission.Original.Identity.SelectedAnchor!.FileId:returned;
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult(id);};
        await using var owner=HeaderAllocator(owned);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.FileId);
        Assert.AreEqual(1,calls);Assert.AreEqual(creates,fixture.Data.Creates);Assert.AreEqual(cloud,fixture.Data.Cloud.Count);
        await owner.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(owner.ExitConfirmed);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentHeaderAllocationRollsBackARewrittenReceiptAndRetriesOnlyItsFirstKnownProviderId()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(true).ConfigureAwait(false);var fixture=owned.Fixture;
        var admission=await PrepareHeaderAllocationAdmissionAsync(owned).ConfigureAwait(false);var calls=0;var creates=fixture.Data.Creates;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            CurrentStageSql(database,"CREATE TRIGGER corrupt_header_id AFTER UPDATE OF file_id ON workspace_publication WHEN NEW.role='catalog-commit' BEGIN UPDATE workspace_publication SET file_id='unexpected-rewritten-header' WHERE id=NEW.id; END");return true;
        },CancellationToken.None).ConfigureAwait(false);
        fixture.Data.Setup.Accounts.Readers.WorkspaceAllocate=_=>{calls++;return Task.FromResult("first-known-header-after-sql-failure");};
        await using var owner=HeaderAllocator(owned);
        await Assert.ThrowsExactlyAsync<WorkspacePublicationConflictException>(()=>owner.AllocateAsync(admission,CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull((await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false))!.FileId);Assert.IsNotNull(owner.Failure);
        Assert.AreEqual(1,calls);Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER corrupt_header_id");return true;},CancellationToken.None).ConfigureAwait(false);
        var saved=await owner.AllocateAsync(admission,CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("first-known-header-after-sql-failure",saved.FileId);Assert.AreEqual(owned.Original.Id,saved.Id);
        Assert.IsNull(saved.VerifiedVersion);Assert.IsNull(owner.Failure);Assert.AreEqual(1,calls);Assert.AreEqual(creates,fixture.Data.Creates);
        CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),saved.Payload.ToArray());
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));Assert.IsTrue(File.Exists(owned.Pending.Path));
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }
}
