using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentHeaderPreparationRecoversOriginalMissingPendingBeforeCompleteInventory(bool sealedBeforeCrash)
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(sealedBeforeCrash).ConfigureAwait(false);
        var fixture=owned.Fixture;
        await using var preparation=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false);
        var cloud=fixture.Data.Cloud.Keys.OrderBy(value=>value,StringComparer.Ordinal).ToArray();
        var allocations=fixture.Data.Allocations;var puts=fixture.Http.Puts;
        var snapshot=SHA256.HashData(await File.ReadAllBytesAsync(owned.Pending.Path).ConfigureAwait(false));
        var before=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibrarySourceRepository(database).Snapshot(),CancellationToken.None).ConfigureAwait(false);
        var pending=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Id,pending.Id);Assert.AreEqual(owned.Pending.Capture.Scope,pending.Scope);
        Assert.IsNull(pending.FileId);Assert.IsNull(pending.VerifiedVersion);
        var original=await ReadRecoveredHeaderIntentAsync(owned).ConfigureAwait(false);
        Assert.AreEqual(owned.Original.Sequence,original.Sequence);Assert.AreEqual(owned.Original.BodySha256,original.BodySha256);
        CollectionAssert.AreEqual(original.Envelope!.Value.ToArray(),pending.Payload.ToArray());
        if(sealedBeforeCrash) CollectionAssert.AreEqual(owned.Original.Envelope!.Value.ToArray(),pending.Payload.ToArray());
        SameFreshHeader(pending,await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual("commit-sealed",(await ReadCompletionCaptureAsync(fixture.Data,pending.Scope.GenerationId).ConfigureAwait(false)).Phase);
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        var after=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>new LibrarySourceRepository(database).Snapshot(),CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(before.Revision,after.Revision);CollectionAssert.AreEqual(before.Sources.ToArray(),after.Sources.ToArray());
        CollectionAssert.AreEqual(snapshot,SHA256.HashData(await File.ReadAllBytesAsync(owned.Pending.Path).ConfigureAwait(false)));
        CollectionAssert.AreEqual(cloud,fixture.Data.Cloud.Keys.OrderBy(value=>value,StringComparer.Ordinal).ToArray());
        Assert.AreEqual(allocations,fixture.Data.Allocations);Assert.AreEqual(puts,fixture.Http.Puts);
        Assert.IsFalse(fixture.Http.BodyFiles.Contains("read-only-original"));
        // Local recovery only makes the genuine complete inventory possible.
        // It must not grant permission to prepare new metadata in the old scope.
        _=await FreshLineageInventoryAsync(fixture).ConfigureAwait(false);
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {
            Assert.ThrowsExactly<StaleLibraryConfigurationException>(()=>new WorkspacePublicationRepository(database,fixture.Data.Budget)
                .Prepare(Guid.NewGuid(),pending.Scope,"facts-index","application/json","{}"u8));
            return true;
        },CancellationToken.None).ConfigureAwait(false);
        await preparation.DisposeAsync().ConfigureAwait(false);Assert.IsTrue(preparation.ExitConfirmed);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);
    }

    [TestMethod]
    public async Task CurrentHeaderPreparationLocalRecoveryFailureRetainsItsFirstSignatureAndOriginalSnapshot()
    {
        await using var owned=await HeaderRecoveryFixture.CreateAsync(false).ConfigureAwait(false);var fixture=owned.Fixture;
        await using var preparation=await NewCurrentFreshHeaderOwnerAsync(fixture).ConfigureAwait(false);
        var original=await File.ReadAllBytesAsync(owned.Pending.Path).ConfigureAwait(false);
        var cloud=fixture.Data.Cloud.Count;var puts=fixture.Http.Puts;var allocations=fixture.Data.Allocations;
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
        {CurrentStageSql(database,"CREATE TRIGGER fail_composed_header_pending BEFORE INSERT ON workspace_publication WHEN NEW.role='catalog-commit' BEGIN SELECT RAISE(ABORT,'composed header recovery'); END");return true;},CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SqliteException>(()=>preparation.PrepareAsync(CancellationToken.None)).ConfigureAwait(false);
        var sealedIntent=await ReadRecoveredHeaderIntentAsync(owned).ConfigureAwait(false);Assert.IsNotNull(sealedIntent.Envelope);
        Assert.AreEqual(owned.Original.Sequence,sealedIntent.Sequence);Assert.AreEqual(owned.Original.BodySha256,sealedIntent.BodySha256);
        Assert.IsNull(await ReadRecoveredHeaderRowAsync(owned).ConfigureAwait(false));
        CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(owned.Pending.Path).ConfigureAwait(false));
        Assert.AreEqual(owned.Head,await LineageHeadAsync(fixture).ConfigureAwait(false));
        await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>{CurrentStageSql(database,"DROP TRIGGER fail_composed_header_pending");return true;},CancellationToken.None).ConfigureAwait(false);
        var pending=await preparation.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(sealedIntent.Envelope!.Value.ToArray(),pending.Payload.ToArray());
        Assert.AreEqual(cloud,fixture.Data.Cloud.Count);Assert.AreEqual(puts,fixture.Http.Puts);Assert.AreEqual(allocations,fixture.Data.Allocations);
        Assert.AreEqual(0L,fixture.Data.Budget.Status().ReservedBytes);Assert.IsNull(preparation.Failure);
    }
}
